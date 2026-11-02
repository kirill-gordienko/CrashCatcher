using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace CrashCatcher.Core
{
    /// <summary>Поиск логов Unity и Unreal на диске и угадывание движка по пути.</summary>
    public static class LogFinder
    {
        public static string LocalLow()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow");
        }

        public static List<Tuple<string, string, DateTime>> FindUnityLogs()
        {
            var found = new List<Tuple<string, string, DateTime>>();
            try
            {
                foreach (var company in Directory.GetDirectories(LocalLow()))
                {
                    string[] products;
                    try { products = Directory.GetDirectories(company); } catch { continue; }
                    foreach (var product in products)
                    {
                        var p = Path.Combine(product, "Player.log");
                        if (File.Exists(p))
                            found.Add(Tuple.Create(Path.GetFileName(product) + "  (" + Path.GetFileName(company) + ")", p, File.GetLastWriteTime(p)));
                    }
                }
            }
            catch { }
            try
            {
                var ed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Unity", "Editor", "Editor.log");
                if (File.Exists(ed)) found.Add(Tuple.Create("Unity Editor (Editor.log)", ed, File.GetLastWriteTime(ed)));
            }
            catch { }
            return found;
        }

        /// <summary>
        /// Логи UE: недавние проекты редактора (из EditorSettings.ini в %LOCALAPPDATA%\\UnrealEngine)
        /// → &lt;Проект&gt;\\Saved\\Logs, и собранные игры → %LOCALAPPDATA%\\&lt;Игра&gt;\\Saved\\Logs.
        /// </summary>
        public static List<Tuple<string, string, DateTime>> FindUnrealLogs()
        {
            return FindUnrealLogs(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        }

        public static List<Tuple<string, string, DateTime>> FindUnrealLogs(string local)
        {
            var found = new List<Tuple<string, string, DateTime>>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var projects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var ueRoot = Path.Combine(local, "UnrealEngine");
                if (Directory.Exists(ueRoot))
                {
                    var rx = new Regex(@"(?:[A-Za-z]:)?[\\/][^""\r\n,()=]*?\.uproject", RegexOptions.IgnoreCase);
                    foreach (var ver in Directory.GetDirectories(ueRoot))
                    {
                        var cfg = Path.Combine(ver, "Saved", "Config");
                        if (!Directory.Exists(cfg)) continue;
                        foreach (var dir in Directory.GetDirectories(cfg))
                        {
                            var ini = Path.Combine(dir, "EditorSettings.ini");
                            if (!File.Exists(ini)) continue;
                            string text;
                            try { text = File.ReadAllText(ini); } catch { continue; }
                            foreach (Match m in rx.Matches(text)) projects.Add(m.Value.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar));
                        }
                    }
                }
            }
            catch { }

            foreach (var proj in projects)
            {
                try
                {
                    var logs = Path.Combine(Path.GetDirectoryName(proj), "Saved", "Logs");
                    AddUnrealLogs(found, seen, logs, Path.GetFileNameWithoutExtension(proj) + "  (редактор)");
                }
                catch { }
            }

            try
            {
                foreach (var dir in Directory.GetDirectories(local))
                {
                    var name = Path.GetFileName(dir);
                    if (string.Equals(name, "UnrealEngine", StringComparison.OrdinalIgnoreCase)) continue;
                    var logs = Path.Combine(dir, "Saved", "Logs");
                    if (Directory.Exists(logs)) AddUnrealLogs(found, seen, logs, name + "  (собранная игра)");
                }
            }
            catch { }
            return found;
        }

        static void AddUnrealLogs(List<Tuple<string, string, DateTime>> found, HashSet<string> seen, string logsDir, string label)
        {
            if (!Directory.Exists(logsDir)) return;
            foreach (var f in Directory.GetFiles(logsDir, "*.log"))
            {
                var n = Path.GetFileName(f);
                if (n.IndexOf("-backup-", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                if (n.StartsWith("cef", StringComparison.OrdinalIgnoreCase)) continue;
                if (!seen.Add(f)) continue;
                found.Add(Tuple.Create(label + ": " + n, f, File.GetLastWriteTime(f)));
            }
        }

        /// <summary>Угадать движок по пути к логу.</summary>
        public static string GuessEngine(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            var p = path.Replace('/', '\\');
            if (p.IndexOf("\\Saved\\Logs\\", StringComparison.OrdinalIgnoreCase) >= 0) return "unreal";
            var n = Path.GetFileName(p);
            if (string.Equals(n, "Player.log", StringComparison.OrdinalIgnoreCase) || string.Equals(n, "Editor.log", StringComparison.OrdinalIgnoreCase)
                || p.IndexOf("\\LocalLow\\", StringComparison.OrdinalIgnoreCase) >= 0) return "unity";
            return null;
        }

    }
}
