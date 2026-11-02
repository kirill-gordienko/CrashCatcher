using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace CrashCatcher.Core
{
    /// <summary>Правило распознавания строки лога.</summary>
    [DataContract]
    public sealed class RuleConfig
    {
        [DataMember(Order = 1)] public bool Enabled { get; set; }
        [DataMember(Order = 2)] public string Name { get; set; }
        /// <summary>Регулярное выражение. Группы (?&lt;type&gt;) и (?&lt;msg&gt;) необязательны.</summary>
        [DataMember(Order = 3)] public string Pattern { get; set; }
        /// <summary>"line" — строка сама является ошибкой; "marker" — строка стека (например UnityEngine.Debug:LogError), сообщение берётся выше.</summary>
        [DataMember(Order = 4)] public string Mode { get; set; }
        [DataMember(Order = 5)] public bool SaveClip { get; set; }
        /// <summary>Сохранять клип сразу, без задержки и паузы между клипами (для крашей).</summary>
        [DataMember(Order = 6)] public bool Immediate { get; set; }
        /// <summary>Все срабатывания правила — одна строка в консоли; всё, что идёт после, считается стеком (для крашей).</summary>
        [DataMember(Order = 7)] public bool Group { get; set; }
        /// <summary>Короткая метка, если в регулярке нет группы type.</summary>
        [DataMember(Order = 8)] public string Tag { get; set; }
        [DataMember(Order = 9)] public string Color { get; set; }

        public bool IsMarker { get { return string.Equals(Mode, "marker", StringComparison.OrdinalIgnoreCase); } }

        public RuleConfig Clone() { return (RuleConfig)MemberwiseClone(); }

        public static List<RuleConfig> Defaults()
        {
            return new List<RuleConfig>
            {
                new RuleConfig
                {
                    Enabled = true, Name = "Исключения (*Exception)",
                    Pattern = @"^(?:\[[^\]]*\]\s*)*(?:[A-Za-z_]\w*\.)*(?<type>\w*Exception)(?<msg>:.*|\s+while\s.*)?\s*$",
                    Mode = "line", SaveClip = true, Tag = "EXC", Color = "#FF6B6B"
                },
                new RuleConfig
                {
                    Enabled = true, Name = "Нативный краш Unity",
                    Pattern = @"^(?:Crash!!!|=+\s*OUTPUTTING STACK TRACE\s*=+)",
                    Mode = "line", SaveClip = true, Immediate = true, Group = true, Tag = "CRASH", Color = "#F06595"
                },
                new RuleConfig
                {
                    Enabled = true, Name = "Debug.LogError",
                    Pattern = @"^UnityEngine\.Debug:LogError",
                    Mode = "marker", SaveClip = false, Tag = "ERR", Color = "#FFA94D"
                },
                new RuleConfig
                {
                    Enabled = true, Name = "Assert",
                    Pattern = @"^UnityEngine\.(?:Debug:(?:Assert|LogAssertion)|Assertions\.Assert:)",
                    Mode = "marker", SaveClip = false, Tag = "ASSERT", Color = "#FFD43B"
                },
            };
        }
    }

    [DataContract]
    public sealed class AppSettings
    {
        // --- лог ---
        [DataMember(Order = 1)] public string LogPath { get; set; }
        [DataMember(Order = 2)] public bool ReadExistingOnStart { get; set; }
        [DataMember(Order = 3)] public int PollMs { get; set; }

        // --- запись ---
        [DataMember(Order = 10)] public bool AutoSave { get; set; }
        [DataMember(Order = 11)] public string ReplayHotkey { get; set; }
        /// <summary>"scancode" или "vk".</summary>
        [DataMember(Order = 12)] public string InputMode { get; set; }
        [DataMember(Order = 13)] public int KeyHoldMs { get; set; }
        [DataMember(Order = 14)] public double SaveDelaySec { get; set; }
        [DataMember(Order = 15)] public double CooldownSec { get; set; }
        [DataMember(Order = 16)] public double RepeatGapSec { get; set; }
        [DataMember(Order = 17)] public double ReplayLengthSec { get; set; }

        // --- клипы ---
        [DataMember(Order = 20)] public bool LinkClips { get; set; }
        [DataMember(Order = 21)] public string ClipFolder { get; set; }
        [DataMember(Order = 22)] public double ClipWaitSec { get; set; }

        // --- правила ---
        [DataMember(Order = 30)] public List<RuleConfig> Rules { get; set; }

        // --- виджет ---
        [DataMember(Order = 40)] public string HotkeyToggleVisible { get; set; }
        [DataMember(Order = 41)] public string HotkeyTogglePin { get; set; }
        [DataMember(Order = 53)] public string HotkeyClear { get; set; }
        [DataMember(Order = 42)] public bool Pinned { get; set; }
        [DataMember(Order = 43)] public bool WidgetVisible { get; set; }
        [DataMember(Order = 44)] public int WidgetX { get; set; }
        [DataMember(Order = 45)] public int WidgetY { get; set; }
        [DataMember(Order = 46)] public int WidgetW { get; set; }
        [DataMember(Order = 47)] public int WidgetH { get; set; }
        [DataMember(Order = 48)] public double BackgroundOpacity { get; set; }
        [DataMember(Order = 49)] public float FontSize { get; set; }
        [DataMember(Order = 50)] public int MaxItems { get; set; }
        [DataMember(Order = 51)] public bool ShowInfo { get; set; }
        [DataMember(Order = 52)] public bool FirstRunDone { get; set; }

        public AppSettings() { SetDefaults(); }

        [OnDeserializing]
        private void OnDeserializing(StreamingContext c) { SetDefaults(); }

        private void SetDefaults()
        {
            LogPath = "";
            ReadExistingOnStart = false;
            PollMs = 250;
            AutoSave = true;
            ReplayHotkey = "Alt+F10";
            InputMode = "scancode";
            KeyHoldMs = 60;
            SaveDelaySec = 3;
            CooldownSec = 60;
            RepeatGapSec = 300;
            ReplayLengthSec = 300;
            LinkClips = true;
            ClipFolder = DefaultVideos();
            ClipWaitSec = 60;
            Rules = RuleConfig.Defaults();
            HotkeyToggleVisible = "Ctrl+Shift+F11";
            HotkeyTogglePin = "Ctrl+Shift+F12";
            HotkeyClear = "Ctrl+Shift+F9";
            Pinned = false;
            WidgetVisible = true;
            WidgetX = int.MinValue; WidgetY = int.MinValue;
            WidgetW = 470; WidgetH = 250;
            BackgroundOpacity = 0.75;
            FontSize = 12;
            MaxItems = 500;
            ShowInfo = true;
            FirstRunDone = false;
        }

        /// <summary>Приводит значения к допустимым диапазонам.</summary>
        public void Normalize()
        {
            if (LogPath == null) LogPath = "";
            PollMs = Clamp(PollMs, 50, 5000);
            if (string.IsNullOrWhiteSpace(ReplayHotkey)) ReplayHotkey = "Alt+F10";
            if (InputMode != "vk") InputMode = "scancode";
            KeyHoldMs = Clamp(KeyHoldMs, 10, 500);
            ReplayLengthSec = Clamp(ReplayLengthSec, 30, 1200);
            SaveDelaySec = Clamp(SaveDelaySec, 0, 30);
            CooldownSec = Clamp(CooldownSec, 5, Math.Max(5, ReplayLengthSec - 15));
            RepeatGapSec = Clamp(RepeatGapSec, 10, 86400);
            if (string.IsNullOrWhiteSpace(ClipFolder)) ClipFolder = DefaultVideos();
            ClipWaitSec = Clamp(ClipWaitSec, 5, 600);
            if (Rules == null || Rules.Count == 0) Rules = RuleConfig.Defaults();
            Rules.RemoveAll(r => r == null);
            foreach (var r in Rules)
            {
                if (r.Name == null) r.Name = "Правило";
                if (r.Pattern == null) r.Pattern = "";
                if (r.Mode == null) r.Mode = "line";
                if (string.IsNullOrWhiteSpace(r.Tag)) r.Tag = "LOG";
                if (string.IsNullOrWhiteSpace(r.Color)) r.Color = "#66D9E8";
            }
            if (HotkeyToggleVisible == null) HotkeyToggleVisible = "";
            if (HotkeyTogglePin == null) HotkeyTogglePin = "";
            if (HotkeyClear == null) HotkeyClear = "";
            BackgroundOpacity = Clamp(BackgroundOpacity, 0, 1);
            FontSize = (float)Clamp(FontSize, 8, 24);
            MaxItems = Clamp(MaxItems, 20, 10000);
            WidgetW = Clamp(WidgetW, 260, 4000);
            WidgetH = Clamp(WidgetH, 110, 4000);
        }

        public AppSettings Clone()
        {
            var s = (AppSettings)MemberwiseClone();
            s.Rules = new List<RuleConfig>();
            if (Rules != null) foreach (var r in Rules) s.Rules.Add(r.Clone());
            return s;
        }

        static string DefaultVideos()
        {
            try
            {
                var v = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
                if (!string.IsNullOrEmpty(v)) return v;
            }
            catch { }
            return @"%USERPROFILE%\Videos";
        }

        static int Clamp(int v, int lo, int hi) { return v < lo ? lo : v > hi ? hi : v; }
        static double Clamp(double v, double lo, double hi) { return double.IsNaN(v) ? lo : v < lo ? lo : v > hi ? hi : v; }

        // ---------- загрузка / сохранение ----------

        public static AppSettings Load(string path, out string error)
        {
            error = null;
            try
            {
                if (!File.Exists(path)) { var d = new AppSettings(); d.Normalize(); return d; }
                using (var fs = File.OpenRead(path))
                {
                    var ser = new DataContractJsonSerializer(typeof(AppSettings));
                    var s = (AppSettings)ser.ReadObject(fs);
                    s.Normalize();
                    return s;
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
                try { File.Copy(path, path + ".broken", true); } catch { }
                var d = new AppSettings(); d.Normalize(); return d;
            }
        }

        public void Save(string path)
        {
            var tmp = path + ".tmp";
            using (var fs = File.Create(tmp))
            {
                using (var w = JsonReaderWriterFactory.CreateJsonWriter(fs, new UTF8Encoding(false), false, true, "  "))
                {
                    new DataContractJsonSerializer(typeof(AppSettings)).WriteObject(w, this);
                    w.Flush();
                }
            }
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        public static string Expand(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            return Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        }
    }
}
