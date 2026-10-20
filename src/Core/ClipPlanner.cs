using System;
using System.Collections.Generic;
using System.Text;

namespace CrashCatcher.Core
{
    public enum ClipStatus { Pending, Pressed, Linked, Saved, NotFound, Failed }

    /// <summary>Одно сохранение клипа (одно нажатие Alt+F10) и ошибки, ради которых оно делается.</summary>
    public sealed class ClipJob
    {
        public int Id;
        public DateTime Created, ScheduledAt;
        public DateTime? PressedAt;
        public bool Manual;
        public readonly List<ErrorRecord> Records = new List<ErrorRecord>();
        public DateTime FirstErrorTime;
        public ClipStatus Status;
        public string FilePath;
        public string Note;

        public string Tag
        {
            get
            {
                if (Records.Count == 0) return Manual ? "MANUAL" : "CLIP";
                var tags = new List<string>();
                foreach (var r in Records) if (!tags.Contains(r.Abbr)) tags.Add(r.Abbr);
                var t = tags[0];
                if (tags.Count > 1) t += "+" + (tags.Count - 1);
                return t;
            }
        }

        public string StatusText
        {
            get
            {
                switch (Status)
                {
                    case ClipStatus.Pending: return "ждёт сохранения";
                    case ClipStatus.Pressed: return "сохраняется…";
                    case ClipStatus.Linked: return "сохранён";
                    case ClipStatus.Saved: return "отправлен в NVIDIA";
                    case ClipStatus.NotFound: return "файл не найден";
                    default: return "ошибка";
                }
            }
        }
    }

    /// <summary>
    /// Решает, когда нажимать Alt+F10:
    /// задержка после ошибки, не чаще раза в CooldownSec, повтор той же ошибки — только после паузы RepeatGapSec.
    /// Ошибки, случившиеся пока клип ждёт сохранения, попадают в тот же клип.
    /// </summary>
    public sealed class ClipPlanner
    {
        public bool AutoSave = true;
        public double DelaySec = 3, CooldownSec = 60, RepeatGapSec = 300;
        public const double ImmediateGapSec = 5;

        public ClipJob Pending { get; private set; }
        public DateTime LastPress = DateTime.MinValue;
        int _nextId = 1;

        public void Apply(AppSettings s)
        {
            AutoSave = s.AutoSave;
            DelaySec = s.SaveDelaySec;
            CooldownSec = s.CooldownSec;
            RepeatGapSec = s.RepeatGapSec;
        }

        /// <summary>Вызывается на каждое срабатывание. Возвращает клип, если ошибка в него попала.</summary>
        public ClipJob Consider(ErrorRecord rec, bool isNew, DateTime prevSeen, DateTime now)
        {
            if (!AutoSave || rec == null || rec.IsInfo || rec.Rule == null || !rec.Rule.SaveClip) return null;

            // уже ждёт в текущем клипе
            if (Pending != null && rec.Clip == Pending) return null;

            bool episode = isNew || rec.Clip == null
                || prevSeen == DateTime.MinValue
                || (now - prevSeen).TotalSeconds >= RepeatGapSec;
            if (!episode) return null;

            bool immediate = rec.Rule.Immediate;
            if (Pending == null)
            {
                Pending = new ClipJob
                {
                    Id = _nextId++, Created = now, FirstErrorTime = now,
                    ScheduledAt = Earliest(now, immediate), Status = ClipStatus.Pending,
                };
            }
            else if (immediate)
            {
                var t = Earliest(now, true);
                if (t < Pending.ScheduledAt) Pending.ScheduledAt = t;
            }
            Pending.Records.Add(rec);
            rec.Clip = Pending;
            rec.Version++;
            return Pending;
        }

        DateTime Earliest(DateTime now, bool immediate)
        {
            var a = immediate ? now : now.AddSeconds(DelaySec);
            var b = LastPress == DateTime.MinValue ? DateTime.MinValue : LastPress.AddSeconds(immediate ? ImmediateGapSec : CooldownSec);
            return a > b ? a : b;
        }

        /// <summary>Клип, который пора сохранить (или null).</summary>
        public ClipJob TakeDue(DateTime now)
        {
            if (Pending == null || now < Pending.ScheduledAt) return null;
            var j = Pending;
            Pending = null;
            j.PressedAt = now;
            j.Status = ClipStatus.Pressed;
            LastPress = now;
            return j;
        }

        /// <summary>Ручное сохранение: сразу (с учётом короткого интервала между нажатиями).</summary>
        public ClipJob Manual(DateTime now, double delaySec)
        {
            var at = now.AddSeconds(delaySec);
            var gap = LastPress == DateTime.MinValue ? DateTime.MinValue : LastPress.AddSeconds(ImmediateGapSec);
            if (gap > at) at = gap;
            if (Pending != null)
            {
                Pending.Manual = true;
                if (at < Pending.ScheduledAt) Pending.ScheduledAt = at;
                return Pending;
            }
            Pending = new ClipJob { Id = _nextId++, Created = now, FirstErrorTime = now, ScheduledAt = at, Manual = true, Status = ClipStatus.Pending };
            return Pending;
        }

        public void CancelPending()
        {
            if (Pending == null) return;
            foreach (var r in Pending.Records) { if (r.Clip == Pending) r.Clip = null; r.Version++; }
            Pending = null;
        }
    }

    public static class ClipText
    {
        /// <summary>Текст .txt рядом с клипом: ошибки клипа + всё остальное за время буфера.</summary>
        public static string Build(ClipJob job, IList<ErrorRecord> window, string logPath, double replaySec)
        {
            var sb = new StringBuilder();
            var pressed = job.PressedAt ?? DateTime.Now;
            sb.AppendLine("Crash Catcher — клип #" + job.Id + (job.Manual && job.Records.Count == 0 ? " (вручную)" : ""));
            sb.AppendLine("Клип сохранён: " + pressed.ToString("yyyy-MM-dd HH:mm:ss") +
                          "  (охватывает примерно " + pressed.AddSeconds(-replaySec).ToString("HH:mm:ss") + " – " + pressed.ToString("HH:mm:ss") + ")");
            if (job.Records.Count > 0) sb.AppendLine("Первая ошибка:  " + job.FirstErrorTime.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("Лог: " + logPath);
            sb.AppendLine();

            if (job.Records.Count > 0)
            {
                sb.AppendLine("================ Ошибки, из-за которых сохранён клип ================");
                foreach (var r in job.Records) AppendRecord(sb, r);
            }

            var others = new List<ErrorRecord>();
            foreach (var r in window) if (!job.Records.Contains(r)) others.Add(r);
            if (others.Count > 0)
            {
                sb.AppendLine("================ Остальное за это время ================");
                foreach (var r in others) AppendRecord(sb, r);
            }
            return sb.ToString();
        }

        static void AppendRecord(StringBuilder sb, ErrorRecord r)
        {
            sb.Append('[').Append(r.FirstSeen.ToString("HH:mm:ss")).Append("] ");
            if (r.Count > 1) sb.Append("×").Append(r.Count).Append(" (последний раз ").Append(r.LastSeen.ToString("HH:mm:ss")).Append(") ");
            sb.AppendLine();
            sb.AppendLine(r.FullText());
            sb.AppendLine();
        }
    }
}
