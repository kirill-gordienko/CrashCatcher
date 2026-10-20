using System;
using System.Collections.Generic;

namespace CrashCatcher.Core
{
    /// <summary>Сочетание клавиш вида "Alt+F10", "Ctrl+Shift+F12".</summary>
    public struct Hotkey
    {
        public bool Ctrl, Shift, Alt, Win;
        public int Vk;
        public string KeyName;

        public bool IsValid { get { return Vk != 0; } }

        // MOD_ALT=1, MOD_CONTROL=2, MOD_SHIFT=4, MOD_WIN=8
        public uint Modifiers { get { return (uint)((Alt ? 1 : 0) | (Ctrl ? 2 : 0) | (Shift ? 4 : 0) | (Win ? 8 : 0)); } }

        public override string ToString()
        {
            if (!IsValid) return "";
            var parts = new List<string>();
            if (Ctrl) parts.Add("Ctrl");
            if (Shift) parts.Add("Shift");
            if (Alt) parts.Add("Alt");
            if (Win) parts.Add("Win");
            parts.Add(KeyName);
            return string.Join("+", parts.ToArray());
        }

        static readonly Dictionary<string, int> Named = BuildNames();

        static Dictionary<string, int> BuildNames()
        {
            var d = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 1; i <= 24; i++) d["F" + i] = 0x70 + i - 1;
            for (char c = 'A'; c <= 'Z'; c++) d[c.ToString()] = c;
            for (char c = '0'; c <= '9'; c++) { d[c.ToString()] = c; d["D" + c] = c; }
            for (int i = 0; i <= 9; i++) d["NumPad" + i] = 0x60 + i;
            d["Space"] = 0x20; d["Enter"] = 0x0D; d["Return"] = 0x0D; d["Tab"] = 0x09; d["Escape"] = 0x1B; d["Esc"] = 0x1B;
            d["Backspace"] = 0x08; d["Back"] = 0x08;
            d["Insert"] = 0x2D; d["Ins"] = 0x2D; d["Delete"] = 0x2E; d["Del"] = 0x2E; d["Home"] = 0x24; d["End"] = 0x23;
            d["PageUp"] = 0x21; d["PgUp"] = 0x21; d["Prior"] = 0x21; d["PageDown"] = 0x22; d["PgDn"] = 0x22; d["Next"] = 0x22;
            d["Left"] = 0x25; d["Up"] = 0x26; d["Right"] = 0x27; d["Down"] = 0x28;
            d["Pause"] = 0x13; d["PrintScreen"] = 0x2C; d["PrtSc"] = 0x2C; d["Snapshot"] = 0x2C; d["Scroll"] = 0x91; d["ScrollLock"] = 0x91;
            d["Multiply"] = 0x6A; d["Add"] = 0x6B; d["Subtract"] = 0x6D; d["Decimal"] = 0x6E; d["Divide"] = 0x6F;
            d["Oemtilde"] = 0xC0; d["`"] = 0xC0; d["~"] = 0xC0;
            d["OemMinus"] = 0xBD; d["-"] = 0xBD; d["Oemplus"] = 0xBB; d["="] = 0xBB;
            return d;
        }

        public static string NameOf(int vk)
        {
            if (vk >= 0x70 && vk <= 0x87) return "F" + (vk - 0x70 + 1);
            if ((vk >= 'A' && vk <= 'Z') || (vk >= '0' && vk <= '9')) return ((char)vk).ToString();
            if (vk >= 0x60 && vk <= 0x69) return "NumPad" + (vk - 0x60);
            foreach (var kv in Named) if (kv.Value == vk && kv.Key.Length > 1) return kv.Key;
            return null;
        }

        public static bool TryParse(string text, out Hotkey hk)
        {
            hk = new Hotkey();
            if (string.IsNullOrWhiteSpace(text)) return false;
            var parts = text.Split(new[] { '+' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
            {
                var p = parts[i].Trim();
                var low = p.ToLowerInvariant();
                if (low == "ctrl" || low == "control" || low == "ctl") hk.Ctrl = true;
                else if (low == "shift") hk.Shift = true;
                else if (low == "alt" || low == "menu") hk.Alt = true;
                else if (low == "win" || low == "windows" || low == "lwin") hk.Win = true;
                else
                {
                    if (hk.Vk != 0) return false;
                    int vk;
                    if (!Named.TryGetValue(p, out vk)) return false;
                    hk.Vk = vk;
                    hk.KeyName = NameOf(vk) ?? p;
                }
            }
            return hk.Vk != 0;
        }

        /// <summary>Клавиши с расширенным скан-кодом (E0 xx).</summary>
        public static bool IsExtended(int vk)
        {
            switch (vk)
            {
                case 0x21: case 0x22: case 0x23: case 0x24: case 0x25: case 0x26: case 0x27: case 0x28:
                case 0x2D: case 0x2E: case 0x6F: case 0x5B: case 0x5C: case 0xA3: case 0xA5: case 0x2C:
                    return true;
            }
            return false;
        }
    }
}
