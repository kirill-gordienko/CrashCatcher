using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using CrashCatcher.Core;
using Microsoft.Win32;

namespace CrashCatcher.Win
{
    sealed class SettingsForm : Form
    {
        readonly AppController _app;
        readonly AppSettings _s;

        readonly TabControl _tabs;
        TabPage _rulesTab;
        TextBox _txtLog, _txtClipFolder, _txtTestLine;
        CheckBox _chkReadExisting, _chkAutoSave, _chkLink, _chkShowInfo, _chkAutostart;
        NumericUpDown _numPoll, _numDelay, _numCooldown, _numRepeat, _numReplay, _numWait, _numFont, _numMax, _numHold;
        HotkeyBox _hkReplay, _hkVisible, _hkPin, _hkClear;
        ComboBox _cmbInput;
        DataGridView _grid;
        BindingList<RuleConfig> _rules;
        Label _lblTest, _lblTestRun;

        public SettingsForm(AppController app, AppSettings s)
        {
            _app = app;
            _s = s;

            SuspendLayout();
            AutoScaleDimensions = new SizeF(96f, 96f);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Segoe UI", 9f);
            Text = "Crash Catcher — настройки";
            Icon = app.AppIcon;
            StartPosition = FormStartPosition.CenterScreen;
            TopMost = true;
            ClientSize = new Size(700, 500);
            MinimumSize = new Size(600, 440);
            ShowInTaskbar = true;

            _tabs = new TabControl { Dock = DockStyle.Fill };
            _tabs.TabPages.Add(LogTab());
            _tabs.TabPages.Add(RecordTab());
            _tabs.TabPages.Add(ClipsTab());
            _tabs.TabPages.Add(_rulesTab = RulesTab());
            _tabs.TabPages.Add(WidgetTab());

            var bottom = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true,
                Padding = new Padding(8, 6, 8, 8), WrapContents = false,
            };
            var btnCancel = new Button { Text = "Отмена", AutoSize = true, MinimumSize = new Size(90, 28) };
            btnCancel.Click += (o, e) => Close();
            var btnSave = new Button { Text = "Сохранить", AutoSize = true, MinimumSize = new Size(110, 28) };
            btnSave.Click += (o, e) => { if (Apply()) Close(); };
            var btnFolder = new Button { Text = "Папка программы", AutoSize = true, MinimumSize = new Size(90, 28) };
            btnFolder.Click += (o, e) => AppController.OpenFolder(_app.BaseDir);
            bottom.Controls.Add(btnCancel);
            bottom.Controls.Add(btnSave);
            bottom.Controls.Add(btnFolder);
            AcceptButton = btnSave;
            CancelButton = btnCancel;

            Controls.Add(_tabs);
            Controls.Add(bottom);
            ResumeLayout(true);

            int dbgTab;
            if (int.TryParse(Environment.GetEnvironmentVariable("CC_DEBUG_TAB"), out dbgTab) && dbgTab >= 0 && dbgTab < _tabs.TabCount)
                _tabs.SelectedIndex = dbgTab;
        }

        // ------------------------------------------------------------ вкладки

        TabPage LogTab()
        {
            var page = new TabPage("Лог игры");
            var t = new RowsPanel();
            _txtLog = new TextBox { Text = _s.LogPath };
            var browse = SmallButton("Обзор…");
            browse.Click += (o, e) => BrowseLog();
            t.Row("Файл лога:", _txtLog, browse);

            var find = SmallButton("Найти логи Unity ▾");
            find.Click += (o, e) => ShowFoundLogs(find);
            t.Row(null, find, null);

            _chkReadExisting = new CheckBox { Text = "При запуске разобрать то, что уже есть в логе (обычно не нужно)", AutoSize = true, Checked = _s.ReadExistingOnStart };
            t.Full(_chkReadExisting);
            _numPoll = Num(50, 2000, _s.PollMs, 0, 50);
            t.Row("Опрос файла, мс:", _numPoll, null);
            t.Hint(("Билд Unity пишет лог в %USERPROFILE%\\AppData\\LocalLow\\<Company>\\<Product>\\Player.log. " +
                         "Файл пересоздаётся при каждом запуске игры — тулза это замечает и читает новый файл с начала. " +
                         "Редактор пишет в %LOCALAPPDATA%\\Unity\\Editor\\Editor.log."));
            page.Controls.Add(t);
            return page;
        }

        TabPage RecordTab()
        {
            var page = new TabPage("Запись");
            var t = new RowsPanel();
            _chkAutoSave = new CheckBox { Text = "Автоматически сохранять клип при ошибке", AutoSize = true, Checked = _s.AutoSave };
            t.Full(_chkAutoSave);

            _hkReplay = new HotkeyBox { Text = _s.ReplayHotkey };
            var test = SmallButton("Тест (через 3 с)");
            test.Click += (o, e) =>
            {
                if (!Apply()) return;
                _app.ManualClip(3);
                _lblTestRun.Text = "Через 3 секунды нажму " + _hkReplay.Text + ". Переключись в игру — результат появится в виджете.";
            };
            t.Row("Клавиша NVIDIA:", _hkReplay, test);
            _lblTestRun = t.Hint("Нажми в поле и затем нужное сочетание. «Тест» сохранит настройки, нажмёт клавишу и поищет новый ролик.");

            _cmbInput = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300, Anchor = AnchorStyles.Left };
            _cmbInput.Items.Add("Скан-коды (как настоящая клавиатура)");
            _cmbInput.Items.Add("Виртуальные коды клавиш");
            _cmbInput.SelectedIndex = _s.InputMode == "vk" ? 1 : 0;
            t.Row("Способ нажатия:", _cmbInput, null);
            _numHold = Num(10, 500, _s.KeyHoldMs, 0, 10);
            t.Row("Удерживать клавишу, мс:", _numHold, null);

            _numDelay = Num(0, 30, (decimal)_s.SaveDelaySec, 1, 0.5m);
            t.Row("Задержка после ошибки, с:", _numDelay, null);
            _numCooldown = Num(5, 1185, (decimal)_s.CooldownSec, 0, 5);
            t.Row("Не чаще одного клипа раз в, с:", _numCooldown, null);
            _numRepeat = Num(10, 86400, (decimal)_s.RepeatGapSec, 0, 30);
            t.Row("Та же ошибка снова пишется, если молчала, с:", _numRepeat, null);
            _numReplay = Num(30, 1200, (decimal)_s.ReplayLengthSec, 0, 30);
            t.Row("Длина буфера в NVIDIA App, с:", _numReplay, null);
            t.Hint(("Задержка нужна, чтобы в ролик попали последствия ошибки. Ошибки, случившиеся пока клип ждёт своей очереди, " +
                         "попадают в тот же клип. Краш движка сохраняется сразу, без задержки. " +
                         "Если игра запущена от администратора — запусти тулзу тоже от администратора, иначе нажатия не дойдут."));
            page.Controls.Add(t);
            return page;
        }

        TabPage ClipsTab()
        {
            var page = new TabPage("Клипы");
            var t = new RowsPanel();
            _chkLink = new CheckBox { Text = "Переименовывать ролик по ошибке и класть рядом .txt со стеком", AutoSize = true, Checked = _s.LinkClips };
            t.Full(_chkLink);
            _txtClipFolder = new TextBox { Text = _s.ClipFolder };
            var browse = SmallButton("Обзор…");
            browse.Click += (o, e) =>
            {
                using (var d = new FolderBrowserDialog { Description = "Папка, куда NVIDIA App сохраняет видео", ShowNewFolderButton = false })
                {
                    var cur = AppSettings.Expand(_txtClipFolder.Text);
                    if (Directory.Exists(cur)) d.SelectedPath = cur;
                    if (d.ShowDialog(this) == DialogResult.OK) _txtClipFolder.Text = d.SelectedPath;
                }
            };
            t.Row("Папка клипов:", _txtClipFolder, browse);
            var open = SmallButton("Открыть папку");
            open.Click += (o, e) => AppController.OpenFolder(AppSettings.Expand(_txtClipFolder.Text));
            t.Row(null, open, null);
            _numWait = Num(5, 600, (decimal)_s.ClipWaitSec, 0, 5);
            t.Row("Ждать появления ролика, с:", _numWait, null);
            t.Hint(("Где NVIDIA App хранит видео: NVIDIA App → Настройки → Запись → «Расположение видео». " +
                         "Тулза ищет новый ролик во всех подпапках (до 4 уровней), дожидается, пока NVIDIA его допишет, " +
                         "и переименовывает в вид 2026-10-15_23-40-12_NRE.mp4. Рядом кладёт .txt: стек ошибки и всё, что было в логе за время буфера."));
            page.Controls.Add(t);
            return page;
        }

        TabPage RulesTab()
        {
            var page = new TabPage("Правила");
            var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(8) };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            _rules = new BindingList<RuleConfig>(_s.Rules.Select(r => r.Clone()).ToList());
            _grid = new DataGridView
            {
                Dock = DockStyle.Fill, AutoGenerateColumns = false, AllowUserToAddRows = false, AllowUserToResizeRows = false,
                RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false,
                BackgroundColor = SystemColors.Window, BorderStyle = BorderStyle.FixedSingle,
            };
            _grid.Columns.Add(new DataGridViewCheckBoxColumn { DataPropertyName = "Enabled", HeaderText = "Вкл", Width = 38 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "Name", HeaderText = "Название", Width = 150 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "Pattern", HeaderText = "Регулярное выражение", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = 120 });
            var mode = new DataGridViewComboBoxColumn { DataPropertyName = "Mode", HeaderText = "Режим", Width = 72, FlatStyle = FlatStyle.Flat };
            mode.Items.Add("line");
            mode.Items.Add("marker");
            _grid.Columns.Add(mode);
            _grid.Columns.Add(new DataGridViewCheckBoxColumn { DataPropertyName = "SaveClip", HeaderText = "Клип", Width = 42 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "Tag", HeaderText = "Метка", Width = 60 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "Color", HeaderText = "Цвет", Width = 70 });
            _grid.DataError += (o, e) => { e.ThrowException = false; };
            _grid.DataSource = _rules;
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            t.Controls.Add(_grid, 0, 0);

            var add = SmallButton("Добавить");
            add.Click += (o, e) =>
            {
                _rules.Add(new RuleConfig { Enabled = true, Name = "Новое правило", Pattern = "^MyError:", Mode = "line", SaveClip = false, Tag = "MY", Color = "#66D9E8" });
                _grid.CurrentCell = _grid.Rows[_grid.Rows.Count - 1].Cells[2];
                _grid.BeginEdit(true);
            };
            var del = SmallButton("Удалить");
            del.Click += (o, e) =>
            {
                if (_grid.CurrentRow != null && _grid.CurrentRow.Index >= 0 && _grid.CurrentRow.Index < _rules.Count)
                    _rules.RemoveAt(_grid.CurrentRow.Index);
            };
            var reset = SmallButton("Стандартные правила");
            reset.Click += (o, e) =>
            {
                if (MessageBox.Show(this, "Вернуть правила по умолчанию? Свои правила будут удалены.", "Crash Catcher",
                        MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
                _rules.Clear();
                foreach (var r in RuleConfig.Defaults()) _rules.Add(r);
            };
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.Controls.Add(Flow(add, del, reset), 0, 1);

            _txtTestLine = new TextBox { Width = 400, Anchor = AnchorStyles.Left | AnchorStyles.Right };
            var check = SmallButton("Проверить строку");
            check.Click += (o, e) => TestLine();
            var testRow = new TableLayoutPanel { ColumnCount = 2, Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(0, 4, 0, 0) };
            testRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            testRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            testRow.Controls.Add(_txtTestLine, 0, 0);
            testRow.Controls.Add(check, 1, 0);
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.Controls.Add(testRow, 0, 2);
            _lblTest = Hint("Вставь строку из лога и нажми «Проверить строку» — увидишь, какое правило сработает.");
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.Controls.Add(_lblTest, 0, 3);
            var h = Hint("Режим line — сама строка является ошибкой (группы (?<type>…) и (?<msg>…) необязательны). " +
                         "Режим marker — строка стека (например UnityEngine.Debug:LogError), текст ошибки берётся строкой выше. " +
                         "«Клип» — сохранять ли видео по этому правилу.");
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.Controls.Add(h, 0, 4);
            t.RowCount = 5;
            WrapLabels(t, _lblTest, h);
            page.Controls.Add(t);
            return page;
        }

        TabPage WidgetTab()
        {
            var page = new TabPage("Виджет");
            var t = new RowsPanel();
            _hkVisible = new HotkeyBox { Text = _s.HotkeyToggleVisible };
            t.Row("Показать / скрыть:", _hkVisible, null);
            _hkPin = new HotkeyBox { Text = _s.HotkeyTogglePin };
            t.Row("Закрепить / открепить:", _hkPin, null);
            _hkClear = new HotkeyBox { Text = _s.HotkeyClear };
            t.Row("Очистить консоль:", _hkClear, null);
            t.Hint(("Закреплённый виджет пропускает клики в игру. Открепить его можно этим сочетанием или из меню значка в трее. " +
                         "Backspace в поле — убрать сочетание."));
            _numFont = Num(8, 24, (decimal)_s.FontSize, 0, 1);
            t.Row("Размер шрифта:", _numFont, null);
            _numMax = Num(20, 10000, _s.MaxItems, 0, 50);
            t.Row("Строк в консоли (макс.):", _numMax, null);
            _chkShowInfo = new CheckBox { Text = "Показывать служебные сообщения (слежу за…, клип сохранён…)", AutoSize = true, Checked = _s.ShowInfo };
            t.Full(_chkShowInfo);
            _chkAutostart = new CheckBox { Text = "Запускать вместе с Windows", AutoSize = true, Checked = GetAutostart() };
            t.Full(_chkAutostart);
            t.Hint(("Поверх игры виджет виден в оконном и безрамочном режиме (Unity: Fullscreen Window). " +
                         "В эксклюзивном полноэкранном режиме игра перекрывает любые окна."));
            page.Controls.Add(t);
            return page;
        }

        // ------------------------------------------------------------ действия

        void BrowseLog()
        {
            using (var d = new OpenFileDialog { Title = "Файл лога игры", Filter = "Логи (*.log)|*.log|Все файлы (*.*)|*.*", CheckFileExists = false })
            {
                var cur = AppSettings.Expand(_txtLog.Text);
                try
                {
                    if (!string.IsNullOrEmpty(cur) && Directory.Exists(Path.GetDirectoryName(cur))) d.InitialDirectory = Path.GetDirectoryName(cur);
                    else d.InitialDirectory = LocalLow();
                }
                catch { }
                if (d.ShowDialog(this) == DialogResult.OK) _txtLog.Text = d.FileName;
            }
        }

        static string LocalLow()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow");
        }

        void ShowFoundLogs(Control anchor)
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

            var menu = new ContextMenuStrip();
            if (found.Count == 0) menu.Items.Add(new ToolStripMenuItem("Не нашёл ни одного Player.log в AppData\\LocalLow") { Enabled = false });
            foreach (var f in found.OrderByDescending(x => x.Item3).Take(20))
            {
                var item = new ToolStripMenuItem(f.Item1 + "   —   " + f.Item3.ToString("dd.MM HH:mm")) { ToolTipText = f.Item2, Tag = f.Item2 };
                item.Click += (o, e) => _txtLog.Text = (string)((ToolStripMenuItem)o).Tag;
                menu.Items.Add(item);
            }
            menu.Show(anchor, new Point(0, anchor.Height));
        }

        void TestLine()
        {
            _grid.EndEdit();
            var line = _txtTestLine.Text;
            if (string.IsNullOrEmpty(line)) { _lblTest.Text = "Вставь строку из лога в поле слева."; return; }
            foreach (var r in _rules)
            {
                if (!r.Enabled || string.IsNullOrWhiteSpace(r.Pattern)) continue;
                Match m;
                try { m = Regex.Match(line, r.Pattern); }
                catch (Exception ex) { _lblTest.Text = "Ошибка в регулярке «" + r.Name + "»: " + ex.Message; return; }
                if (!m.Success) continue;
                string type = m.Groups["type"].Success ? m.Groups["type"].Value : r.Tag;
                string abbr = m.Groups["type"].Success ? LogParser.Abbreviate(type) : r.Tag;
                string msg = m.Groups["msg"].Success ? m.Groups["msg"].Value.TrimStart(':', ' ') : line;
                _lblTest.Text = r.IsMarker
                    ? "Сработает «" + r.Name + "» (маркер стека): ошибкой станет строка над этой. Клип: " + (r.SaveClip ? "да" : "нет") + "."
                    : "Сработает «" + r.Name + "»: " + abbr + " (" + type + "), сообщение: " + msg + ". Клип: " + (r.SaveClip ? "да" : "нет") + ".";
                return;
            }
            _lblTest.Text = "Ни одно включённое правило не сработает на эту строку.";
        }

        bool Apply()
        {
            _grid.EndEdit();
            var n = _s.Clone();
            n.LogPath = _txtLog.Text.Trim().Trim('"');
            n.ReadExistingOnStart = _chkReadExisting.Checked;
            n.PollMs = (int)_numPoll.Value;
            n.AutoSave = _chkAutoSave.Checked;
            n.ReplayHotkey = _hkReplay.Text.Trim();
            n.InputMode = _cmbInput.SelectedIndex == 1 ? "vk" : "scancode";
            n.KeyHoldMs = (int)_numHold.Value;
            n.SaveDelaySec = (double)_numDelay.Value;
            n.CooldownSec = (double)_numCooldown.Value;
            n.RepeatGapSec = (double)_numRepeat.Value;
            n.ReplayLengthSec = (double)_numReplay.Value;
            n.LinkClips = _chkLink.Checked;
            n.ClipFolder = _txtClipFolder.Text.Trim().Trim('"');
            n.ClipWaitSec = (double)_numWait.Value;
            n.HotkeyToggleVisible = _hkVisible.Text.Trim();
            n.HotkeyTogglePin = _hkPin.Text.Trim();
            n.HotkeyClear = _hkClear.Text.Trim();
            n.FontSize = (float)_numFont.Value;
            n.MaxItems = (int)_numMax.Value;
            n.ShowInfo = _chkShowInfo.Checked;
            n.Rules = _rules.Select(r => r.Clone()).ToList();

            foreach (var r in n.Rules)
            {
                if (!r.Enabled) continue;
                try { new Regex(r.Pattern ?? ""); }
                catch (Exception ex)
                {
                    _tabs.SelectedTab = _rulesTab;
                    MessageBox.Show(this, "Ошибка в регулярном выражении правила «" + r.Name + "»:\n" + ex.Message, "Crash Catcher", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return false;
                }
            }
            Hotkey hk;
            if (!Hotkey.TryParse(n.ReplayHotkey, out hk))
            {
                _tabs.SelectedIndex = 1;
                MessageBox.Show(this, "Не понял сочетание для NVIDIA: «" + n.ReplayHotkey + "». Пример: Alt+F10.", "Crash Catcher", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            n.ReplayHotkey = hk.ToString();
            foreach (var pair in new[] { new[] { n.HotkeyToggleVisible, "показать/скрыть" }, new[] { n.HotkeyTogglePin, "закрепить/открепить" }, new[] { n.HotkeyClear, "очистить консоль" } })
            {
                if (string.IsNullOrEmpty(pair[0])) continue;
                Hotkey h2;
                if (!Hotkey.TryParse(pair[0], out h2))
                {
                    _tabs.SelectedIndex = 4;
                    MessageBox.Show(this, "Не понял сочетание «" + pair[0] + "» (" + pair[1] + ").", "Crash Catcher", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return false;
                }
                if (h2.ToString() == n.ReplayHotkey)
                {
                    _tabs.SelectedIndex = 4;
                    MessageBox.Show(this, "Сочетание " + h2 + " совпадает с клавишей NVIDIA.", "Crash Catcher", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return false;
                }
            }
            var used = new List<string>();
            foreach (var k in new[] { n.HotkeyToggleVisible, n.HotkeyTogglePin, n.HotkeyClear })
            {
                if (string.IsNullOrEmpty(k)) continue;
                Hotkey hp;
                var norm = Hotkey.TryParse(k, out hp) ? hp.ToString() : k;
                if (used.Contains(norm))
                {
                    _tabs.SelectedIndex = 4;
                    MessageBox.Show(this, "Сочетание " + norm + " назначено на два действия.", "Crash Catcher", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return false;
                }
                used.Add(norm);
            }
            try { SetAutostart(_chkAutostart.Checked); } catch { }
            _app.ApplySettings(n);
            return true;
        }

        // ------------------------------------------------------------ автозапуск

        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        static bool GetAutostart()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                    return k != null && k.GetValue("CrashCatcher") != null;
            }
            catch { return false; }
        }

        static void SetAutostart(bool on)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (k == null) return;
                if (on) k.SetValue("CrashCatcher", "\"" + Application.ExecutablePath + "\"");
                else k.DeleteValue("CrashCatcher", false);
            }
        }

        // ------------------------------------------------------------ разметка

        static Label Hint(string text)
        {
            return new Label { Text = text, AutoSize = true, MaximumSize = new Size(560, 0), ForeColor = SystemColors.GrayText, Margin = new Padding(3, 6, 3, 6) };
        }

        /// <summary>Подгоняет ширину переноса у подписей под ширину контейнера.</summary>
        static void WrapLabels(Control container, params Label[] labels)
        {
            container.Resize += (o, e) =>
            {
                int w = Math.Max(100, container.ClientSize.Width - container.Padding.Horizontal - 12);
                foreach (var l in labels) l.MaximumSize = new Size(w, 0);
            };
        }

        static Button SmallButton(string text)
        {
            return new Button { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(6, 1, 6, 1) };
        }

        static FlowLayoutPanel Flow(params Control[] cs)
        {
            var f = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new Padding(0) };
            foreach (var c in cs) f.Controls.Add(c);
            return f;
        }

        static NumericUpDown Num(decimal min, decimal max, decimal value, int decimals, decimal increment)
        {
            var n = new NumericUpDown { Minimum = min, Maximum = max, DecimalPlaces = decimals, Increment = increment, Width = 90, Anchor = AnchorStyles.Left };
            n.Value = Math.Max(min, Math.Min(max, value));
            return n;
        }
    }

    /// <summary>Поле, которое запоминает нажатое сочетание клавиш.</summary>
    sealed class HotkeyBox : TextBox
    {
        public HotkeyBox()
        {
            ReadOnly = true;
            BackColor = SystemColors.Window;
            ShortcutsEnabled = false;
            Width = 200;
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            var key = keyData & Keys.KeyCode;
            var mods = keyData & Keys.Modifiers;
            if (key == Keys.Tab && mods == Keys.None) return base.ProcessCmdKey(ref msg, keyData);
            if (key == Keys.ShiftKey || key == Keys.ControlKey || key == Keys.Menu || key == Keys.LWin || key == Keys.RWin) return true;
            if ((key == Keys.Back || key == Keys.Delete) && mods == Keys.None) { Text = ""; return true; }
            var parts = new List<string>();
            if ((mods & Keys.Control) != 0) parts.Add("Ctrl");
            if ((mods & Keys.Shift) != 0) parts.Add("Shift");
            if ((mods & Keys.Alt) != 0) parts.Add("Alt");
            parts.Add(Hotkey.NameOf((int)key) ?? key.ToString());
            Text = string.Join("+", parts.ToArray());
            SelectionStart = Text.Length;
            return true;
        }
    }
}

namespace CrashCatcher.Win
{
    /// <summary>
    /// Простая ручная раскладка «подпись — поле — кнопка» по строкам.
    /// Не зависит от причуд TableLayoutPanel при разных DPI.
    /// </summary>
    sealed class RowsPanel : Panel
    {
        sealed class Item { public Label L; public Control C, X; public bool Full, IsHint, Stretch; public int Want; }
        readonly List<Item> _items = new List<Item>();

        public RowsPanel()
        {
            Dock = DockStyle.Fill;
            AutoScroll = true;
            Padding = new Padding(12, 12, 12, 8);
        }

        public void Row(string label, Control c, Control extra)
        {
            var it = new Item { C = c, X = extra, Stretch = c is TextBox && !(c is HotkeyBox), Want = c.Width };
            if (label != null)
            {
                it.L = new Label { Text = label, AutoSize = false, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
                Controls.Add(it.L);
            }
            Controls.Add(c);
            if (extra != null) Controls.Add(extra);
            _items.Add(it);
        }

        public void Full(Control c)
        {
            _items.Add(new Item { C = c, Full = true });
            Controls.Add(c);
        }

        public Label Hint(string text)
        {
            var l = new Label { Text = text, AutoSize = false, ForeColor = SystemColors.GrayText };
            _items.Add(new Item { C = l, IsHint = true });
            Controls.Add(l);
            return l;
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            float k = DeviceDpi / 96f;
            if (k < 1) k = 1;
            int gap = (int)(8 * k);
            int w = Math.Max((int)(200 * k), ClientSize.Width - Padding.Horizontal);
            int x0 = Padding.Left + AutoScrollPosition.X;
            int y = Padding.Top + AutoScrollPosition.Y;

            int labelW = 0;
            foreach (var it in _items)
                if (it.L != null) labelW = Math.Max(labelW, TextRenderer.MeasureText(it.L.Text, it.L.Font).Width + (int)(4 * k));
            labelW = Math.Min(labelW, (int)(w * 0.5));
            int cx0 = x0 + (labelW > 0 ? labelW + gap : 0);

            foreach (var it in _items)
            {
                if (it.IsHint)
                {
                    var sz = TextRenderer.MeasureText(it.C.Text, it.C.Font, new Size(w, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl);
                    it.C.SetBounds(x0, y, w, sz.Height + (int)(4 * k));
                    y += sz.Height + (int)(4 * k) + gap;
                    continue;
                }
                if (it.Full)
                {
                    var ps = it.C.GetPreferredSize(new Size(w, 0));
                    it.C.SetBounds(x0, y, Math.Min(w, ps.Width), ps.Height);
                    y += ps.Height + gap;
                    continue;
                }
                int ch = it.C.GetPreferredSize(Size.Empty).Height;
                if (it.C is TextBox || it.C is NumericUpDown || it.C is ComboBox) ch = it.C.Height;
                int rowH = ch;
                Size xs = Size.Empty;
                if (it.X != null) { xs = it.X.GetPreferredSize(Size.Empty); rowH = Math.Max(rowH, xs.Height); }
                int lh = it.L != null ? TextRenderer.MeasureText("Ag", it.L.Font).Height + (int)(4 * k) : 0;
                rowH = Math.Max(rowH, lh);

                if (it.L != null) it.L.SetBounds(x0, y + (rowH - lh) / 2, labelW, lh);
                int right = x0 + w;
                if (it.X != null)
                {
                    it.X.SetBounds(right - xs.Width, y + (rowH - xs.Height) / 2, xs.Width, xs.Height);
                    right -= xs.Width + gap;
                }
                int cw;
                if (it.Stretch) cw = Math.Max((int)(60 * k), right - cx0);
                else
                {
                    // ширина в логических пикселях, запомненная при добавлении (иначе поле «сжимается» навсегда)
                    int want = (it.C is NumericUpDown || it.C is ComboBox || it.C is TextBox)
                        ? (int)(it.Want * k)
                        : it.C.GetPreferredSize(Size.Empty).Width;
                    cw = Math.Max(10, Math.Min(want, right - cx0));
                }
                it.C.SetBounds(cx0, y + (rowH - ch) / 2, cw, ch);
                y += rowH + gap;
            }
            var min = new Size(0, y - AutoScrollPosition.Y + Padding.Bottom);
            if (AutoScrollMinSize != min) AutoScrollMinSize = min;
        }
    }
}
