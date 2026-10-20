using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace CrashCatcher.Core
{
    public enum InfoKind { Normal, Good, Warn, Bad }

    /// <summary>Строка консоли: сгруппированная ошибка или служебное сообщение.</summary>
    public sealed class ErrorRecord
    {
        static long _ids;
        public readonly long Id = System.Threading.Interlocked.Increment(ref _ids);

        public string Signature;
        public RuleConfig Rule;
        public string Type, Abbr, Message;
        public List<string> Stack = new List<string>();
        public int DroppedLines;
        public int Count;
        public DateTime FirstSeen, LastSeen;
        public ClipJob Clip;

        public bool IsInfo;
        public InfoKind Kind;

        // состояние интерфейса
        public bool Expanded;
        public int Version;

        public string FullText()
        {
            var sb = new StringBuilder();
            if (IsInfo) { sb.Append(FirstSeen.ToString("HH:mm:ss")).Append("  ").Append(Message); return sb.ToString(); }
            sb.Append(Type);
            if (!string.IsNullOrEmpty(Message) && Message != Type) sb.Append(": ").Append(Message);
            sb.AppendLine();
            foreach (var s in Stack) sb.AppendLine(s);
            if (DroppedLines > 0) sb.AppendLine("… ещё " + DroppedLines + " строк(и) в логе");
            return sb.ToString().TrimEnd();
        }
    }

    /// <summary>Группирует одинаковые ошибки: NRE каждый кадр — одна строка со счётчиком.</summary>
    public sealed class ErrorBook
    {
        readonly Dictionary<string, ErrorRecord> _bySig = new Dictionary<string, ErrorRecord>();
        readonly List<ErrorRecord> _items = new List<ErrorRecord>();
        public int MaxItems = 500;
        public int Version { get; private set; }

        public IList<ErrorRecord> Items { get { return _items; } }

        static readonly Regex Digits = new Regex(@"\d+", RegexOptions.Compiled);
        static readonly Regex Spaces = new Regex(@"\s+", RegexOptions.Compiled);

        public static string Normalize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = Digits.Replace(s, "#");
            s = Spaces.Replace(s, " ").Trim();
            return s.Length > 300 ? s.Substring(0, 300) : s;
        }

        static string FirstUserFrame(List<string> stack)
        {
            foreach (var raw in stack)
            {
                var s = raw.Trim();
                if (s.Length == 0) continue;
                if (s.IndexOf("UnityEngine.Debug", StringComparison.Ordinal) >= 0) continue;
                if (s.IndexOf("UnityEngine.Logger", StringComparison.Ordinal) >= 0) continue;
                if (s.IndexOf("DebugLogHandler", StringComparison.Ordinal) >= 0) continue;
                return s;
            }
            return "";
        }

        public static string SignatureOf(ParsedEntry e)
        {
            var key = (e.Rule != null ? e.Rule.Name : "") + "|" + e.Type;
            if (e.Rule != null && e.Rule.Group) return key;
            return key + "|" + Normalize(e.Message) + "|" + Normalize(FirstUserFrame(e.Stack));
        }

        /// <summary>Добавляет срабатывание. prevSeen — когда эта же ошибка была в прошлый раз (MinValue, если впервые).</summary>
        public ErrorRecord Add(ParsedEntry e, out bool isNew, out DateTime prevSeen)
        {
            var sig = SignatureOf(e);
            ErrorRecord r;
            if (_bySig.TryGetValue(sig, out r))
            {
                isNew = false;
                prevSeen = r.LastSeen;
                r.Count++;
                r.LastSeen = e.Time;
                if (e.Rule != null && e.Rule.Group && e.Stack.Count > 0 && r.Stack.Count < 600)
                {
                    r.Stack.Add("—— " + e.Message + " ——");
                    r.Stack.AddRange(e.Stack);
                    r.DroppedLines += e.DroppedLines;
                }
                r.Version++;
            }
            else
            {
                isNew = true;
                prevSeen = DateTime.MinValue;
                r = new ErrorRecord
                {
                    Signature = sig, Rule = e.Rule, Type = e.Type, Abbr = e.Abbr, Message = e.Message,
                    Stack = new List<string>(e.Stack), DroppedLines = e.DroppedLines,
                    Count = 1, FirstSeen = e.Time, LastSeen = e.Time,
                };
                _bySig[sig] = r;
                _items.Add(r);
                Trim();
            }
            Version++;
            return r;
        }

        public ErrorRecord AddInfo(string text, InfoKind kind, DateTime now)
        {
            var r = new ErrorRecord { IsInfo = true, Kind = kind, Message = text, FirstSeen = now, LastSeen = now, Count = 1 };
            _items.Add(r);
            Trim();
            Version++;
            return r;
        }

        public void Touch() { Version++; }

        public void Clear()
        {
            _items.Clear();
            _bySig.Clear();
            Version++;
        }

        void Trim()
        {
            while (_items.Count > MaxItems)
            {
                var old = _items[0];
                _items.RemoveAt(0);
                if (!old.IsInfo && old.Signature != null)
                {
                    ErrorRecord cur;
                    if (_bySig.TryGetValue(old.Signature, out cur) && cur == old) _bySig.Remove(old.Signature);
                }
            }
        }

        /// <summary>Ошибки (не служебные), которые встречались начиная с момента t.</summary>
        public List<ErrorRecord> SeenSince(DateTime t)
        {
            var list = new List<ErrorRecord>();
            foreach (var r in _items) if (!r.IsInfo && r.LastSeen >= t) list.Add(r);
            return list;
        }

        /// <summary>Счётчики по типам для заголовка: NRE 12, MRE 1 ...</summary>
        public List<KeyValuePair<string, int>> Totals(out Dictionary<string, string> colors)
        {
            var dict = new Dictionary<string, int>();
            colors = new Dictionary<string, string>();
            var order = new List<string>();
            foreach (var r in _items)
            {
                if (r.IsInfo) continue;
                int c;
                if (!dict.TryGetValue(r.Abbr, out c)) { order.Add(r.Abbr); colors[r.Abbr] = r.Rule != null ? r.Rule.Color : null; }
                dict[r.Abbr] = c + r.Count;
            }
            var res = new List<KeyValuePair<string, int>>();
            foreach (var k in order) res.Add(new KeyValuePair<string, int>(k, dict[k]));
            res.Sort((a, b) => b.Value.CompareTo(a.Value));
            return res;
        }
    }
}
