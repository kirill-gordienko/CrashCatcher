using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace CrashCatcher.Core
{
    public sealed class LinkResult
    {
        public bool Found;
        public string OriginalPath;
        public string FinalPath;
        public string TextPath;
        public string Error;
    }

    /// <summary>
    /// Находит ролик, который NVIDIA сохранила после нажатия, ждёт окончания записи,
    /// переименовывает его по ошибке и кладёт рядом .txt со стеком.
    /// </summary>
    public sealed class ClipLinker
    {
        static readonly HashSet<string> Claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static readonly string[] Extensions = { ".mp4", ".mkv", ".mov" };

        public int MaxDepth = 4;
        public int PollMs = 1000;
        public double FinishTimeoutSec = 180;
        public Func<DateTime> UtcNow = () => DateTime.UtcNow;

        /// <summary>Блокирующий вызов — запускать в фоне.</summary>
        public LinkResult Run(string root, DateTime pressedUtc, double waitSec, string baseName, string sidecarText,
                              CancellationToken ct, Action<string> progress)
        {
            var res = new LinkResult();
            try
            {
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
                {
                    res.Error = "папка клипов не найдена: " + root;
                    return res;
                }

                var threshold = pressedUtc.AddSeconds(-3);
                var deadline = UtcNow().AddSeconds(waitSec);
                string found = null;
                while (!ct.IsCancellationRequested)
                {
                    found = FindCandidate(root, threshold);
                    if (found != null) break;
                    if (UtcNow() >= deadline) break;
                    Sleep(ct);
                }
                if (found == null)
                {
                    res.Error = "новый ролик не появился за " + (int)waitSec + " с";
                    return res;
                }
                res.Found = true;
                res.OriginalPath = found;
                if (progress != null) progress("NVIDIA пишет файл: " + Path.GetFileName(found));

                // ждём, пока NVIDIA допишет файл: размер не меняется и файл открывается монопольно
                var finishBy = UtcNow().AddSeconds(FinishTimeoutSec);
                long last = -1;
                bool done = false;
                while (!ct.IsCancellationRequested && UtcNow() < finishBy)
                {
                    long size = -1;
                    try
                    {
                        using (var fs = new FileStream(found, FileMode.Open, FileAccess.Read, FileShare.None))
                            size = fs.Length;
                    }
                    catch (IOException) { size = -1; }
                    catch (UnauthorizedAccessException) { size = -1; }
                    if (size > 0 && size == last) { done = true; break; }
                    last = size;
                    Sleep(ct);
                }
                if (!done) { res.Error = "NVIDIA не закончила запись файла"; res.FinalPath = found; return res; }

                var dir = Path.GetDirectoryName(found);
                var ext = Path.GetExtension(found);
                var target = Unique(dir, Sanitize(baseName), ext);
                Exception lastEx = null;
                for (int i = 0; i < 30 && !ct.IsCancellationRequested; i++)
                {
                    try { File.Move(found, target); lastEx = null; break; }
                    catch (IOException ex) { lastEx = ex; }
                    catch (UnauthorizedAccessException ex) { lastEx = ex; }
                    Sleep(ct);
                }
                if (lastEx != null)
                {
                    res.Error = "не удалось переименовать: " + lastEx.Message;
                    target = found;
                }
                res.FinalPath = target;
                lock (Claimed) Claimed.Add(target);

                if (sidecarText != null)
                {
                    var txt = Path.Combine(dir, Path.GetFileNameWithoutExtension(target) + ".txt");
                    File.WriteAllText(txt, sidecarText, new UTF8Encoding(true));
                    res.TextPath = txt;
                }
                return res;
            }
            catch (Exception ex)
            {
                res.Error = ex.Message;
                return res;
            }
        }

        void Sleep(CancellationToken ct)
        {
            ct.WaitHandle.WaitOne(PollMs);
        }

        /// <summary>Самый ранний новый ролик (создан после threshold), который ещё никому не достался.</summary>
        public string FindCandidate(string root, DateTime thresholdUtc)
        {
            string best = null;
            DateTime bestTime = DateTime.MaxValue;
            foreach (var dir in Dirs(root, 0))
            {
                DateTime dirWrite;
                try { dirWrite = Directory.GetLastWriteTimeUtc(dir); } catch { continue; }
                if (dirWrite < thresholdUtc) continue;
                string[] files;
                try { files = Directory.GetFiles(dir); } catch { continue; }
                foreach (var f in files)
                {
                    var ext = Path.GetExtension(f);
                    if (Array.FindIndex(Extensions, e => string.Equals(e, ext, StringComparison.OrdinalIgnoreCase)) < 0) continue;
                    DateTime created;
                    try
                    {
                        created = File.GetCreationTimeUtc(f);
                        var written = File.GetLastWriteTimeUtc(f);
                        // на некоторых ФС время создания недоступно — берём более позднее
                        if (written > created && created < thresholdUtc && written >= thresholdUtc && !IsWindows) created = written;
                    }
                    catch { continue; }
                    if (created < thresholdUtc) continue;
                    lock (Claimed) { if (Claimed.Contains(f)) continue; }
                    if (created < bestTime) { best = f; bestTime = created; }
                }
            }
            if (best != null) lock (Claimed) Claimed.Add(best);
            return best;
        }

        static readonly bool IsWindows = Environment.OSVersion.Platform == PlatformID.Win32NT;

        IEnumerable<string> Dirs(string dir, int depth)
        {
            yield return dir;
            if (depth >= MaxDepth) yield break;
            string[] subs;
            try { subs = Directory.GetDirectories(dir); } catch { yield break; }
            foreach (var s in subs)
            {
                FileAttributes a;
                try { a = File.GetAttributes(s); } catch { continue; }
                if ((a & FileAttributes.ReparsePoint) != 0 || (a & FileAttributes.Hidden) != 0) continue;
                foreach (var d in Dirs(s, depth + 1)) yield return d;
            }
        }

        static string Unique(string dir, string baseName, string ext)
        {
            var p = Path.Combine(dir, baseName + ext);
            int i = 2;
            while (File.Exists(p) || File.Exists(Path.ChangeExtension(p, ".txt")))
                p = Path.Combine(dir, baseName + "_" + (i++) + ext);
            return p;
        }

        public static string Sanitize(string name)
        {
            var bad = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder();
            foreach (var ch in name) sb.Append(Array.IndexOf(bad, ch) >= 0 || ch == ':' || ch == '\\' || ch == '/' ? '_' : ch);
            var s = sb.ToString().Trim();
            return s.Length == 0 ? "clip" : s;
        }
    }

    /// <summary>Журнал тулзы: history\history_ГГГГ-ММ-ДД.log</summary>
    public sealed class History
    {
        readonly string _dir;
        readonly object _lock = new object();
        public string Directory { get { return _dir; } }

        public History(string dir) { _dir = dir; }

        public void Write(string text)
        {
            try
            {
                lock (_lock)
                {
                    System.IO.Directory.CreateDirectory(_dir);
                    var now = DateTime.Now;
                    var path = Path.Combine(_dir, "history_" + now.ToString("yyyy-MM-dd") + ".log");
                    var prefix = now.ToString("HH:mm:ss.fff") + "  ";
                    var body = text.Replace("\r\n", "\n").Replace("\n", Environment.NewLine + new string(' ', prefix.Length));
                    File.AppendAllText(path, prefix + body + Environment.NewLine, new UTF8Encoding(false));
                }
            }
            catch { }
        }
    }
}
