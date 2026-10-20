using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using CrashCatcher.Core;

namespace CrashCatcher.Win
{
    /// <summary>
    /// Виджет поверх всех окон. Рисуется целиком вручную в 32-битную картинку с альфа-каналом
    /// (UpdateLayeredWindow): прозрачность фона регулируется отдельно, текст остаётся непрозрачным.
    /// Не забирает фокус у игры. В закреплённом режиме клики проходят насквозь.
    /// </summary>
    sealed class Overlay : Form
    {
        readonly AppController _app;
        float _s = 1f;
        Font _fTitle, _fSmall, _fMono, _fMonoBold, _fIcon, _fSymbol;
        bool _hasMdl2;
        float _charW = 7, _timeW = 60;
        int _rowH = 20, _lineH = 16;

        readonly List<Zone> _zones = new List<Zone>();
        Bitmap _bmp;
        bool _dirty = true;
        readonly Timer _renderTimer, _topTimer;

        int _scroll, _contentH, _listH;
        bool _stick = true;
        string _hotKey, _hint;
        Zone _pressed;
        enum DragKind { None, Move, Resize, Slider, Thumb }
        DragKind _drag;
        Point _dragScreen;
        Rectangle _dragBounds, _sliderTrack, _thumbRect;
        int _dragScroll;
        string _flash;
        DateTime _flashUntil;

        public string StatusText = "";
        public Color StatusColor = Color.Gray;

        readonly StringFormat _sfLeft, _sfLeftClip, _sfCenter, _sfMeasure;

        sealed class Zone
        {
            public Rectangle R;
            public string Id;
            public ErrorRecord Rec;
            public string Hint;
            public Cursor Cursor;
            public string Key { get { return Rec != null ? Id + ":" + Rec.Id : Id; } }
        }

        // Segoe MDL2 Assets
        const string G_Record = "", G_Pause = "", G_Play = "", G_Delete = "", G_Pin = "",
                     G_Settings = "", G_Minimize = "", G_Clock = "", G_Sync = "", G_Video = "",
                     G_Warning = "";

        public Overlay(AppController app)
        {
            _app = app;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            Text = "Crash Catcher";
            ControlBox = false;
            MinimizeBox = MaximizeBox = false;

            _sfLeft = Fmt(StringAlignment.Near, true);
            _sfLeftClip = Fmt(StringAlignment.Near, false);
            _sfCenter = Fmt(StringAlignment.Center, false);
            _sfMeasure = new StringFormat(StringFormat.GenericTypographic);
            _sfMeasure.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;

            _renderTimer = new Timer { Interval = 33 };
            _renderTimer.Tick += (s, e) =>
            {
                if (_flash != null && DateTime.Now >= _flashUntil) { _flash = null; _dirty = true; }
                if (_dirty) { _dirty = false; Render(); }
            };
            _topTimer = new Timer { Interval = 2000 };
            _topTimer.Tick += (s, e) => KeepOnTop();
        }

        static StringFormat Fmt(StringAlignment a, bool ellipsis)
        {
            var f = new StringFormat(StringFormat.GenericTypographic);
            f.Alignment = a;
            f.LineAlignment = StringAlignment.Center;
            f.FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.LineLimit | StringFormatFlags.MeasureTrailingSpaces;
            f.Trimming = ellipsis ? StringTrimming.EllipsisCharacter : StringTrimming.None;
            return f;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST;
                if (_app != null && _app.S.Pinned) cp.ExStyle |= Native.WS_EX_TRANSPARENT;
                return cp;
            }
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try { using (var g = Graphics.FromHwnd(IntPtr.Zero)) _s = g.DpiX / 96f; } catch { _s = 1f; }
            if (_s < 1f) _s = 1f;
            CreateFonts();
            _renderTimer.Start();
            _topTimer.Start();
            _dirty = true;
        }

        public float DpiScale { get { return _s; } }

        public void ApplySettings()
        {
            CreateFonts();
            ApplyPinned();
            MarkDirty();
        }

        void CreateFonts()
        {
            DisposeFonts();
            float fs = _app.S.FontSize;
            _fMono = new Font("Consolas", fs * _s, FontStyle.Regular, GraphicsUnit.Pixel);
            _fMonoBold = new Font("Consolas", (fs - 1) * _s, FontStyle.Bold, GraphicsUnit.Pixel);
            _fTitle = new Font("Segoe UI Semibold", 12.5f * _s, FontStyle.Regular, GraphicsUnit.Pixel);
            _fSmall = new Font("Segoe UI", 11.5f * _s, FontStyle.Regular, GraphicsUnit.Pixel);
            _hasMdl2 = FontExists("Segoe MDL2 Assets");
            _fIcon = new Font(_hasMdl2 ? "Segoe MDL2 Assets" : "Segoe UI Symbol", 11.5f * _s, FontStyle.Regular, GraphicsUnit.Pixel);
            _fSymbol = new Font("Segoe UI Symbol", 11.5f * _s, FontStyle.Regular, GraphicsUnit.Pixel);
            using (var bmp = new Bitmap(4, 4))
            using (var g = Graphics.FromImage(bmp))
            {
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                _charW = g.MeasureString("0000000000", _fMono, PointF.Empty, _sfMeasure).Width / 10f;
                _timeW = g.MeasureString("00:00:00", _fMono, PointF.Empty, _sfMeasure).Width;
                _lineH = (int)Math.Ceiling(_fMono.GetHeight(g));
            }
            _rowH = _lineH + (int)(7 * _s);
        }

        static bool FontExists(string name)
        {
            try { using (var f = new Font(name, 10)) return string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        void DisposeFonts()
        {
            foreach (var f in new[] { _fTitle, _fSmall, _fMono, _fMonoBold, _fIcon, _fSymbol }) if (f != null) f.Dispose();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _renderTimer.Dispose();
                _topTimer.Dispose();
                DisposeFonts();
                if (_bmp != null) _bmp.Dispose();
            }
            base.Dispose(disposing);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_MOUSEACTIVATE) { m.Result = (IntPtr)Native.MA_NOACTIVATE; return; }
            if (m.Msg == Native.WM_HOTKEY) { _app.OnHotkey(m.WParam.ToInt32()); return; }
            base.WndProc(ref m);
        }

        // ---------------------------------------------------------------- публичное

        public void MarkDirty() { _dirty = true; }

        public void Flash(string text)
        {
            _flash = text;
            _flashUntil = DateTime.Now.AddSeconds(2.5);
            _dirty = true;
        }

        public void ShowWidget()
        {
            if (!Visible) Show();
            Render();
            KeepOnTop();
        }

        public void ApplyPinned()
        {
            if (!IsHandleCreated) return;
            long ex = Native.GetExStyle(Handle);
            if (_app.S.Pinned) ex |= Native.WS_EX_TRANSPARENT; else ex &= ~(long)Native.WS_EX_TRANSPARENT;
            Native.SetExStyle(Handle, ex);
            _hotKey = null; _hint = null; _pressed = null; _drag = DragKind.None;
            Cursor = Cursors.Default;
            _dirty = true;
        }

        public void ScrollToEnd() { _stick = true; _dirty = true; }

        void KeepOnTop()
        {
            if (!Visible || !IsHandleCreated) return;
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }

        // ---------------------------------------------------------------- отрисовка

        /// <summary>Отрисовка в картинку без окна (для отладки и превью).</summary>
        public Bitmap RenderPreview(int w, int h)
        {
            if (_fMono == null) CreateFonts();
            var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                _zones.Clear();
                StatusText = "Слежу · Player.log";
                StatusColor = Color.FromArgb(81, 207, 102);
                PaintAll(g, w, h);
            }
            return bmp;
        }

        void Render()
        {
            if (!IsHandleCreated || IsDisposed || !Visible || _fMono == null) return;
            int w = Math.Max(1, Width), h = Math.Max(1, Height);
            if (_bmp == null || _bmp.Width != w || _bmp.Height != h)
            {
                if (_bmp != null) _bmp.Dispose();
                _bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            }
            using (var g = Graphics.FromImage(_bmp))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                _zones.Clear();
                try { PaintAll(g, w, h); }
                catch (Exception ex) { Debug.WriteLine(ex); }
            }
            Push(_bmp);
        }

        unsafe void Push(Bitmap bmp)
        {
            int w = bmp.Width, h = bmp.Height;
            IntPtr screen = Native.GetDC(IntPtr.Zero);
            IntPtr mem = Native.CreateCompatibleDC(screen);
            IntPtr dib = IntPtr.Zero, old = IntPtr.Zero;
            try
            {
                var bi = new Native.BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf(typeof(Native.BITMAPINFOHEADER)),
                    biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32, biCompression = 0,
                };
                IntPtr bits;
                dib = Native.CreateDIBSection(screen, ref bi, 0, out bits, IntPtr.Zero, 0);
                if (dib == IntPtr.Zero || bits == IntPtr.Zero) return;
                var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
                try
                {
                    long row = (long)w * 4;
                    for (int y = 0; y < h; y++)
                        Buffer.MemoryCopy((byte*)data.Scan0 + (long)y * data.Stride, (byte*)bits + y * row, row, row);
                }
                finally { bmp.UnlockBits(data); }

                old = Native.SelectObject(mem, dib);
                var dst = new Native.POINT(Left, Top);
                var size = new Native.SIZE(w, h);
                var src = new Native.POINT(0, 0);
                var blend = new Native.BLENDFUNCTION
                {
                    BlendOp = Native.AC_SRC_OVER, BlendFlags = 0, SourceConstantAlpha = 255, AlphaFormat = Native.AC_SRC_ALPHA
                };
                Native.UpdateLayeredWindow(Handle, screen, ref dst, ref size, mem, ref src, 0, ref blend, Native.ULW_ALPHA);
            }
            finally
            {
                if (old != IntPtr.Zero) Native.SelectObject(mem, old);
                if (dib != IntPtr.Zero) Native.DeleteObject(dib);
                Native.DeleteDC(mem);
                Native.ReleaseDC(IntPtr.Zero, screen);
            }
        }

        int S(float v) { return (int)Math.Round(v * _s); }

        void PaintAll(Graphics g, int w, int h)
        {
            var st = _app.S;
            bool pinned = st.Pinned;
            int bgA = (int)Math.Round(st.BackgroundOpacity * 255);
            if (!pinned && bgA < 2) bgA = 2; // почти невидимый, но кликабельный фон
            bool shadow = bgA < 160;

            float rad = 8 * _s;
            using (var path = RoundRect(new RectangleF(0.5f, 0.5f, w - 1.5f, h - 1.5f), rad))
            {
                using (var b = new SolidBrush(Color.FromArgb(bgA, 17, 19, 23))) g.FillPath(b, path);
                if (!pinned) using (var p = new Pen(Color.FromArgb(70, 255, 255, 255), 1f)) g.DrawPath(p, path);
            }

            int headH = S(28), footH = pinned ? 0 : S(24);
            PaintHeader(g, new Rectangle(0, 0, w, headH), bgA, shadow, pinned);
            PaintList(g, new Rectangle(0, headH, w, Math.Max(0, h - headH - footH)), shadow);
            if (!pinned) PaintFooter(g, new Rectangle(0, h - footH, w, footH), shadow);
        }

        void PaintHeader(Graphics g, Rectangle head, int bgA, bool shadow, bool pinned)
        {
            if (bgA > 4)
                using (var path = RoundTop(head, 8 * _s))
                using (var b = new SolidBrush(Color.FromArgb(Math.Min(255, bgA / 2), 0, 0, 0)))
                    g.FillPath(b, path);
            if (!pinned) _zones.Add(new Zone { R = head, Id = "head", Hint = "Перетащи, чтобы подвинуть виджет", Cursor = Cursors.SizeAll });

            int cy = head.Top + head.Height / 2;
            float x = S(11);
            float dot = 4 * _s;
            using (var b = new SolidBrush(StatusColor)) g.FillEllipse(b, x, cy - dot, dot * 2, dot * 2);
            x += S(14);
            const string title = "Crash Catcher";
            DrawText(g, title, _fTitle, Color.FromArgb(240, 240, 240), new RectangleF(x, head.Top, S(160), head.Height), shadow, _sfLeftClip);
            x += Measure(g, title, _fTitle) + S(12);

            float right = head.Right - S(6);
            if (!pinned)
            {
                int bs = S(23);
                var buttons = new[]
                {
                    new[] { "b_hide", G_Minimize, "–", "Скрыть виджет (" + _app.S.HotkeyToggleVisible + ")" },
                    new[] { "b_set", G_Settings, "⚙", "Настройки" },
                    new[] { "b_pin", G_Pin, "📌", "Закрепить: клики пойдут сквозь виджет в игру (" + _app.S.HotkeyTogglePin + ")" },
                    new[] { "b_clear", G_Delete, "✕", "Очистить консоль" },
                    new[] { "b_pause", _app.Paused ? G_Play : G_Pause, _app.Paused ? "▶" : "‖", _app.Paused ? "Продолжить слежение" : "Пауза слежения" },
                    new[] { "b_clip", G_Record, "●", "Сохранить клип сейчас (" + _app.S.ReplayHotkey + ")" },
                };
                foreach (var bt in buttons)
                {
                    right -= bs;
                    var r = new Rectangle((int)right, cy - bs / 2, bs, bs);
                    bool active = bt[0] == "b_pause" && _app.Paused;
                    Color col = active ? Color.FromArgb(255, 212, 59) : bt[0] == "b_clip" ? Color.FromArgb(255, 107, 107) : Color.FromArgb(225, 230, 235);
                    DrawButton(g, r, bt[0], bt[1], bt[2], bt[3], col, shadow);
                    right -= S(1);
                }
            }
            else
            {
                string t = "закреплено · " + _app.S.HotkeyTogglePin;
                float tw = Measure(g, t, _fSmall) + S(4);
                right -= tw + S(4);
                DrawText(g, t, _fSmall, Color.FromArgb(150, 158, 168), new RectangleF(right, head.Top, tw, head.Height), shadow, _sfLeftClip);
            }

            // счётчики по типам
            Dictionary<string, string> colors;
            var totals = _app.Book.Totals(out colors);
            float limit = right - S(8);
            foreach (var kv in totals)
            {
                string s = kv.Key + " " + kv.Value;
                float tw = Measure(g, s, _fSmall);
                if (x + tw > limit)
                {
                    if (x + S(12) < limit) DrawText(g, "…", _fSmall, Color.FromArgb(160, 160, 160), new RectangleF(x, head.Top, S(14), head.Height), shadow, _sfLeftClip);
                    break;
                }
                string c;
                colors.TryGetValue(kv.Key, out c);
                DrawText(g, s, _fSmall, ParseColor(c, Color.FromArgb(255, 107, 107)), new RectangleF(x, head.Top, tw + S(2), head.Height), shadow, _sfLeftClip);
                x += tw + S(9);
            }
        }

        void DrawButton(Graphics g, Rectangle r, string id, string glyph, string fallback, string hint, Color col, bool shadow)
        {
            bool hot = _hotKey == id;
            bool down = _pressed != null && _pressed.Id == id;
            if (hot)
                using (var b = new SolidBrush(Color.FromArgb(down ? 95 : 55, 255, 255, 255)))
                    g.FillEllipse(b, r.X + 1, r.Y + 1, r.Width - 2, r.Height - 2);
            DrawGlyph(g, glyph, fallback, col, r, shadow);
            _zones.Add(new Zone { R = r, Id = id, Hint = hint, Cursor = Cursors.Hand });
        }

        void DrawGlyph(Graphics g, string glyph, string fallback, Color col, RectangleF r, bool shadow)
        {
            if (_hasMdl2) DrawText(g, glyph, _fIcon, col, r, shadow, _sfCenter);
            else DrawText(g, fallback, _fSymbol, col, r, shadow, _sfCenter);
        }

        void PaintList(Graphics g, Rectangle area, bool shadow)
        {
            var items = _app.Book.Items;
            int padTop = S(4);
            var heights = new int[items.Count];
            int total = padTop * 2;
            float detailWidth = DetailWidth(area);
            for (int i = 0; i < items.Count; i++)
            {
                heights[i] = RowHeight(items[i], detailWidth);
                total += heights[i];
            }
            _contentH = total;
            _listH = area.Height;
            int maxScroll = Math.Max(0, total - area.Height);
            if (_stick) _scroll = maxScroll;
            if (_scroll > maxScroll) _scroll = maxScroll;
            if (_scroll < 0) _scroll = 0;

            var oldClip = g.Clip;
            g.SetClip(area);

            if (items.Count == 0)
            {
                DrawText(g, _app.EmptyText, _fSmall, Color.FromArgb(150, 158, 168),
                    new RectangleF(area.X + S(12), area.Y, area.Width - S(24), area.Height), shadow, _sfCenter);
            }

            int y = area.Top + padTop - _scroll;
            for (int i = 0; i < items.Count; i++)
            {
                int hgt = heights[i];
                if (y + hgt >= area.Top && y <= area.Bottom)
                    PaintRow(g, items[i], new Rectangle(area.Left, y, area.Width, hgt), area, detailWidth, shadow);
                y += hgt;
            }

            if (maxScroll > 0)
            {
                int trackH = area.Height - S(8);
                int th = Math.Max(S(24), (int)((long)trackH * area.Height / total));
                int ty = area.Top + S(4) + (int)((long)(trackH - th) * _scroll / maxScroll);
                bool hot = _hotKey == "thumb" || _drag == DragKind.Thumb;
                int tw = S(hot ? 6 : 4);
                _thumbRect = new Rectangle(area.Right - tw - S(3), ty, tw, th);
                using (var path = RoundRect(_thumbRect, tw / 2f))
                using (var b = new SolidBrush(Color.FromArgb(hot ? 170 : 95, 255, 255, 255)))
                    g.FillPath(b, path);
                if (!_app.S.Pinned)
                    _zones.Add(new Zone { R = new Rectangle(area.Right - S(14), ty, S(14), th), Id = "thumb", Cursor = Cursors.Hand });
            }
            else _thumbRect = Rectangle.Empty;

            g.Clip = oldClip;
            oldClip.Dispose();
        }

        float DetailWidth(Rectangle area)
        {
            float indent = S(8) + _timeW + S(8);
            return Math.Max(S(60), area.Width - indent - S(18));
        }

        int ChipH { get { return S(21); } }

        int RowHeight(ErrorRecord r, float detailWidth)
        {
            if (!r.Expanded || r.IsInfo) return _rowH;
            int chips = _app.S.Pinned ? 0 : S(5) + ChipH;
            return _rowH + DetailLines(r, detailWidth).Count * _lineH + chips + S(8);
        }

        struct DLine { public string Text; public Color Color; }

        List<DLine> DetailLines(ErrorRecord r, float width)
        {
            int cols = Math.Max(16, (int)(width / Math.Max(1f, _charW)));
            var res = new List<DLine>();
            var white = Color.FromArgb(242, 242, 242);
            var gray = Color.FromArgb(150, 158, 168);
            var stackCol = Color.FromArgb(172, 181, 192);

            foreach (var m in (r.Message ?? "").Split('\n'))
                foreach (var part in Wrap(m, cols)) res.Add(new DLine { Text = part, Color = white });

            string meta = r.Type + "  ·  ×" + r.Count + "  ·  впервые " + r.FirstSeen.ToString("HH:mm:ss");
            if (r.Count > 1) meta += "  ·  последний " + r.LastSeen.ToString("HH:mm:ss");
            foreach (var part in Wrap(meta, cols)) res.Add(new DLine { Text = part, Color = gray });

            if (r.Clip != null)
            {
                var c = r.Clip;
                string t = "Клип #" + c.Id + ": " + c.StatusText;
                if (c.Status == ClipStatus.Pending)
                {
                    int sec = (int)Math.Ceiling((c.ScheduledAt - DateTime.Now).TotalSeconds);
                    if (sec > 0) t += " через " + sec + " с";
                }
                if (!string.IsNullOrEmpty(c.FilePath)) t += " — " + Path.GetFileName(c.FilePath);
                if (!string.IsNullOrEmpty(c.Note)) t += " (" + c.Note + ")";
                foreach (var part in Wrap(t, cols)) res.Add(new DLine { Text = part, Color = ClipColor(c.Status) });
            }

            const int maxStack = 60;
            int shown = 0;
            foreach (var s in r.Stack)
            {
                foreach (var part in Wrap(s.Replace("\t", "    "), cols))
                {
                    if (shown >= maxStack) break;
                    res.Add(new DLine { Text = part, Color = stackCol });
                    shown++;
                }
                if (shown >= maxStack)
                {
                    res.Add(new DLine { Text = "… полный стек — кнопка «Копировать»", Color = gray });
                    break;
                }
            }
            if (r.DroppedLines > 0) res.Add(new DLine { Text = "… ещё " + r.DroppedLines + " строк(и) в логе", Color = gray });
            return res;
        }

        static IEnumerable<string> Wrap(string s, int cols)
        {
            s = (s ?? "").TrimEnd();
            if (s.Length <= cols) { yield return s; yield break; }
            int i = 0;
            bool first = true;
            while (i < s.Length)
            {
                int take = first ? cols : cols - 2;
                if (take < 4) take = 4;
                int n = Math.Min(take, s.Length - i);
                yield return (first ? "" : "  ") + s.Substring(i, n);
                i += n;
                first = false;
            }
        }

        static Color ClipColor(ClipStatus s)
        {
            switch (s)
            {
                case ClipStatus.Pending: return Color.FromArgb(255, 212, 59);
                case ClipStatus.Pressed: return Color.FromArgb(116, 192, 252);
                case ClipStatus.Linked:
                case ClipStatus.Saved: return Color.FromArgb(105, 219, 124);
                default: return Color.FromArgb(255, 169, 77);
            }
        }

        static string ClipGlyph(ClipStatus s, out string fallback)
        {
            switch (s)
            {
                case ClipStatus.Pending: fallback = "◷"; return G_Clock;
                case ClipStatus.Pressed: fallback = "↻"; return G_Sync;
                case ClipStatus.Linked:
                case ClipStatus.Saved: fallback = "▶"; return G_Video;
                default: fallback = "!"; return G_Warning;
            }
        }

        void PaintRow(Graphics g, ErrorRecord r, Rectangle rc, Rectangle area, float detailWidth, bool shadow)
        {
            var head = new Rectangle(rc.X, rc.Y, rc.Width, _rowH);
            string key = "row:" + r.Id;
            bool hot = _hotKey == key;
            bool pinned = _app.S.Pinned;
            if ((hot && !r.IsInfo) || r.Expanded)
            {
                var hr = new RectangleF(rc.X + S(4), rc.Y + 1, rc.Width - S(8) - (_thumbRect.IsEmpty ? 0 : S(8)), rc.Height - 2);
                using (var path = RoundRect(hr, 4 * _s))
                using (var b = new SolidBrush(Color.FromArgb(r.Expanded ? 22 : 26, 255, 255, 255)))
                    g.FillPath(b, path);
            }
            if (!pinned)
            {
                var zr = Rectangle.Intersect(head, area);
                if (zr.Height > 0)
                    _zones.Add(new Zone
                    {
                        R = zr, Id = "row", Rec = r,
                        Hint = r.IsInfo ? null : (r.Expanded ? "Свернуть" : "Нажми, чтобы раскрыть стек"),
                        Cursor = r.IsInfo ? Cursors.Default : Cursors.Hand,
                    });
            }

            float x = rc.X + S(10);
            DrawText(g, r.FirstSeen.ToString("HH:mm:ss"), _fMono, Color.FromArgb(128, 136, 147),
                new RectangleF(x, head.Y, _timeW + S(2), head.Height), shadow, _sfLeftClip);
            x += _timeW + S(8);
            float right = rc.Right - S(12) - (_thumbRect.IsEmpty ? 0 : S(6));

            if (r.IsInfo)
            {
                Color c;
                switch (r.Kind)
                {
                    case InfoKind.Good: c = Color.FromArgb(140, 233, 154); break;
                    case InfoKind.Warn: c = Color.FromArgb(255, 192, 120); break;
                    case InfoKind.Bad: c = Color.FromArgb(255, 135, 135); break;
                    default: c = Color.FromArgb(160, 168, 178); break;
                }
                DrawText(g, "• " + FirstLine(r.Message), _fMono, c, new RectangleF(x, head.Y, Math.Max(10, right - x), head.Height), shadow, _sfLeft);
                if (!pinned && hot) _hint = r.Message;
                return;
            }

            var rc0 = ParseColor(r.Rule != null ? r.Rule.Color : null, Color.FromArgb(255, 107, 107));
            float bw = Measure(g, r.Abbr, _fMonoBold) + S(10);
            float bh = _lineH;
            var br = new RectangleF(x, head.Y + (head.Height - bh) / 2f, bw, bh);
            using (var path = RoundRect(br, 3 * _s))
            using (var b = new SolidBrush(Color.FromArgb(75, rc0)))
                g.FillPath(b, path);
            DrawText(g, r.Abbr, _fMonoBold, Lighten(rc0), br, false, _sfCenter);
            x += bw + S(7);

            if (r.Clip != null)
            {
                string fb;
                string glyph = ClipGlyph(r.Clip.Status, out fb);
                float iw = S(18);
                var ir = new RectangleF(right - iw, head.Y, iw, head.Height);
                DrawGlyph(g, glyph, fb, ClipColor(r.Clip.Status), ir, shadow);
                if (!pinned)
                {
                    string hint = "Клип #" + r.Clip.Id + ": " + r.Clip.StatusText +
                                  (r.Clip.FilePath != null ? " — нажми, чтобы открыть" : "");
                    _zones.Add(new Zone { R = Rectangle.Round(ir), Id = "clipicon", Rec = r, Hint = hint, Cursor = r.Clip.FilePath != null ? Cursors.Hand : Cursors.Default });
                }
                right -= iw + S(4);
            }
            if (r.Count > 1)
            {
                string cnt = "×" + r.Count;
                float cw = Measure(g, cnt, _fMono) + S(2);
                DrawText(g, cnt, _fMono, Color.FromArgb(178, 186, 196), new RectangleF(right - cw, head.Y, cw, head.Height), shadow, _sfLeftClip);
                right -= cw + S(8);
            }
            DrawText(g, FirstLine(r.Message), _fMono, Color.FromArgb(236, 236, 236),
                new RectangleF(x, head.Y, Math.Max(10, right - x), head.Height), shadow, _sfLeft);

            if (!r.Expanded) return;

            float indent = rc.X + S(10) + _timeW + S(8);
            int y = rc.Y + _rowH;
            foreach (var line in DetailLines(r, detailWidth))
            {
                if (y + _lineH >= area.Top && y <= area.Bottom)
                    DrawText(g, line.Text, _fMono, line.Color, new RectangleF(indent, y, detailWidth + S(6), _lineH), shadow, _sfLeftClip);
                y += _lineH;
            }
            if (pinned) return; // в закреплённом режиме кнопки не нужны — клики идут в игру
            y += S(5);
            float cx = indent;
            var chips = new List<string[]> { new[] { "copy", "Копировать" } };
            if (r.Clip != null && r.Clip.FilePath != null && File.Exists(r.Clip.FilePath))
            {
                chips.Add(new[] { "openclip", "Открыть клип" });
                chips.Add(new[] { "showclip", "Показать в папке" });
            }
            chips.Add(new[] { "collapse", "Свернуть" });
            foreach (var c in chips)
            {
                float cw = Measure(g, c[1], _fSmall) + S(18);
                var cr = new RectangleF(cx, y, cw, ChipH);
                string ck = c[0] + ":" + r.Id;
                bool chot = _hotKey == ck;
                using (var path = RoundRect(cr, 4 * _s))
                using (var b = new SolidBrush(Color.FromArgb(chot ? 70 : 34, 255, 255, 255)))
                    g.FillPath(b, path);
                DrawText(g, c[1], _fSmall, Color.FromArgb(235, 235, 235), cr, shadow, _sfCenter);
                if (!pinned)
                {
                    var zr = Rectangle.Intersect(Rectangle.Round(cr), area);
                    if (zr.Height > 0) _zones.Add(new Zone { R = zr, Id = c[0], Rec = r, Cursor = Cursors.Hand });
                }
                cx += cw + S(6);
            }
        }

        void PaintFooter(Graphics g, Rectangle f, bool shadow)
        {
            _zones.Add(new Zone { R = f, Id = "foot", Cursor = Cursors.SizeAll });
            float x = f.X + S(11);
            int cy = f.Y + f.Height / 2;
            var gray = Color.FromArgb(150, 158, 168);
            DrawText(g, "Фон", _fSmall, gray, new RectangleF(x, f.Y, S(40), f.Height), shadow, _sfLeftClip);
            x += Measure(g, "Фон", _fSmall) + S(7);

            int tw = S(84);
            var track = new Rectangle((int)x, cy - S(2), tw, Math.Max(2, S(4)));
            _sliderTrack = track;
            double op = _app.S.BackgroundOpacity;
            using (var path = RoundRect(track, track.Height / 2f))
            using (var b = new SolidBrush(Color.FromArgb(70, 255, 255, 255)))
                g.FillPath(b, path);
            int filled = (int)Math.Round(tw * op);
            if (filled > 0)
                using (var path = RoundRect(new RectangleF(track.X, track.Y, filled, track.Height), track.Height / 2f))
                using (var b = new SolidBrush(Color.FromArgb(230, 77, 171, 247)))
                    g.FillPath(b, path);
            bool hot = _hotKey == "slider" || _drag == DragKind.Slider;
            float kr = (hot ? 6.5f : 5.5f) * _s;
            using (var b = new SolidBrush(Color.FromArgb(250, 250, 250)))
                g.FillEllipse(b, track.X + filled - kr, cy - kr, kr * 2, kr * 2);
            _zones.Add(new Zone { R = Rectangle.Inflate(track, S(7), S(8)), Id = "slider", Hint = "Прозрачность фона (текст не меняется)", Cursor = Cursors.Hand });
            x += tw + S(10);
            string pct = (int)Math.Round(op * 100) + "%";
            float pw = Measure(g, "100%", _fSmall) + S(2);
            DrawText(g, pct, _fSmall, gray, new RectangleF(x, f.Y, pw, f.Height), shadow, _sfLeftClip);
            x += pw + S(10);

            int gs = S(14);
            var grip = new Rectangle(f.Right - gs - S(3), f.Bottom - gs - S(3), gs, gs);
            using (var b = new SolidBrush(Color.FromArgb(150, 255, 255, 255)))
            {
                float d = 2f * _s, step = 4f * _s;
                for (int i = 0; i < 3; i++)
                    for (int j = 0; j <= i; j++)
                        g.FillEllipse(b, grip.Right - d - j * step - S(2), grip.Bottom - d - (i - j) * step - S(2), d, d);
            }
            _zones.Add(new Zone { R = grip, Id = "grip", Hint = "Потяни, чтобы изменить размер", Cursor = Cursors.SizeNWSE });

            string text = _flash ?? _hint ?? StatusText;
            DrawText(g, text, _fSmall, Color.FromArgb(185, 192, 200), new RectangleF(x, f.Y, Math.Max(10, grip.Left - x - S(6)), f.Height), shadow, _sfLeft);
        }

        // ---------------------------------------------------------------- мышь

        Zone Hit(Point p)
        {
            for (int i = _zones.Count - 1; i >= 0; i--) if (_zones[i].R.Contains(p)) return _zones[i];
            return null;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            switch (_drag)
            {
                case DragKind.Move:
                {
                    var p = Cursor.Position;
                    Location = new Point(_dragBounds.X + p.X - _dragScreen.X, _dragBounds.Y + p.Y - _dragScreen.Y);
                    return;
                }
                case DragKind.Resize:
                {
                    var p = Cursor.Position;
                    int nw = Math.Max(S(270), _dragBounds.Width + p.X - _dragScreen.X);
                    int nh = Math.Max(S(110), _dragBounds.Height + p.Y - _dragScreen.Y);
                    if (nw != Width || nh != Height)
                    {
                        Size = new Size(nw, nh);
                        Render();
                    }
                    return;
                }
                case DragKind.Slider:
                    SetOpacityFromX(e.X);
                    return;
                case DragKind.Thumb:
                {
                    int maxScroll = Math.Max(0, _contentH - _listH);
                    int free = _listH - S(8) - _thumbRect.Height;
                    if (free > 0 && maxScroll > 0)
                    {
                        int dy = Cursor.Position.Y - _dragScreen.Y;
                        _scroll = Math.Max(0, Math.Min(maxScroll, _dragScroll + (int)((long)dy * maxScroll / free)));
                        _stick = _scroll >= maxScroll;
                        _dirty = true;
                    }
                    return;
                }
            }
            var z = Hit(e.Location);
            string key = z != null ? z.Key : null;
            if (key != _hotKey)
            {
                _hotKey = key;
                _hint = z != null ? z.Hint : null;
                Cursor = z != null && z.Cursor != null ? z.Cursor : Cursors.Default;
                _dirty = true;
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left || _app.S.Pinned) return;
            var z = Hit(e.Location);
            _dragScreen = Cursor.Position;
            _dragBounds = Bounds;
            if (z == null) return;
            switch (z.Id)
            {
                case "head":
                case "foot": _drag = DragKind.Move; break;
                case "grip": _drag = DragKind.Resize; break;
                case "slider": _drag = DragKind.Slider; SetOpacityFromX(e.X); break;
                case "thumb": _drag = DragKind.Thumb; _dragScroll = _scroll; break;
                default: _pressed = z; _dirty = true; break;
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (_drag != DragKind.None)
            {
                var d = _drag;
                _drag = DragKind.None;
                if (d == DragKind.Move || d == DragKind.Resize || d == DragKind.Slider) _app.WidgetChanged(Bounds);
                _dirty = true;
                return;
            }
            if (_pressed != null)
            {
                var p = _pressed;
                _pressed = null;
                var z = Hit(e.Location);
                if (z != null && z.Key == p.Key && e.Button == MouseButtons.Left) Execute(p);
                _dirty = true;
            }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            int max = Math.Max(0, _contentH - _listH);
            _scroll -= e.Delta * 3 * _rowH / 120;
            if (_scroll < 0) _scroll = 0;
            if (_scroll > max) _scroll = max;
            _stick = _scroll >= max;
            _dirty = true;
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_drag != DragKind.None) return;
            _hotKey = null;
            _hint = null;
            _dirty = true;
        }

        void SetOpacityFromX(int x)
        {
            if (_sliderTrack.Width <= 0) return;
            double v = (x - _sliderTrack.X) / (double)_sliderTrack.Width;
            v = Math.Max(0, Math.Min(1, v));
            _app.S.BackgroundOpacity = Math.Round(v, 2);
            _dirty = true;
        }

        void Execute(Zone z)
        {
            var r = z.Rec;
            switch (z.Id)
            {
                case "b_clip": _app.ManualClip(0); break;
                case "b_pause": _app.TogglePause(); break;
                case "b_clear": _app.ClearConsole(); break;
                case "b_pin": _app.SetPinned(true); break;
                case "b_set": _app.ShowSettings(); break;
                case "b_hide": _app.SetWidgetVisible(false); break;
                case "row":
                    if (r != null && !r.IsInfo)
                    {
                        r.Expanded = !r.Expanded;
                        var items = _app.Book.Items;
                        if (items.Count == 0 || items[items.Count - 1] != r) _stick = false;
                    }
                    break;
                case "collapse": if (r != null) r.Expanded = false; break;
                case "copy":
                    if (r != null)
                    {
                        try { Clipboard.SetText(r.FullText()); Flash("Скопировано в буфер обмена"); }
                        catch { Flash("Не удалось скопировать: буфер обмена занят"); }
                    }
                    break;
                case "openclip":
                case "clipicon":
                    if (r != null && r.Clip != null && r.Clip.FilePath != null) OpenPath(r.Clip.FilePath, false);
                    break;
                case "showclip":
                    if (r != null && r.Clip != null && r.Clip.FilePath != null) OpenPath(r.Clip.FilePath, true);
                    break;
            }
            _dirty = true;
        }

        void OpenPath(string path, bool select)
        {
            try
            {
                if (select) Process.Start("explorer.exe", "/select,\"" + path + "\"");
                else Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex) { Flash("Не удалось открыть: " + ex.Message); }
        }

        // ---------------------------------------------------------------- утилиты рисования

        void DrawText(Graphics g, string s, Font f, Color c, RectangleF r, bool shadow, StringFormat sf)
        {
            if (string.IsNullOrEmpty(s) || r.Width <= 1 || r.Height <= 1) return;
            if (shadow)
            {
                using (var b = new SolidBrush(Color.FromArgb(Math.Min(200, (int)c.A), 0, 0, 0)))
                {
                    var r2 = r;
                    r2.Offset(Math.Max(1f, _s), Math.Max(1f, _s));
                    g.DrawString(s, f, b, r2, sf);
                }
            }
            using (var b = new SolidBrush(c)) g.DrawString(s, f, b, r, sf);
        }

        float Measure(Graphics g, string s, Font f)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            return g.MeasureString(s, f, PointF.Empty, _sfMeasure).Width;
        }

        static string FirstLine(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            int i = s.IndexOf('\n');
            return i < 0 ? s : s.Substring(0, i);
        }

        static Color Lighten(Color c)
        {
            return Color.FromArgb(255, c.R + (255 - c.R) / 3, c.G + (255 - c.G) / 3, c.B + (255 - c.B) / 3);
        }

        public static Color ParseColor(string hex, Color fallback)
        {
            if (string.IsNullOrEmpty(hex)) return fallback;
            try
            {
                var h = hex.Trim().TrimStart('#');
                if (h.Length == 6) return Color.FromArgb(255, Convert.ToInt32(h.Substring(0, 2), 16), Convert.ToInt32(h.Substring(2, 2), 16), Convert.ToInt32(h.Substring(4, 2), 16));
            }
            catch { }
            return fallback;
        }

        static GraphicsPath RoundRect(RectangleF r, float rad)
        {
            var p = new GraphicsPath();
            float d = Math.Min(rad * 2, Math.Min(r.Width, r.Height));
            if (d <= 0.5f) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        static GraphicsPath RoundTop(RectangleF r, float rad)
        {
            var p = new GraphicsPath();
            float d = Math.Min(rad * 2, Math.Min(r.Width, r.Height));
            p.AddArc(r.X + 0.5f, r.Y + 0.5f, d, d, 180, 90);
            p.AddArc(r.Right - d - 1f, r.Y + 0.5f, d, d, 270, 90);
            p.AddLine(r.Right - 1f, r.Bottom, r.X + 0.5f, r.Bottom);
            p.CloseFigure();
            return p;
        }
    }
}
