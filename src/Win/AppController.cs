using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using CrashCatcher.Core;

namespace CrashCatcher.Win
{
    /// <summary>Связывает всё вместе: лог → разбор → консоль → клипы, трей, горячие клавиши, настройки.</summary>
    sealed class AppController : IDisposable
    {
        public AppSettings S { get; private set; }
        public ErrorBook Book { get; private set; }
        public Overlay Overlay { get; private set; }
        public bool Paused { get; private set; }
        public string BaseDir { get; private set; }
        public Icon AppIcon { get; private set; }

        readonly string _settingsPath;
        readonly ClipPlanner _planner = new ClipPlanner();
        readonly ClipLinker _linker = new ClipLinker();
        readonly History _history;
        readonly ConcurrentQueue<ParsedEntry> _entries = new ConcurrentQueue<ParsedEntry>();
        readonly ConcurrentQueue<KeyValuePair<TailState, string>> _states = new ConcurrentQueue<KeyValuePair<TailState, string>>();
        readonly CancellationTokenSource _cts = new CancellationTokenSource();
        readonly System.Windows.Forms.Timer _uiTimer, _saveTimer;

        LogTailer _tailer;
        LogParser _parser;
        string _watchKey;
        bool _startedOnce;
        TailState _tailState = TailState.Starting;
        NotifyIcon _tray;
        SettingsForm _settingsForm;
        int _lastBookVersion = -1;
        bool _missingReported;

        const int HK_VISIBLE = 1, HK_PIN = 2;

        readonly bool _ephemeral;

        public AppController(bool ephemeral)
        {
            _ephemeral = ephemeral;
            BaseDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            _settingsPath = Path.Combine(BaseDir, "CrashCatcher.settings.json");
            if (!CanWrite(BaseDir))
            {
                var alt = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CrashCatcher");
                Directory.CreateDirectory(alt);
                _settingsPath = Path.Combine(alt, "CrashCatcher.settings.json");
                BaseDir = alt;
            }
            string err;
            S = AppSettings.Load(_settingsPath, out err);
            Book = new ErrorBook { MaxItems = S.MaxItems };
            _history = new History(Path.Combine(BaseDir, "history"));
            _planner.Apply(S);

            AppIcon = LoadIcon();
            Overlay = new Overlay(this);
            PlaceOverlay();

            _uiTimer = new System.Windows.Forms.Timer { Interval = 100 };
            _uiTimer.Tick += (s, e) => UiTick();
            _saveTimer = new System.Windows.Forms.Timer { Interval = 1500 };
            _saveTimer.Tick += (s, e) => { _saveTimer.Stop(); SaveSettings(); };

            if (err != null) Info("Файл настроек повреждён, загружены значения по умолчанию (" + err + ")", InfoKind.Warn);
        }

        static bool CanWrite(string dir)
        {
            try
            {
                var p = Path.Combine(dir, ".cc_write_test");
                File.WriteAllText(p, "x");
                File.Delete(p);
                return true;
            }
            catch { return false; }
        }

        static Icon LoadIcon()
        {
            try
            {
                using (var st = Assembly.GetExecutingAssembly().GetManifestResourceStream("CrashCatcher.app.ico"))
                    if (st != null) return new Icon(st, SystemInformation.SmallIconSize);
            }
            catch { }
            return SystemIcons.Application;
        }

        void PlaceOverlay()
        {
            float scale = 1f;
            try { using (var g = Graphics.FromHwnd(IntPtr.Zero)) scale = Math.Max(1f, g.DpiX / 96f); } catch { }
            var b = new Rectangle(S.WidgetX, S.WidgetY, S.WidgetW, S.WidgetH);
            bool firstPlacement = S.WidgetX == int.MinValue || S.WidgetY == int.MinValue;
            if (firstPlacement)
            {
                b.Width = (int)(S.WidgetW * scale);
                b.Height = (int)(S.WidgetH * scale);
            }
            bool onScreen = !firstPlacement && Screen.AllScreens.Any(sc =>
                sc.WorkingArea.IntersectsWith(new Rectangle(b.X + 40, b.Y + 10, Math.Max(10, b.Width - 80), Math.Max(10, b.Height / 3))));
            if (!onScreen)
            {
                var wa = Screen.PrimaryScreen.WorkingArea;
                b.Width = Math.Min(b.Width, wa.Width - 40);
                b.Height = Math.Min(b.Height, wa.Height - 40);
                b.X = wa.Right - b.Width - (int)(24 * scale);
                b.Y = wa.Top + (int)(24 * scale);
            }
            Overlay.Bounds = b;
        }

        // ---------------------------------------------------------------- запуск / остановка

        public void Start()
        {
            CreateTray();
            if (S.WidgetVisible) Overlay.ShowWidget();
            else { var h = Overlay.Handle; } // окно нужно для горячих клавиш даже скрытым
            Overlay.ApplyPinned();
            RegisterHotkeys();
            RestartTailer();
            _uiTimer.Start();
            UpdateStatus();

            if (Native.IsUserAnAdmin()) Info("Запущено от администратора", InfoKind.Normal);
            if (!S.FirstRunDone)
            {
                Info("Первый запуск. Укажи Player.log в настройках и проверь кнопкой «Тест», что NVIDIA сохраняет клип.", InfoKind.Warn);
                Info("В NVIDIA App должен быть включён «Мгновенный повтор» на 5 минут (Alt+F10).", InfoKind.Normal);
                ShowSettings();
            }
        }

        // ---------------------------------------------------------------- демо / отладка

        const string DemoLog = @"NullReferenceException: Object reference not set to an instance of an object
  at CarController.FixedUpdate () [0x00012] in <a1b2c3d4e5f6>:0 

MissingReferenceException: The object of type 'Rigidbody' has been destroyed but you are still trying to access it.
Your script should either check if it is null or you should not destroy the object.
  at (wrapper managed-to-native) UnityEngine.Rigidbody.get_velocity_Injected(UnityEngine.Rigidbody,UnityEngine.Vector3&)
  at UnityEngine.Rigidbody.get_velocity () [0x00000] in <hash>:0 
  at Speedometer.Update () [0x00001] in <hash>:0 
  at UnityEngine.Events.InvokableCall.Invoke () [0x00010] in <hash>:0 

Wheel friction too low for surface 'Asphalt_Wet'
UnityEngine.Debug:LogError(Object)
WheelSetup:Awake() (at Assets/Scripts/WheelSetup.cs:42)

InvalidOperationException: Collection was modified; enumeration operation may not execute.
  at System.Collections.Generic.List`1+Enumerator[T].MoveNextRare () [0x00013] in <hash>:0 
  at RaceManager.Update () [0x0001a] in <hash>:0 
";

        public void LoadDemo()
        {
            var t0 = DateTime.Now.AddSeconds(-95);
            Book.AddInfo("Слежу за C:\\Users\\nvrsk\\AppData\\LocalLow\\Studio\\DriftRacing\\Player.log", InfoKind.Normal, t0);
            var p = new LogParser(S.Rules);
            int i = 0;
            p.Clock = () => t0.AddSeconds(20 + 9 * i);
            var list = new List<ParsedEntry>();
            p.Entry += list.Add;
            foreach (var l in DemoLog.Replace("\r\n", "\n").Split('\n')) { p.Feed(l); }
            p.Flush();
            foreach (var e in list)
            {
                e.Time = t0.AddSeconds(20 + 9 * i++);
                bool isNew; DateTime prev;
                var r = Book.Add(e, out isNew, out prev);
                if (r.Abbr == "NRE")
                {
                    for (int k = 0; k < 36; k++) { e.Time = e.Time.AddMilliseconds(16); Book.Add(e, out isNew, out prev); }
                    r.Clip = new ClipJob { Id = 1, Status = ClipStatus.Linked, FilePath = "C:\\Videos\\NVIDIA\\DriftRacing\\2026-10-16_00-12-03_NRE.mp4" };
                }
                if (r.Abbr == "MRE") r.Expanded = true;
                if (r.Abbr == "IOE") r.Clip = new ClipJob { Id = 2, Status = ClipStatus.Pending, ScheduledAt = DateTime.Now.AddSeconds(41) };
            }
            Book.AddInfo("Клип #1 сохранён: 2026-10-16_00-12-03_NRE.mp4", InfoKind.Good, t0.AddSeconds(31));
            Overlay.MarkDirty();
        }

        public void RenderPreviews(string pngBase)
        {
            var variants = new[] { new { Op = 0.75, Pin = false, W = 520, H = 330, Name = "" }, new { Op = 0.0, Pin = true, W = 520, H = 230, Name = "_pinned" }, new { Op = 0.35, Pin = false, W = 300, H = 200, Name = "_small" } };
            foreach (var v in variants)
            {
                S.BackgroundOpacity = v.Op;
                S.Pinned = v.Pin;
                using (var bmp = Overlay.RenderPreview(v.W, v.H))
                    bmp.Save(pngBase + v.Name + ".png", System.Drawing.Imaging.ImageFormat.Png);
            }
        }

        public void Dispose()
        {
            try { _cts.Cancel(); } catch { }
            StopTailer();
            _uiTimer.Stop();
            UnregisterHotkeys();
            SaveSettings();
            if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
            if (_settingsForm != null && !_settingsForm.IsDisposed) _settingsForm.Dispose();
            Overlay.Dispose();
        }

        public void Exit()
        {
            Application.Exit();
        }

        // ---------------------------------------------------------------- слежение за логом

        string WatchKey()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append(S.LogPath).Append('|').Append(S.PollMs);
            foreach (var r in S.Rules) sb.Append('|').Append(r.Enabled).Append(r.Name).Append(r.Pattern).Append(r.Mode).Append(r.Group).Append(r.Tag);
            return sb.ToString();
        }

        void StopTailer()
        {
            var t = _tailer;
            _tailer = null;
            if (t != null) t.Stop();
            if (_parser != null) _parser.Flush();
            _parser = null;
        }

        void RestartTailer()
        {
            StopTailer();
            _watchKey = WatchKey();
            _tailState = TailState.Starting;
            _missingReported = false;
            if (Paused) { UpdateStatus(); return; }
            var path = AppSettings.Expand(S.LogPath);
            if (string.IsNullOrEmpty(path)) { UpdateStatus(); return; }

            var parser = new LogParser(S.Rules);
            foreach (var e in parser.RuleErrors) Info("Ошибка в правиле: " + e, InfoKind.Bad);
            parser.Entry += e => _entries.Enqueue(e);
            var tailer = new LogTailer(path, S.ReadExistingOnStart && !_startedOnce, S.PollMs);
            tailer.Line += parser.Feed;
            tailer.Polled += parser.Tick;
            tailer.StateChanged += (st, info) =>
            {
                if (st == TailState.Reset) parser.Reset();
                _states.Enqueue(new KeyValuePair<TailState, string>(st, info));
            };
            _parser = parser;
            _tailer = tailer;
            _startedOnce = true;
            tailer.Start();
            UpdateStatus();
        }

        void UiTick()
        {
            KeyValuePair<TailState, string> st;
            while (_states.TryDequeue(out st)) OnTailState(st.Key, st.Value);

            ParsedEntry e;
            int n = 0;
            while (n++ < 5000 && _entries.TryDequeue(out e)) OnEntry(e);

            var job = _planner.TakeDue(DateTime.Now);
            if (job != null) Press(job);

            if (_planner.Pending != null) Overlay.MarkDirty(); // обратный отсчёт в раскрытой строке
            if (Book.Version != _lastBookVersion)
            {
                _lastBookVersion = Book.Version;
                Overlay.MarkDirty();
            }
        }

        void OnTailState(TailState st, string info)
        {
            _tailState = st == TailState.Reset ? TailState.Watching : st;
            var name = Path.GetFileName(info ?? "");
            switch (st)
            {
                case TailState.Watching:
                    Info("Слежу за " + info, InfoKind.Normal);
                    _history.Write("Слежу за " + info);
                    _missingReported = false;
                    break;
                case TailState.Reset:
                    Info(name + " пересоздан — игра перезапущена, читаю заново", InfoKind.Normal);
                    _history.Write("Лог пересоздан: " + info);
                    break;
                case TailState.Missing:
                    if (!_missingReported) Info("Файл лога пока не существует, жду: " + info, InfoKind.Warn);
                    _missingReported = true;
                    break;
                case TailState.Error:
                    Info("Не могу прочитать лог: " + info, InfoKind.Bad);
                    break;
            }
            UpdateStatus();
        }

        void OnEntry(ParsedEntry e)
        {
            bool isNew;
            DateTime prev;
            var r = Book.Add(e, out isNew, out prev);
            if (isNew) _history.Write(r.Abbr + "  " + r.FullText());
            else if (prev != DateTime.MinValue && (e.Time - prev).TotalSeconds >= S.RepeatGapSec)
                _history.Write(r.Abbr + "  снова (×" + r.Count + "): " + FirstLine(r.Message));
            _planner.Consider(r, isNew, prev, e.Time);
        }

        static string FirstLine(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            int i = s.IndexOf('\n');
            return i < 0 ? s : s.Substring(0, i);
        }

        // ---------------------------------------------------------------- клипы

        public void ManualClip(double delaySec)
        {
            _planner.Manual(DateTime.Now, delaySec);
            if (delaySec > 0)
                Info("Тест: через " + delaySec + " с нажму " + S.ReplayHotkey + " — переключись в игру", InfoKind.Normal);
            Overlay.MarkDirty();
        }

        void Press(ClipJob job)
        {
            Hotkey hk;
            if (!Hotkey.TryParse(S.ReplayHotkey, out hk))
            {
                job.Status = ClipStatus.Failed;
                job.Note = "неверная клавиша " + S.ReplayHotkey;
                Info("Клип #" + job.Id + ": неверное сочетание клавиш «" + S.ReplayHotkey + "» в настройках", InfoKind.Bad);
                TouchJob(job);
                return;
            }

            var pressedLocal = job.PressedAt ?? DateTime.Now;
            var window = Book.SeenSince(pressedLocal.AddSeconds(-S.ReplayLengthSec));
            string sidecar = ClipText.Build(job, window, AppSettings.Expand(S.LogPath), S.ReplayLengthSec);
            var nameTime = job.Records.Count > 0 ? job.FirstErrorTime : pressedLocal;
            string baseName = nameTime.ToString("yyyy-MM-dd_HH-mm-ss") + "_" + job.Tag;
            string what = job.Records.Count > 0 ? job.Tag : "вручную";

            Info("Клип #" + job.Id + " (" + what + "): нажимаю " + hk, InfoKind.Normal);
            _history.Write("КЛИП #" + job.Id + " (" + what + "): нажимаю " + hk);
            TouchJob(job);

            bool scan = S.InputMode != "vk";
            int hold = S.KeyHoldMs;
            bool link = S.LinkClips;
            string root = AppSettings.Expand(S.ClipFolder);
            double wait = S.ClipWaitSec;
            var pressedUtc = pressedLocal.ToUniversalTime();
            var token = _cts.Token;

            ThreadPool.QueueUserWorkItem(_ =>
            {
                string err = null;
                try { err = KeySender.Send(hk, scan, hold); }
                catch (Exception ex) { err = ex.Message; }
                if (err != null)
                {
                    Post(() =>
                    {
                        job.Status = ClipStatus.Failed;
                        job.Note = err;
                        Info("Клип #" + job.Id + ": не удалось нажать клавиши — " + err, InfoKind.Bad);
                        _history.Write("КЛИП #" + job.Id + ": ошибка нажатия: " + err);
                        TouchJob(job);
                    });
                    return;
                }
                if (!link)
                {
                    Post(() =>
                    {
                        job.Status = ClipStatus.Saved;
                        Info("Клип #" + job.Id + ": " + hk + " отправлено в NVIDIA", InfoKind.Good);
                        TouchJob(job);
                    });
                    return;
                }
                var res = _linker.Run(root, pressedUtc, wait, baseName, sidecar, token, null);
                Post(() => OnLinked(job, res));
            });
        }

        void OnLinked(ClipJob job, LinkResult res)
        {
            if (res.Found && res.FinalPath != null)
            {
                job.FilePath = res.FinalPath;
                job.Status = ClipStatus.Linked;
                if (res.Error == null)
                {
                    Info("Клип #" + job.Id + " сохранён: " + Path.GetFileName(res.FinalPath), InfoKind.Good);
                    _history.Write("КЛИП #" + job.Id + " → " + res.FinalPath);
                }
                else
                {
                    job.Note = res.Error;
                    Info("Клип #" + job.Id + " сохранён, но " + res.Error + ": " + res.FinalPath, InfoKind.Warn);
                    _history.Write("КЛИП #" + job.Id + " → " + res.FinalPath + " (" + res.Error + ")");
                }
            }
            else
            {
                job.Status = ClipStatus.NotFound;
                job.Note = res.Error;
                Info("Клип #" + job.Id + ": " + res.Error + ". Проверь, что в NVIDIA App включён «Мгновенный повтор», и папку клипов в настройках.", InfoKind.Warn);
                _history.Write("КЛИП #" + job.Id + ": " + res.Error);
            }
            TouchJob(job);
        }

        void TouchJob(ClipJob job)
        {
            foreach (var r in job.Records) r.Version++;
            Book.Touch();
            Overlay.MarkDirty();
        }

        void Post(Action a)
        {
            try
            {
                if (Overlay.IsHandleCreated && !Overlay.IsDisposed) Overlay.BeginInvoke(a);
            }
            catch (InvalidOperationException) { }
        }

        // ---------------------------------------------------------------- команды

        public void Info(string text, InfoKind kind)
        {
            if (!S.ShowInfo && kind == InfoKind.Normal) return;
            Book.AddInfo(text, kind, DateTime.Now);
            Overlay.MarkDirty();
        }

        public void TogglePause()
        {
            Paused = !Paused;
            if (Paused)
            {
                StopTailer();
                _planner.CancelPending();
                Info("Пауза: лог не читается", InfoKind.Normal);
            }
            else
            {
                Info("Слежение продолжено (то, что было в паузе, пропущено)", InfoKind.Normal);
                RestartTailer();
            }
            UpdateStatus();
        }

        public void ClearConsole()
        {
            Book.Clear();
            Overlay.ScrollToEnd();
            Overlay.MarkDirty();
        }

        public void SetPinned(bool pinned)
        {
            S.Pinned = pinned;
            Overlay.ApplyPinned();
            if (pinned && !Overlay.Visible) Overlay.ShowWidget();
            Overlay.Flash(pinned ? "Закреплено. Открепить: " + S.HotkeyTogglePin + " или меню в трее" : "Откреплено");
            UpdateTrayMenu();
            SaveSoon();
        }

        public void SetWidgetVisible(bool visible)
        {
            S.WidgetVisible = visible;
            if (visible) Overlay.ShowWidget();
            else Overlay.Hide();
            UpdateTrayMenu();
            SaveSoon();
        }

        public void WidgetChanged(Rectangle bounds)
        {
            S.WidgetX = bounds.X; S.WidgetY = bounds.Y; S.WidgetW = bounds.Width; S.WidgetH = bounds.Height;
            SaveSoon();
        }

        public void OnHotkey(int id)
        {
            if (id == HK_VISIBLE) SetWidgetVisible(!Overlay.Visible);
            else if (id == HK_PIN) SetPinned(!S.Pinned);
        }

        public bool HasLogPath { get { return !string.IsNullOrWhiteSpace(S.LogPath); } }

        public string EmptyText
        {
            get
            {
                if (!HasLogPath) return "Укажи путь к Player.log в настройках (кнопка с шестерёнкой)";
                if (Paused) return "Пауза";
                return "Ошибок пока нет · слежу за " + Path.GetFileName(AppSettings.Expand(S.LogPath));
            }
        }

        void UpdateStatus()
        {
            Color c;
            string t;
            var file = HasLogPath ? Path.GetFileName(AppSettings.Expand(S.LogPath)) : "";
            if (!HasLogPath) { c = Color.FromArgb(150, 150, 150); t = "Лог не указан — открой настройки"; }
            else if (Paused) { c = Color.FromArgb(150, 150, 150); t = "Пауза · " + file; }
            else if (_tailState == TailState.Watching) { c = Color.FromArgb(81, 207, 102); t = "Слежу · " + file + (S.AutoSave ? "" : " · автоклипы выключены"); }
            else if (_tailState == TailState.Missing) { c = Color.FromArgb(252, 196, 25); t = "Жду появления " + file; }
            else if (_tailState == TailState.Error) { c = Color.FromArgb(255, 107, 107); t = "Ошибка чтения " + file; }
            else { c = Color.FromArgb(150, 150, 150); t = "Запуск…"; }
            Overlay.StatusColor = c;
            Overlay.StatusText = t;
            Overlay.MarkDirty();
            if (_tray != null)
            {
                var tip = "Crash Catcher — " + t;
                _tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
            }
            UpdateTrayMenu();
        }

        // ---------------------------------------------------------------- настройки

        public void ShowSettings()
        {
            if (_settingsForm != null && !_settingsForm.IsDisposed)
            {
                if (_settingsForm.WindowState == FormWindowState.Minimized) _settingsForm.WindowState = FormWindowState.Normal;
                _settingsForm.Activate();
                return;
            }
            _settingsForm = new SettingsForm(this, S.Clone());
            _settingsForm.FormClosed += (s, e) => _settingsForm = null;
            _settingsForm.Show();
            _settingsForm.Activate();
        }

        public void ApplySettings(AppSettings n)
        {
            // положение и вид виджета живут в текущих настройках
            n.WidgetX = S.WidgetX; n.WidgetY = S.WidgetY; n.WidgetW = S.WidgetW; n.WidgetH = S.WidgetH;
            n.Pinned = S.Pinned; n.WidgetVisible = S.WidgetVisible; n.BackgroundOpacity = S.BackgroundOpacity;
            n.FirstRunDone = true;
            n.Normalize();
            bool hotkeysChanged = n.HotkeyToggleVisible != S.HotkeyToggleVisible || n.HotkeyTogglePin != S.HotkeyTogglePin;
            S = n;
            Book.MaxItems = S.MaxItems;
            _planner.Apply(S);
            if (WatchKey() != _watchKey) RestartTailer();
            if (hotkeysChanged) RegisterHotkeys();
            Overlay.ApplySettings();
            UpdateStatus();
            SaveSettings();
        }

        void SaveSoon() { _saveTimer.Stop(); _saveTimer.Start(); }

        void SaveSettings()
        {
            if (_ephemeral) return;
            try { S.Save(_settingsPath); }
            catch (Exception ex) { Info("Не удалось сохранить настройки: " + ex.Message, InfoKind.Bad); }
        }

        public string SettingsPath { get { return _settingsPath; } }
        public string HistoryDir { get { return _history.Directory; } }

        // ---------------------------------------------------------------- горячие клавиши

        void RegisterHotkeys()
        {
            UnregisterHotkeys();
            TryRegister(HK_VISIBLE, S.HotkeyToggleVisible, "показать/скрыть");
            TryRegister(HK_PIN, S.HotkeyTogglePin, "закрепить/открепить");
        }

        void TryRegister(int id, string text, string what)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            Hotkey hk;
            if (!Hotkey.TryParse(text, out hk)) { Info("Не понял сочетание «" + text + "» (" + what + ")", InfoKind.Warn); return; }
            if (!Native.RegisterHotKey(Overlay.Handle, id, hk.Modifiers | Native.MOD_NOREPEAT, (uint)hk.Vk))
                Info("Сочетание " + hk + " (" + what + ") уже занято другой программой — поменяй в настройках", InfoKind.Warn);
        }

        void UnregisterHotkeys()
        {
            if (!Overlay.IsHandleCreated) return;
            Native.UnregisterHotKey(Overlay.Handle, HK_VISIBLE);
            Native.UnregisterHotKey(Overlay.Handle, HK_PIN);
        }

        // ---------------------------------------------------------------- трей

        ToolStripMenuItem _miShow, _miPin, _miPause, _miAuto;

        void CreateTray()
        {
            var menu = new ContextMenuStrip();
            _miShow = new ToolStripMenuItem("Показать виджет", null, (s, e) => SetWidgetVisible(!Overlay.Visible));
            _miPin = new ToolStripMenuItem("Закрепить (клики сквозь виджет)", null, (s, e) => SetPinned(!S.Pinned));
            _miPause = new ToolStripMenuItem("Пауза слежения", null, (s, e) => TogglePause());
            _miAuto = new ToolStripMenuItem("Автосохранение клипов", null, (s, e) =>
            {
                S.AutoSave = !S.AutoSave;
                _planner.Apply(S);
                if (!S.AutoSave) _planner.CancelPending();
                Info(S.AutoSave ? "Автосохранение клипов включено" : "Автосохранение клипов выключено", InfoKind.Normal);
                UpdateStatus();
                SaveSoon();
            });
            menu.Items.Add(_miShow);
            menu.Items.Add(_miPin);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Сохранить клип сейчас", null, (s, e) => ManualClip(0)));
            menu.Items.Add(_miAuto);
            menu.Items.Add(_miPause);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Настройки…", null, (s, e) => ShowSettings()));
            menu.Items.Add(new ToolStripMenuItem("Открыть журнал", null, (s, e) => OpenFolder(HistoryDir)));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Выход", null, (s, e) => Exit()));
            menu.Opening += (s, e) => UpdateTrayMenu();

            _tray = new NotifyIcon { Icon = AppIcon, Text = "Crash Catcher", ContextMenuStrip = menu, Visible = true };
            _tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) SetWidgetVisible(!Overlay.Visible); };
            UpdateTrayMenu();
        }

        void UpdateTrayMenu()
        {
            if (_miShow == null) return;
            _miShow.Text = Overlay.Visible ? "Скрыть виджет" : "Показать виджет";
            _miShow.ShortcutKeyDisplayString = S.HotkeyToggleVisible;
            _miPin.Checked = S.Pinned;
            _miPin.Text = S.Pinned ? "Открепить" : "Закрепить (клики сквозь виджет)";
            _miPin.ShortcutKeyDisplayString = S.HotkeyTogglePin;
            _miPause.Checked = Paused;
            _miAuto.Checked = S.AutoSave;
        }

        public static void OpenFolder(string dir)
        {
            try
            {
                Directory.CreateDirectory(dir);
                System.Diagnostics.Process.Start("explorer.exe", "\"" + dir + "\"");
            }
            catch { }
        }
    }
}
