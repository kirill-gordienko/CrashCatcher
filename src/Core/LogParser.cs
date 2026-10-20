using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace CrashCatcher.Core
{
    /// <summary>Одна распознанная ошибка из лога (одно срабатывание).</summary>
    public sealed class ParsedEntry
    {
        public RuleConfig Rule;
        public string Type;      // NullReferenceException / CRASH / ERR ...
        public string Abbr;      // NRE / CRASH / ERR ...
        public string Message;
        public readonly List<string> Stack = new List<string>();
        public int DroppedLines;
        public DateTime Time;
        public long LineNo;
    }

    /// <summary>Разбирает поток строк лога на ошибки по правилам.</summary>
    public sealed class LogParser
    {
        sealed class Compiled
        {
            public RuleConfig Rule;
            public Regex Regex;
            public bool HasType, HasMsg;
        }

        struct Hist { public long No; public string Text; public bool Blank, Frame; }

        static readonly Regex FrameRx = new Regex(
            @"^(?:\s+\S" +
            @"|at\s|Rethrow as |--- |\(Filename: |0x[0-9A-Fa-f]{6,}|\[0x" +
            @"|[\w.<>`'+\[\]/,|$:~\-]+ ?\(.*\))",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        readonly List<Compiled> _rules = new List<Compiled>();
        readonly List<string> _ruleErrors = new List<string>();
        readonly Hist[] _hist = new Hist[48];
        int _histCount, _histHead;
        long _lineNo;
        long _lastConsumed; // номер последней строки, вошедшей в какую-либо запись
        ParsedEntry _open;
        bool _openSeenFrame;
        DateTime _lastLine;

        static int CountLines(string s)
        {
            int n = 1;
            foreach (var ch in s) if (ch == '\n') n++;
            return n;
        }

        public int MaxStackLines = 80;
        public int MaxStackLinesGrouped = 400;
        public double IdleCloseMs = 600;
        public Func<DateTime> Clock = () => DateTime.Now;

        /// <summary>Готовая запись (стек собран).</summary>
        public event Action<ParsedEntry> Entry;

        public IList<string> RuleErrors { get { return _ruleErrors; } }

        public LogParser(IEnumerable<RuleConfig> rules)
        {
            foreach (var r in rules)
            {
                if (r == null || !r.Enabled || string.IsNullOrWhiteSpace(r.Pattern)) continue;
                try
                {
                    var rx = new Regex(r.Pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
                    var names = rx.GetGroupNames();
                    _rules.Add(new Compiled
                    {
                        Rule = r, Regex = rx,
                        HasType = Array.IndexOf(names, "type") >= 0,
                        HasMsg = Array.IndexOf(names, "msg") >= 0,
                    });
                }
                catch (Exception ex) { _ruleErrors.Add(r.Name + ": " + ex.Message); }
            }
        }

        public static bool IsFrame(string line)
        {
            return line.Length > 0 && FrameRx.IsMatch(line);
        }

        /// <summary>Файл пересоздан — забываем контекст.</summary>
        public void Reset()
        {
            Close();
            _histCount = 0; _histHead = 0;
            _lastConsumed = _lineNo;
        }

        public void Feed(string line)
        {
            _lineNo++;
            var now = Clock();
            _lastLine = now;

            if (line.Trim().Length == 0)
            {
                // в дампе краша бывают пустые строки — его закрывает только тишина в логе
                if (_open == null || !_open.Rule.Group) Close();
                Push(line, true, false);
                return;
            }

            foreach (var c in _rules)
            {
                Match m;
                try { m = c.Regex.Match(line); }
                catch (RegexMatchTimeoutException) { continue; }
                if (!m.Success) continue;

                if (!c.Rule.IsMarker)
                {
                    if (_open != null && _open.Rule == c.Rule && c.Rule.Group)
                    {
                        // новый раздел того же краша (например OUTPUTTING STACK TRACE):
                        // сжимаем то, что было до него (список модулей), чтобы стек влез
                        if (_open.Stack.Count > 30)
                        {
                            int cut = _open.Stack.Count - 10;
                            _open.Stack.RemoveRange(10, cut);
                            _open.Stack.Add("… пропущено строк: " + cut + " …");
                        }
                        AppendStack(line);
                        _lastConsumed = _lineNo;
                    }
                    else
                    {
                        Close();
                        _open = NewEntry(c, m, line, _lineNo, now);
                        _openSeenFrame = false;
                        _lastConsumed = _lineNo;
                    }
                    Push(line, false, false);
                    return;
                }

                // marker: сообщение — выше по логу
                long msgNo; string msg; List<string> between;
                ResolveMessage(out msgNo, out msg, out between);
                if (_open != null && msgNo <= _open.LineNo)
                {
                    AppendStack(line); // это часть уже открытой записи
                }
                else
                {
                    Close();
                    var e = new ParsedEntry
                    {
                        Rule = c.Rule, Type = c.Rule.Tag, Abbr = c.Rule.Tag,
                        Message = msg, Time = now, LineNo = msgNo > 0 ? msgNo : _lineNo,
                    };
                    _open = e;
                    _openSeenFrame = true;
                    foreach (var b in between) AppendStack(b);
                    AppendStack(line);
                }
                _lastConsumed = _lineNo;
                Push(line, false, true);
                return;
            }

            bool frame = IsFrame(line);
            if (_open != null)
            {
                if (_open.Rule.Group || frame)
                {
                    AppendStack(line);
                    if (frame) _openSeenFrame = true;
                    _lastConsumed = _lineNo;
                }
                else if (!_openSeenFrame && _open.Message.Length < 2000 && CountLines(_open.Message) < 8)
                {
                    // продолжение сообщения до начала стека (у MissingReferenceException оно в две строки)
                    _open.Message = _open.Message + "\n" + line.Trim();
                    _lastConsumed = _lineNo;
                }
                else Close();
            }
            Push(line, false, frame);
        }

        /// <summary>Закрывает запись, если лог замолчал.</summary>
        public void Tick()
        {
            if (_open != null && (Clock() - _lastLine).TotalMilliseconds >= IdleCloseMs) Close();
        }

        public void Flush() { Close(); }

        void Close()
        {
            if (_open == null) return;
            var e = _open;
            _open = null;
            var h = Entry;
            if (h != null) h(e);
        }

        ParsedEntry NewEntry(Compiled c, Match m, string line, long no, DateTime now)
        {
            var e = new ParsedEntry { Rule = c.Rule, Time = now, LineNo = no };
            if (c.HasType && m.Groups["type"].Success && m.Groups["type"].Value.Length > 0)
            {
                e.Type = m.Groups["type"].Value;
                e.Abbr = Abbreviate(e.Type);
            }
            else
            {
                e.Type = c.Rule.Tag;
                e.Abbr = c.Rule.Tag;
            }
            if (c.HasMsg && m.Groups["msg"].Success)
                e.Message = m.Groups["msg"].Value.TrimStart(':', ' ', '\t').TrimEnd();
            else
                e.Message = line.Trim();
            return e;
        }

        void AppendStack(string line)
        {
            if (_open == null) return;
            if (line.StartsWith("(Filename:", StringComparison.Ordinal)) return;
            int cap = _open.Rule.Group ? MaxStackLinesGrouped : MaxStackLines;
            if (_open.Stack.Count < cap) _open.Stack.Add(line.TrimEnd());
            else _open.DroppedLines++;
        }

        void Push(string text, bool blank, bool frame)
        {
            _hist[_histHead] = new Hist { No = _lineNo, Text = text, Blank = blank, Frame = frame };
            _histHead = (_histHead + 1) % _hist.Length;
            if (_histCount < _hist.Length) _histCount++;
        }

        /// <summary>
        /// Для маркера стека (UnityEngine.Debug:LogError) ищем текст сообщения выше:
        /// пропускаем строки стека, затем берём подряд идущие строки сообщения.
        /// </summary>
        void ResolveMessage(out long msgNo, out string msg, out List<string> between)
        {
            between = new List<string>();
            msgNo = 0; msg = "(без текста)";
            int idx = _histHead;
            int seen = 0;
            var frames = new List<string>();
            var msgLines = new List<Hist>();
            bool inMessage = false;
            while (seen < _histCount)
            {
                idx = (idx - 1 + _hist.Length) % _hist.Length;
                seen++;
                var h = _hist[idx];
                if (h.No <= _lastConsumed || h.Blank) break;
                if (!inMessage)
                {
                    if (h.Frame) { frames.Add(h.Text); continue; }
                    inMessage = true;
                    msgLines.Add(h);
                }
                else
                {
                    // Строки выше — чаще служебный лог движка, а не продолжение сообщения
                    // (у движка нет пустых строк-разделителей). Берём только ближайшую.
                    break;
                }
            }
            frames.Reverse();
            msgLines.Reverse();
            if (msgLines.Count > 0)
            {
                msgNo = msgLines[0].No;
                msg = msgLines[0].Text.Trim();
                for (int i = 1; i < msgLines.Count; i++) between.Add("  " + msgLines[i].Text.Trim());
            }
            else if (frames.Count > 0)
            {
                // сообщения не нашли — первая строка стека тоже сойдёт
            }
            between.AddRange(frames);
        }

        /// <summary>NullReferenceException → NRE, MissingReferenceException → MRE.</summary>
        public static string Abbreviate(string type)
        {
            if (string.IsNullOrEmpty(type)) return "?";
            var sb = new StringBuilder();
            foreach (var ch in type) if (char.IsUpper(ch)) sb.Append(ch);
            if (sb.Length < 2) return type.Length <= 6 ? type.ToUpperInvariant() : "EXC";
            return sb.ToString();
        }
    }
}
