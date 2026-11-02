using System;
using System.Text.RegularExpressions;

namespace CrashCatcher.Core
{
    /// <summary>Особенности формата лога конкретного движка: что срезать в начале строки и что считать строкой стека.</summary>
    public sealed class EngineProfile
    {
        public string Id;
        public string Title;
        /// <summary>Префикс, который убирается перед разбором (у UE — «[время][кадр]»).</summary>
        public Regex StripPrefix;
        /// <summary>Строка стека / продолжение текущей ошибки.</summary>
        public Regex FrameRx;
        /// <summary>Строка, которая для разбора считается пустой (разделителем).</summary>
        public Regex BlankRx;
        /// <summary>Категория строки (у UE — «LogTemp:»). Продолжение ошибки должно быть той же категории.</summary>
        public Regex CategoryRx;
        /// <summary>Уровень после категории («LogTemp: Error: ») — убирается для читаемости.</summary>
        public Regex VerbosityRx;
        /// <summary>«[File:…\X.cpp] [Line: 42]» → «X.cpp:42».</summary>
        public static readonly Regex FileRefRx = new Regex(@"\s*\[File:\s*(?<f>[^\]]+?)\s*\]\s*\[Line:\s*(?<l>\d+)\s*\]\s*", RegexOptions.Compiled);

        const RegexOptions Opt = RegexOptions.Compiled | RegexOptions.CultureInvariant;

        public static readonly EngineProfile Unity = new EngineProfile
        {
            Id = "unity",
            Title = "Unity",
            FrameRx = new Regex(
                @"^(?:\s+\S" +
                @"|at\s|Rethrow as |--- |\(Filename: |0x[0-9A-Fa-f]{6,}|\[0x" +
                @"|[\w.<>`'+\[\]/,|$:~\-]+ ?\(.*\))", Opt),
        };

        public static readonly EngineProfile Unreal = new EngineProfile
        {
            Id = "unreal",
            Title = "Unreal Engine 5",
            // [2026.11.01-09.05.21:345][322]
            StripPrefix = new Regex(@"^\[\d{4}\.\d\d\.\d\d-\d\d\.\d\d\.\d\d:\d{3}\]\[\s*\d+\]", Opt),
            FrameRx = new Regex(
                @"^(?:\s+\S" +                                             // \tFunction /Game/...  и  "  in: (...)"
                @"|Script Stack \(|Script call stack:" +
                @"|\[Callstack\]|0x[0-9A-Fa-f]{6,}" +
                @"|\w+: (?:Error|Warning|Fatal|Display|Log): (?:\[Callstack\]|Stack:\s*$|\s*$))", Opt),
            CategoryRx = new Regex(@"^([A-Za-z]\w*): (?:(Error|Warning|Fatal|Display|Verbose|Log): )?", Opt),
            VerbosityRx = new Regex(@"^([A-Za-z]\w*): (?:Error|Warning|Fatal|Display|Verbose|Log): ?", Opt),
        };

        /// <summary>Текст сообщения для показа: без «: Error:», ссылка на файл короче.</summary>
        public string CleanMessage(string s)
        {
            if (VerbosityRx != null) s = VerbosityRx.Replace(s, "$1: ", 1);
            if (StripPrefix != null) s = FileRefRx.Replace(s, m => " · " + ShortFile(m.Groups["f"].Value) + ":" + m.Groups["l"].Value + " ");
            return s.Trim();
        }

        /// <summary>Строка стека для показа: у UE без «LogX: Error: ».</summary>
        public string CleanStack(string s)
        {
            if (VerbosityRx != null) s = VerbosityRx.Replace(s, "", 1);
            return s.TrimEnd();
        }

        public string CategoryOf(string line)
        {
            if (CategoryRx == null) return null;
            var m = CategoryRx.Match(line);
            return m.Success ? m.Groups[1].Value + ":" + m.Groups[2].Value : null; // «LogTemp:Error»
        }

        static string ShortFile(string path)
        {
            int i = Math.Max(path.LastIndexOf('\\'), path.LastIndexOf('/'));
            return i >= 0 ? path.Substring(i + 1) : path;
        }

        public static EngineProfile ById(string id)
        {
            return string.Equals(id, Unreal.Id, StringComparison.OrdinalIgnoreCase) ? Unreal : Unity;
        }
    }
}
