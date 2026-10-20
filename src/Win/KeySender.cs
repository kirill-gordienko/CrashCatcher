using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using CrashCatcher.Core;

namespace CrashCatcher.Win
{
    /// <summary>Программно нажимает сочетание клавиш (по умолчанию Alt+F10 для NVIDIA «Мгновенный повтор»).</summary>
    static class KeySender
    {
        const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LWIN = 0x5B, VK_RWIN = 0x5C;
        const int VK_LSHIFT = 0xA0, VK_LCONTROL = 0xA2, VK_LMENU = 0xA4;

        /// <summary>Возвращает текст ошибки или null.</summary>
        public static string Send(Hotkey hk, bool scanCodes, int holdMs)
        {
            if (!hk.IsValid) return "неверное сочетание клавиш";

            // Если игрок прямо сейчас держит лишний модификатор (например Shift),
            // сочетание превратится в другое — немного ждём, пока отпустит.
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 400 && ExtraModifiersHeld(hk)) Thread.Sleep(20);

            var mods = new List<int>();
            if (hk.Ctrl) mods.Add(VK_LCONTROL);
            if (hk.Shift) mods.Add(VK_LSHIFT);
            if (hk.Alt) mods.Add(VK_LMENU);
            if (hk.Win) mods.Add(VK_LWIN);

            int expected = 0, sent = 0;
            string error = null;
            try
            {
                foreach (var m in mods) { expected++; sent += Key(m, false, scanCodes); Thread.Sleep(15); }
                expected++; sent += Key(hk.Vk, false, scanCodes);
                Thread.Sleep(holdMs);
            }
            finally
            {
                // отпускаем всегда, даже если что-то пошло не так
                expected++; sent += Key(hk.Vk, true, scanCodes);
                Thread.Sleep(15);
                for (int i = mods.Count - 1; i >= 0; i--) { expected++; sent += Key(mods[i], true, scanCodes); Thread.Sleep(10); }
            }
            if (sent != expected)
                error = "SendInput отправил " + sent + " из " + expected + " событий (" + new Win32Exception(Marshal.GetLastWin32Error()).Message + ")";
            return error;
        }

        static bool Down(int vk) { return (Native.GetAsyncKeyState(vk) & 0x8000) != 0; }

        static bool ExtraModifiersHeld(Hotkey hk)
        {
            return (!hk.Ctrl && Down(VK_CONTROL)) || (!hk.Shift && Down(VK_SHIFT)) || (!hk.Alt && Down(VK_MENU))
                || (!hk.Win && (Down(VK_LWIN) || Down(VK_RWIN)));
        }

        static int Key(int vk, bool up, bool scanMode)
        {
            ushort scan = (ushort)Native.MapVirtualKey((uint)vk, 0 /* MAPVK_VK_TO_VSC */);
            uint flags = up ? Native.KEYEVENTF_KEYUP : 0;
            if (Hotkey.IsExtended(vk)) flags |= Native.KEYEVENTF_EXTENDEDKEY;
            if (scanMode && scan != 0) flags |= Native.KEYEVENTF_SCANCODE;
            var input = new Native.INPUT
            {
                type = Native.INPUT_KEYBOARD,
                U = new Native.InputUnion
                {
                    ki = new Native.KEYBDINPUT
                    {
                        wVk = (scanMode && scan != 0) ? (ushort)0 : (ushort)vk,
                        wScan = scan,
                        dwFlags = flags,
                    }
                }
            };
            return (int)Native.SendInput(1, new[] { input }, Marshal.SizeOf(typeof(Native.INPUT)));
        }
    }
}
