using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using CrashCatcher.Core;

public static class CoreTests
{
    static int _fail, _pass;

    static void Check(bool cond, string what)
    {
        if (cond) { _pass++; }
        else { _fail++; Console.WriteLine("  FAIL: " + what); }
    }

    static void Eq<T>(T expected, T actual, string what)
    {
        Check(EqualityComparer<T>.Default.Equals(expected, actual), what + " — ожидалось [" + expected + "], получено [" + actual + "]");
    }

    public static int Run()
    {
        var tests = new List<KeyValuePair<string, Action>>
        {
            T("Parser: Player.log (Mono) — NRE/MRE/IOE/LogError", ParserPlayerLog),
            T("Parser: Editor.log формат", ParserEditorLog),
            T("Parser: InputSystem + исключения без пустых строк", ParserNoBlankLines),
            T("Parser: нативный краш с пустыми строками", ParserCrash),
            T("Parser: LogError в релизном Player.log", ParserReleaseLogError),
            T("Parser: idle-закрытие и хвост", ParserIdle),
            T("Abbreviate", Abbrev),
            T("UE5: ensure с колстеком (реальный лог)", UeEnsure),
            T("UE5: краш appError → Critical error — одна запись", UeCrash),
            T("UE5: Accessed None, BP Runtime Error, обычный Error", UeBlueprint),
            T("Settings: старые настройки без UE-полей", SettingsUpgrade),
            T("LogFinder: недавние проекты UE и собранные игры", FinderUe),
            T("Book + Planner: спам одной ошибкой", PlannerSpam),
            T("Planner: кулдаун и объединение ошибок", PlannerCooldown),
            T("Planner: краш — сразу", PlannerImmediate),
            T("Planner: ручное сохранение", PlannerManual),
            T("Tailer: дочитывание, частичные строки, пересоздание", TailerBasic),
            T("Tailer: файл появился позже", TailerAppears),
            T("Linker: поиск, ожидание, переименование, .txt", Linker),
            T("Settings: JSON туда-обратно и значения по умолчанию", SettingsRoundTrip),
            T("Hotkey: разбор", HotkeyParse),
        };
        foreach (var t in tests)
        {
            int before = _fail;
            try { t.Value(); }
            catch (Exception ex) { _fail++; Console.WriteLine("  EXCEPTION: " + ex); }
            Console.WriteLine((_fail == before ? "[ OK ] " : "[FAIL] ") + t.Key);
        }
        Console.WriteLine();
        Console.WriteLine("passed checks: " + _pass + ", failed: " + _fail);
        return _fail;
    }

    static KeyValuePair<string, Action> T(string n, Action a) { return new KeyValuePair<string, Action>(n, a); }

    static List<ParsedEntry> Parse(string text, List<RuleConfig> rules = null)
    {
        var p = new LogParser(rules ?? RuleConfig.Defaults());
        var list = new List<ParsedEntry>();
        p.Entry += list.Add;
        foreach (var l in text.Replace("\r\n", "\n").Split('\n')) p.Feed(l);
        p.Flush();
        return list;
    }

    const string PlayerLog = @"Mono path[0] = 'C:/Games/DR/DR_Data/Managed'
Initialize engine version: 2022.3.20f1 (61c2feb0970d)
GfxDevice: creating device client; threaded=1; jobified=1
Direct3D:
    Version:  Direct3D 11.0 [level 11.1]
Begin MonoManager ReloadAssembly
- Loaded All Assemblies, in  0.123 seconds
UnloadTime: 0.512300 ms
NullReferenceException: Object reference not set to an instance of an object
  at CarController.FixedUpdate () [0x00012] in <a1b2c3d4e5f6>:0

MissingReferenceException: The object of type 'Rigidbody' has been destroyed but you are still trying to access it.
Your script should either check if it is null or you should not destroy the object.
  at (wrapper managed-to-native) UnityEngine.Rigidbody.get_velocity_Injected(UnityEngine.Rigidbody,UnityEngine.Vector3&)
  at UnityEngine.Rigidbody.get_velocity () [0x00000] in <hash>:0
  at Speedometer.Update () [0x00001] in <hash>:0

Player connected
Wheel friction too low
UnityEngine.Debug:LogError(Object)
WheelSetup:Awake() (at Assets/Scripts/WheelSetup.cs:42)

(Filename: Assets/Scripts/WheelSetup.cs Line: 42)

InvalidOperationException: Collection was modified; enumeration operation may not execute.
  at System.Collections.Generic.List`1+Enumerator[T].MoveNextRare () [0x00013] in <hash>:0
  at RaceManager.Update () [0x0001a] in <hash>:0

Setting up 4 worker threads for Enlighten.
Exception in some text that is not at line start
";

    static void ParserPlayerLog()
    {
        var e = Parse(PlayerLog);
        Eq(4, e.Count, "число записей");
        if (e.Count < 4) return;
        Eq("NullReferenceException", e[0].Type, "NRE type");
        Eq("NRE", e[0].Abbr, "NRE abbr");
        Eq("Object reference not set to an instance of an object", e[0].Message, "NRE msg");
        Eq(1, e[0].Stack.Count, "NRE stack");
        Eq("MRE", e[1].Abbr, "MRE abbr");
        Check(e[1].Message.Contains("Your script should either"), "MRE сообщение в две строки");
        Eq(3, e[1].Stack.Count, "MRE stack");
        Eq("ERR", e[2].Abbr, "LogError");
        Eq("Wheel friction too low", e[2].Message, "LogError msg");
        Check(e[2].Stack.Any(s => s.Contains("WheelSetup:Awake")), "LogError stack");
        Check(!e[2].Stack.Any(s => s.StartsWith("(Filename")), "без строки Filename");
        Eq("IOE", e[3].Abbr, "IOE abbr");
        Eq(2, e[3].Stack.Count, "IOE stack");
        Check(e[3].Rule.SaveClip && e[0].Rule.SaveClip && !e[2].Rule.SaveClip, "флаги клипа");
    }

    static void ParserEditorLog()
    {
        var text = @"Reloading assemblies after forced synchronous recompile.
NullReferenceException: Object reference not set to an instance of an object
CarController.FixedUpdate () (at Assets/Scripts/CarController.cs:57)

Assertion failed: wheel count mismatch
UnityEngine.Debug:Assert (bool,object)
WheelSetup:Start () (at Assets/Scripts/WheelSetup.cs:12)

Just a log line (with parens)
UnityEngine.Debug:Log (object)
Foo:Bar () (at Assets/Foo.cs:1)

UnityException: Transform child out of bounds
UnityEngine.Transform.GetChild (int) (at <hash>:0)
Garage.Open () (at Assets/Garage.cs:20)
";
        var e = Parse(text);
        Eq(3, e.Count, "число записей");
        if (e.Count < 3) return;
        Eq("NRE", e[0].Abbr, "NRE");
        Eq(1, e[0].Stack.Count, "NRE stack (editor)");
        Eq("ASSERT", e[1].Abbr, "assert");
        Eq("Assertion failed: wheel count mismatch", e[1].Message, "assert msg");
        Eq("UE", e[2].Abbr, "UnityException");
        Eq(2, e[2].Stack.Count, "UE stack");
    }

    static void ParserNoBlankLines()
    {
        var text = @"MissingReferenceException while executing 'performed' callbacks of 'Player/Boost[/Keyboard/leftShift]'
UnityEngine.InputSystem.LowLevel.NativeInputRuntime/<>c__DisplayClass7_0:<set_onUpdate>b__0 (UnityEngineInternal.Input.NativeInputUpdateType,UnityEngineInternal.Input.NativeInputEventBuffer*)
UnityEngineInternal.Input.NativeInputSystem:NotifyUpdate (UnityEngineInternal.Input.NativeInputUpdateType,intptr)
Lap 3 started
System.ArgumentOutOfRangeException: Index was out of range. Must be non-negative and less than the size of the collection.
Parameter name: index
  at System.Collections.Generic.List`1[T].get_Item (System.Int32 index) [0x00009] in <hash>:0
  at LapTimer.Record () [0x00000] in <hash>:0
Lap 4 started";
        var e = Parse(text);
        Eq(2, e.Count, "число записей");
        if (e.Count < 2) return;
        Eq("MRE", e[0].Abbr, "InputSystem MRE");
        Check(e[0].Message.StartsWith("while executing"), "msg: " + e[0].Message);
        Eq(2, e[0].Stack.Count, "MRE stack, строка 'Lap 3' не попала");
        Eq("ArgumentOutOfRangeException", e[1].Type, "полное имя без namespace");
        Eq("AOORE", e[1].Abbr, "AOORE");
        Check(e[1].Message.Contains("Parameter name: index"), "многострочное сообщение");
        Eq(2, e[1].Stack.Count, "AOORE stack");
    }

    static void ParserCrash()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Some line");
        sb.AppendLine("Crash!!!");
        sb.AppendLine("SymInit: Symbol-SearchPath: 'C:/Games/DR;.;', symOptions: 534, UserName: 'nv'");
        sb.AppendLine("OS-Version: 10.0.0");
        for (int i = 0; i < 150; i++) sb.AppendLine("C:\\Windows\\System32\\mod" + i + ".dll:mod" + i + ".dll (00007FFB00000000), size: 1000 (result: 0), SymType: '-exported-'");
        sb.AppendLine();
        sb.AppendLine("========== OUTPUTTING STACK TRACE ==================");
        sb.AppendLine();
        sb.AppendLine("0x00007FFB1A2B3C4D (UnityPlayer) UnityMain");
        sb.AppendLine("0x00007FFB1A2B3C4E (mono-2.0-bdwgc) mono_get_runtime_build_info");
        sb.AppendLine();
        sb.AppendLine("========== END OF STACKTRACE ===========");
        var e = Parse(sb.ToString());
        Eq(1, e.Count, "один краш");
        if (e.Count < 1) return;
        Eq("CRASH", e[0].Abbr, "crash abbr");
        Check(e[0].Rule.Immediate, "immediate");
        Check(e[0].Stack.Any(s => s.Contains("UnityMain")), "стек краша сохранился");
        Check(e[0].Stack.Any(s => s.Contains("пропущено")), "список модулей сжат");
    }

    static void ParserReleaseLogError()
    {
        var text = @"Boot complete
Track asset missing: Track_03
UnityEngine.DebugLogHandler:Internal_Log(LogType, LogOption, String, Object)
UnityEngine.Logger:Log(LogType, Object)
UnityEngine.Debug:LogError(Object)
TrackLoader:Load(String)

(Filename: C:\build\output\Runtime/Export/Debug/Debug.bindings.h Line: 39)
";
        var e = Parse(text);
        Eq(1, e.Count, "одна запись");
        if (e.Count < 1) return;
        Eq("Track asset missing: Track_03", e[0].Message, "сообщение над внутренними кадрами");
        Check(e[0].Stack.Any(s => s.Contains("TrackLoader:Load")), "стек после маркера");
        var sig = ErrorBook.SignatureOf(e[0]);
        Check(sig.Contains("TrackLoader"), "сигнатура по пользовательскому кадру: " + sig);
    }

    static void ParserIdle()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0);
        var p = new LogParser(RuleConfig.Defaults()) { Clock = () => now };
        var got = new List<ParsedEntry>();
        p.Entry += got.Add;
        p.Feed("NullReferenceException: boom");
        p.Feed("  at A.B () [0x0] in <x>:0");
        p.Tick();
        Eq(0, got.Count, "ещё открыта");
        now = now.AddMilliseconds(700);
        p.Tick();
        Eq(1, got.Count, "закрыта по тишине");
    }

    static void Abbrev()
    {
        Eq("NRE", LogParser.Abbreviate("NullReferenceException"), "NRE");
        Eq("MRE", LogParser.Abbreviate("MissingReferenceException"), "MRE");
        Eq("IOE", LogParser.Abbreviate("InvalidOperationException"), "IOE");
        Eq("EXC", LogParser.Abbreviate("Exception"), "Exception");
    }

    static ParsedEntry Entry(string type, string msg, string frame, DateTime t, RuleConfig rule = null)
    {
        var e = new ParsedEntry { Rule = rule ?? RuleConfig.Defaults()[0], Type = type, Abbr = LogParser.Abbreviate(type), Message = msg, Time = t };
        e.Stack.Add(frame);
        return e;
    }

    static void PlannerSpam()
    {
        var book = new ErrorBook();
        var pl = new ClipPlanner { DelaySec = 3, CooldownSec = 60, RepeatGapSec = 300 };
        var t0 = new DateTime(2026, 1, 1, 12, 0, 0);
        int presses = 0;
        ClipJob first = null;
        // 10 секунд NRE каждые 16 мс
        for (int ms = 0; ms < 10000; ms += 16)
        {
            var now = t0.AddMilliseconds(ms);
            bool isNew; DateTime prev;
            var r = book.Add(Entry("NullReferenceException", "Object reference not set to an instance of an object", "  at Car.Update () [0x0] in <x>:0", now), out isNew, out prev);
            var j = pl.Consider(r, isNew, prev, now);
            if (j != null && first == null) first = j;
            if (pl.TakeDue(now) != null) presses++;
        }
        Eq(1, book.Items.Count, "одна строка в консоли");
        Eq(625, book.Items[0].Count, "счётчик");
        Eq(1, presses, "одно нажатие на весь спам");
        Check(first != null && first.Records.Count == 1, "клип с одной ошибкой");
        // через 6 минут тишины та же ошибка — новый клип
        var later = t0.AddMinutes(6);
        bool n2; DateTime p2;
        var r2 = book.Add(Entry("NullReferenceException", "Object reference not set to an instance of an object", "  at Car.Update () [0x0] in <x>:0", later), out n2, out p2);
        Check(pl.Consider(r2, n2, p2, later) != null, "после паузы — снова клип");
        // та же ошибка в другом месте — отдельная строка
        bool n3; DateTime p3;
        var r3 = book.Add(Entry("NullReferenceException", "Object reference not set to an instance of an object", "  at Garage.Open () [0x0] in <x>:0", later), out n3, out p3);
        Check(n3 && r3 != r2, "другой кадр стека — другая строка");
        // цифры в сообщении не плодят строки
        bool n4; DateTime p4;
        book.Add(Entry("IndexOutOfRangeException", "Index 5 out of range", "  at A.B ()", later), out n4, out p4);
        book.Add(Entry("IndexOutOfRangeException", "Index 7 out of range", "  at A.B ()", later), out n4, out p4);
        Check(!n4, "Index 5/7 — одна строка");
    }

    static void PlannerCooldown()
    {
        var book = new ErrorBook();
        var pl = new ClipPlanner { DelaySec = 3, CooldownSec = 60, RepeatGapSec = 300 };
        var t0 = new DateTime(2026, 1, 1, 12, 0, 0);
        bool isNew; DateTime prev;
        var a = book.Add(Entry("NullReferenceException", "a", "at A", t0), out isNew, out prev);
        var j1 = pl.Consider(a, isNew, prev, t0);
        Eq(t0.AddSeconds(3), j1.ScheduledAt, "задержка 3 с");
        var b = book.Add(Entry("InvalidOperationException", "b", "at B", t0.AddSeconds(1)), out isNew, out prev);
        var j1b = pl.Consider(b, isNew, prev, t0.AddSeconds(1));
        Check(j1b == j1 && j1.Records.Count == 2, "вторая ошибка в тот же клип");
        Eq("NRE+1", j1.Tag, "метка клипа");
        Check(pl.TakeDue(t0.AddSeconds(2)) == null, "рано");
        Check(pl.TakeDue(t0.AddSeconds(3)) == j1, "нажатие в 3 с");
        var c = book.Add(Entry("MissingReferenceException", "c", "at C", t0.AddSeconds(10)), out isNew, out prev);
        var j2 = pl.Consider(c, isNew, prev, t0.AddSeconds(10));
        Eq(t0.AddSeconds(63), j2.ScheduledAt, "кулдаун 60 с от прошлого нажатия");
        Check((j2.ScheduledAt - t0.AddSeconds(10)).TotalSeconds < 300, "ошибка останется в 5-минутном буфере");
        pl.AutoSave = false;
        var d = book.Add(Entry("ArgumentException", "d", "at D", t0.AddSeconds(11)), out isNew, out prev);
        Check(pl.Consider(d, isNew, prev, t0.AddSeconds(11)) == null, "автосохранение выключено");
    }

    static void PlannerImmediate()
    {
        var pl = new ClipPlanner { DelaySec = 3, CooldownSec = 60 };
        var book = new ErrorBook();
        var t0 = new DateTime(2026, 1, 1, 12, 0, 0);
        bool isNew; DateTime prev;
        var a = book.Add(Entry("NullReferenceException", "a", "at A", t0), out isNew, out prev);
        pl.Consider(a, isNew, prev, t0);
        pl.TakeDue(t0.AddSeconds(3));
        var crashRule = RuleConfig.Defaults()[1];
        var cr = book.Add(Entry("CRASH", "Crash!!!", "0x0", t0.AddSeconds(4), crashRule), out isNew, out prev);
        var j = pl.Consider(cr, isNew, prev, t0.AddSeconds(4));
        Eq(t0.AddSeconds(3 + ClipPlanner.ImmediateGapSec), j.ScheduledAt, "краш: не ждёт кулдаун, только 5 с между нажатиями");
    }

    static void PlannerManual()
    {
        var pl = new ClipPlanner();
        var t0 = new DateTime(2026, 1, 1, 12, 0, 0);
        var j = pl.Manual(t0, 0);
        Check(j.Manual && pl.TakeDue(t0) == j, "ручной сразу");
        Eq("MANUAL", j.Tag, "метка ручного");
    }

    static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "cc_test_" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(d);
        return d;
    }

    static void TailerBasic()
    {
        var dir = TempDir();
        var path = Path.Combine(dir, "Player.log");
        File.WriteAllText(path, "old line 1\nold line 2\n");
        var t = new LogTailer(path, false, 100);
        var lines = new List<string>();
        var states = new List<TailState>();
        t.Line += lines.Add;
        t.StateChanged += (s, i) => states.Add(s);
        t.PollOnce();
        Eq(0, lines.Count, "старое не читаем");
        Check(states.Contains(TailState.Watching), "watching");

        using (var w = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
        {
            var b = Encoding.UTF8.GetBytes("new 1\r\nnew 2 частич");
            w.Write(b, 0, b.Length); w.Flush();
            t.PollOnce();
            Eq(1, lines.Count, "одна полная строка");
            Eq("new 1", lines[0], "\\r обрезан");
            // разрываем многобайтовый символ
            var rest = Encoding.UTF8.GetBytes("ная строка\n");
            w.Write(rest, 0, 1); w.Flush();
            t.PollOnce();
            w.Write(rest, 1, rest.Length - 1); w.Flush();
            t.PollOnce();
        }
        Eq(2, lines.Count, "вторая строка");
        if (lines.Count > 1) Eq("new 2 частичная строка", lines[1], "UTF-8 на стыке");

        // игра перезапущена: файл пересоздан и короче
        File.Delete(path);
        File.WriteAllText(path, "fresh 1\n");
        t.PollOnce();
        Check(states.Contains(TailState.Reset), "заметили пересоздание");
        Check(lines.Contains("fresh 1"), "читаем новый файл с начала");

        // файл исчез и появился
        File.Delete(path);
        t.PollOnce();
        Check(states.Contains(TailState.Missing), "missing");
        File.WriteAllText(path, "after missing\n");
        t.PollOnce();
        Check(lines.Contains("after missing"), "после пропажи — с начала");
        Directory.Delete(dir, true);
    }

    static void TailerAppears()
    {
        var dir = TempDir();
        var path = Path.Combine(dir, "Player.log");
        var t = new LogTailer(path, false, 100);
        var lines = new List<string>();
        t.Line += lines.Add;
        t.PollOnce();
        File.WriteAllText(path, "first\nsecond\n");
        t.PollOnce();
        Eq(2, lines.Count, "файл, появившийся после старта, читается целиком");
        Directory.Delete(dir, true);
    }

    static void Linker()
    {
        var root = TempDir();
        var game = Path.Combine(root, "NVIDIA", "Drift Racing");
        Directory.CreateDirectory(game);
        var old = Path.Combine(game, "old.mp4");
        File.WriteAllText(old, "old");
        File.SetCreationTimeUtc(old, DateTime.UtcNow.AddHours(-1));
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddHours(-1));
        Directory.SetLastWriteTimeUtc(game, DateTime.UtcNow.AddHours(-1));

        var pressed = DateTime.UtcNow;
        var linker = new ClipLinker { PollMs = 100 };
        var writer = new Thread(() =>
        {
            Thread.Sleep(300);
            var p = Path.Combine(game, "Drift Racing 2026.10.15 - 23.40.12.03.DVR.mp4");
            using (var fs = new FileStream(p, FileMode.Create, FileAccess.Write, FileShare.Read))
            {
                for (int i = 0; i < 5; i++) { fs.Write(new byte[1000], 0, 1000); fs.Flush(); Thread.Sleep(80); }
            }
        });
        writer.Start();
        var res = linker.Run(root, pressed, 5, "2026-10-15_23-40-12_NRE", "stack here", CancellationToken.None, null);
        writer.Join();
        Check(res.Found, "нашёл новый ролик: " + res.Error);
        Check(res.FinalPath != null && res.FinalPath.EndsWith("2026-10-15_23-40-12_NRE.mp4"), "переименован: " + res.FinalPath);
        Check(res.FinalPath != null && File.Exists(res.FinalPath) && new FileInfo(res.FinalPath).Length == 5000, "файл целиком");
        Check(res.TextPath != null && File.ReadAllText(res.TextPath).Contains("stack here"), ".txt рядом");
        Check(File.Exists(old), "старый ролик не тронут");

        var res2 = new ClipLinker { PollMs = 100 }.Run(root, DateTime.UtcNow, 0.5, "x", null, CancellationToken.None, null);
        Check(!res2.Found && res2.Error != null, "нет ролика — понятная ошибка");
        Directory.Delete(root, true);
    }

    static void SettingsRoundTrip()
    {
        var dir = TempDir();
        var path = Path.Combine(dir, "s.json");
        var s = new AppSettings();
        s.LogPath = @"C:\Users\nvrsk\AppData\LocalLow\Studio\Drift Racing\Player.log";
        s.Rules[2].SaveClip = true;
        s.Rules.Add(new RuleConfig { Enabled = true, Name = "Мой", Pattern = "^BUG:", Mode = "line", Tag = "BUG", Color = "#00FF00" });
        s.Save(path);
        var json = File.ReadAllText(path);
        Check(json.Contains("\"LogPath\""), "json читаемый");
        string err;
        var l = AppSettings.Load(path, out err);
        Eq(null, err, "без ошибок");
        Eq(s.LogPath, l.LogPath, "путь");
        Eq(5, l.Rules.Count, "правила не удвоились");
        Check(l.Rules[2].SaveClip, "флаг правила сохранился");
        Eq("Мой", l.Rules[4].Name, "кириллица");

        File.WriteAllText(path, "{\"LogPath\":\"x.log\"}");
        var p = AppSettings.Load(path, out err);
        Eq("x.log", p.LogPath, "частичный json");
        Eq(60.0, p.CooldownSec, "недостающие поля — по умолчанию");
        Eq(4, p.Rules.Count, "правила по умолчанию");
        Eq("Alt+F10", p.ReplayHotkey, "хоткей по умолчанию");

        File.WriteAllText(path, "{ broken");
        var b = AppSettings.Load(path, out err);
        Check(err != null && b != null && File.Exists(path + ".broken"), "битый файл не роняет");
        Directory.Delete(dir, true);
    }

    static void HotkeyParse()
    {
        Hotkey h;
        Check(Hotkey.TryParse("Alt+F10", out h) && h.Alt && !h.Ctrl && h.Vk == 0x79, "Alt+F10");
        Eq("Alt+F10", h.ToString(), "ToString");
        Check(Hotkey.TryParse("ctrl + shift + f12", out h) && h.Ctrl && h.Shift && h.Vk == 0x7B, "ctrl+shift+f12");
        Eq("Ctrl+Shift+F12", h.ToString(), "нормализация");
        Check(!Hotkey.TryParse("Alt+", out h), "пусто");
        Check(!Hotkey.TryParse("F1+F2", out h), "две клавиши");
        Check(Hotkey.TryParse("Alt+Z", out h) && h.Vk == 'Z', "Alt+Z");
    }

    static List<ParsedEntry> ParseUe(string text, bool withIgnore = false)
    {
        var rules = RuleConfig.UnrealDefaults();
        if (!withIgnore) rules.RemoveAll(r => r.IsIgnore);
        var p = new LogParser(rules, EngineProfile.Unreal);
        var list = new List<ParsedEntry>();
        p.Entry += list.Add;
        foreach (var l in text.Replace("\r\n", "\n").Split('\n')) p.Feed(l);
        p.Flush();
        return list;
    }

    const string UeEnsureLog = "\uFEFFLog file open, 11/01/26 12:20:14\n" +
@"[2026.10.31-17.32.24:031][  0]LogStats: FPlatformStackWalk::StackWalkAndDump -  0.076 s
[2026.10.31-17.32.24:032][  0]LogOutputDevice: Error: === Handled ensure: ===
[2026.10.31-17.32.24:032][  0]LogOutputDevice: Error: 
[2026.10.31-17.32.24:032][  0]LogOutputDevice: Error: Ensure condition failed: false  [File:D:\build\++UE5\Sync\Engine\Source\Runtime\Core\Private\HAL\ConsoleManager.cpp] [Line: 4916] 
[2026.10.31-17.32.24:032][  0]LogOutputDevice: Error: r.Mobile.VirtualTextures is deprecated, use r.VirtualTextures in platform specific engine .ini files instead
[2026.10.31-17.32.24:032][  0]LogOutputDevice: Error: Stack: 
[2026.10.31-17.32.24:032][  0]LogOutputDevice: Error: [Callstack] 0x00007ff808e8cb18 UnrealEditor-Core.dll!UnknownFunction []
[2026.10.31-17.32.24:032][  0]LogOutputDevice: Error: [Callstack] 0x00007ff8081d81df UnrealEditor-Core.dll!UnknownFunction []
[2026.10.31-17.32.24:032][  0]LogOutputDevice: Error: 
[2026.10.31-17.32.24:048][  0]LogStats:                SubmitErrorReport -  0.000 s
[2026.11.01-09.21.18:050][  0]LogAutomationTest: Error: Condition failed
[2026.11.01-09.21.18:050][  0]LogAutomationTest: Error: Condition failed
[2026.11.01-09.21.18:168][  0]LogEngine: Initializing Engine...
";

    static void UeEnsure()
    {
        var e = ParseUe(UeEnsureLog);
        Eq(3, e.Count, "ensure + 2 Condition failed");
        if (e.Count < 3) return;
        Eq("ENSURE", e[0].Abbr, "ensure");
        Eq("false · ConsoleManager.cpp:4916", e[0].Message.Split('\n')[0], "заголовок ensure");
        Check(e[0].Message.Contains("r.Mobile.VirtualTextures is deprecated"), "текст ensure");
        Eq(3, e[0].Stack.Count, "Stack: + 2 кадра (пустые убраны)");
        Check(e[0].Stack.All(x => !x.StartsWith("LogOutputDevice")), "без префикса категории в стеке");
        Check(e[0].Rule.SaveClip, "ensure → клип");
        Eq("LogAutomationTest: Condition failed", e[1].Message, "строка следующей категории не приклеилась");
        Check(!e[1].Rule.SaveClip, "Error → только консоль");
        var book = new ErrorBook(); bool n; DateTime pr;
        book.Add(e[1], out n, out pr); book.Add(e[2], out n, out pr);
        Eq(1, book.Items.Count, "одинаковые Error в одну строку");

        // со стандартным исключением ensure редактора при старте отбрасывается
        var e2 = ParseUe(UeEnsureLog, true);
        Eq(2, e2.Count, "ensure r.Mobile.* проигнорирован");
        Check(e2.All(x => x.Abbr == "ERR"), "остались только Error");
        var asset = ParseUe("[2026.11.01-09.21.18:050][  0]LogOutputDevice: Error: Ensure condition failed: AssetBaseClassLoaded  [File:D:\\x\\AssetManagerTypes.cpp] [Line: 82] \n[2026.11.01-09.21.18:050][  0]LogOutputDevice: Error: Stack: \n[2026.11.01-09.21.18:051][  0]LogInit: ok\n", true);
        Eq(0, asset.Count, "ensure AssetBaseClassLoaded проигнорирован");
        var real = ParseUe("[2026.11.02-18.01.05:200][501]LogOutputDevice: Error: Ensure condition failed: Wheel != nullptr  [File:D:\\x\\CarPhysics.cpp] [Line: 214] \n[2026.11.02-18.01.05:300][502]LogTemp: Display: ok\n", true);
        Eq(1, real.Count, "свой ensure не игнорируется");
    }

    static void UeCrash()
    {
        var text = @"[2026.11.01-09.05.21:343][322]LogOutputDevice: Warning: 

Script Stack (0 frames) :

[2026.11.01-09.05.21:345][322]LogWindows: Error: appError called: Assertion failed: ObjectSpaceBufferOffset == INDEX_NONE [File:D:\build\++UE5\Sync\Engine\Source\Runtime\Renderer\Private\Skinning\SkinningSceneExtension.h] [Line: 118] 


[2026.11.01-09.05.21:345][322]LogWindows: Windows GetLastError: (0)
[2026.11.01-09.05.27:977][322]LogThreadingWindows: Error: Runnable thread Foreground Worker #0 crashed.
[2026.11.01-09.05.27:978][322]LogWindows: Error: === Critical error: ===
[2026.11.01-09.05.27:978][322]LogWindows: Error: 
[2026.11.01-09.05.27:978][322]LogWindows: Error: Assertion failed: ObjectSpaceBufferOffset == INDEX_NONE [File:D:\build\++UE5\Sync\Engine\Source\Runtime\Renderer\Private\Skinning\SkinningSceneExtension.h] [Line: 118] 
[2026.11.01-09.05.27:978][322]LogWindows: Error: [Callstack] 0x00007ff9b3f0bec8 UnrealEditor-Core.dll!UnknownFunction []
[2026.11.01-09.05.27:978][322]LogWindows: Error: [Callstack] 0x00007ff999a2013f UnrealEditor-Renderer.dll!UnknownFunction []
[2026.11.01-09.05.27:979][322]LogWindows: Error: Crash in runnable thread Foreground Worker #0
";
        var e = ParseUe(text);
        Check(e.Count >= 1 && e.All(x => x.Abbr == "CRASH"), "только CRASH (Assertion внутри краша не отдельная ошибка): " + string.Join(",", e.Select(x => x.Abbr)));
        Check(e.Count > 0 && e[0].Rule.Immediate, "краш — клип сразу");
        Check(e.Count > 0 && e[0].Message.Contains("SkinningSceneExtension.h:118"), "файл:строка в заголовке: " + (e.Count > 0 ? e[0].Message : ""));
        var book = new ErrorBook(); bool n; DateTime pr;
        foreach (var x in e) book.Add(x, out n, out pr);
        Eq(1, book.Items.Count, "одна строка в консоли");
        Check(book.Items[0].Stack.Any(s => s.Contains("UnrealEditor-Renderer.dll")), "колстек в деталях");
    }

    static void UeBlueprint()
    {
        var text = @"[2026.11.02-18.00.01:100][500]LogScript: Warning: Accessed None trying to read property CarMovement
	BP_PlayerCar_C /Game/Maps/Track01.Track01:PersistentLevel.BP_PlayerCar_C_0
	Function /Game/Cars/BP_PlayerCar.BP_PlayerCar_C:ExecuteUbergraph_BP_PlayerCar:0123
[2026.11.02-18.00.01:101][500]PIE: Error: Blueprint Runtime Error: ""Accessed None trying to read property CarMovement"". Node:  Set Throttle Graph:  EventGraph Function:  Execute Ubergraph BP Player Car Blueprint:  BP_PlayerCar
[2026.11.02-18.00.01:102][500]LogTemp: Error: Save slot not found
[2026.11.02-18.00.01:103][500]LogTemp: Display: ok
";
        var e = ParseUe(text);
        Eq(3, e.Count, "три записи");
        if (e.Count < 3) return;
        Eq("NONE", e[0].Abbr, "Accessed None");
        Eq("Accessed None trying to read property CarMovement", e[0].Message, "msg");
        Eq(2, e[0].Stack.Count, "скрипт-стек");
        Check(e[0].Rule.SaveClip, "Accessed None → клип");
        Eq("BP", e[1].Abbr, "BP runtime");
        Check(e[1].Rule.SaveClip, "BP → клип");
        Eq("ERR", e[2].Abbr, "Error");
        Eq("LogTemp: Save slot not found", e[2].Message, "Error msg без «: Error:»");
    }

    static void SettingsUpgrade()
    {
        var dir = TempDir();
        var path = Path.Combine(dir, "s.json");
        File.WriteAllText(path, "{\"LogPath\":\"C:\\\\x\\\\Player.log\",\"Rules\":[{\"Enabled\":true,\"Name\":\"Мой\",\"Pattern\":\"^A\",\"Mode\":\"line\",\"Tag\":\"A\"}]}");
        string err;
        var s = AppSettings.Load(path, out err);
        Eq("unity", s.Engine, "движок по умолчанию");
        Eq(1, s.Rules.Count, "правила Unity сохранились");
        Eq(7, s.RulesUnreal.Count, "пресет UE добавился");
        Check(s.RulesUnreal.Count(r => r.IsIgnore) == 1, "исключение одно");
        // старые настройки 1.1 с пресетом UE без исключения — исключение добавляется один раз
        var old = new AppSettings();
        old.RulesUnreal.RemoveAll(r => r.IsIgnore);
        old.Save(path);
        var up = AppSettings.Load(path, out err);
        Check(up.RulesUnreal.Count(r => r.IsIgnore) == 1 && up.UnrealIgnoreAdded, "миграция: исключение добавлено");
        up.RulesUnreal.RemoveAll(r => r.IsIgnore);
        up.Save(path);
        var again = AppSettings.Load(path, out err);
        Check(!again.RulesUnreal.Any(r => r.IsIgnore), "удалённое исключение не возвращается");
        Eq(@"C:\x\Player.log", s.ActiveLogPath, "путь Unity активен");
        s.Engine = "unreal";
        Eq("", s.ActiveLogPath, "у UE свой путь");
        Check(s.ActiveRules == s.RulesUnreal, "у UE свои правила");
        Directory.Delete(dir, true);
    }

    static void FinderUe()
    {
        var local = TempDir();
        var proj = Path.Combine(local, "projects", "FBlands_project");
        Directory.CreateDirectory(Path.Combine(proj, "Saved", "Logs"));
        File.WriteAllText(Path.Combine(proj, "FBlands_project.uproject"), "{}");
        File.WriteAllText(Path.Combine(proj, "Saved", "Logs", "FBlands_project.log"), "x");
        File.WriteAllText(Path.Combine(proj, "Saved", "Logs", "FBlands_project_2.log"), "x");
        File.WriteAllText(Path.Combine(proj, "Saved", "Logs", "FBlands_project-backup-2026.11.01-08.10.23.log"), "x");
        File.WriteAllText(Path.Combine(proj, "Saved", "Logs", "cef3.log"), "x");
        var cfg = Path.Combine(local, "UnrealEngine", "5.8", "Saved", "Config", "WindowsEditor");
        Directory.CreateDirectory(cfg);
        File.WriteAllText(Path.Combine(cfg, "EditorSettings.ini"),
            "[/Script/UnrealEd.EditorSettings]\nRecentlyOpenedProjectFiles=(ProjectName=\"" + Path.Combine(proj, "FBlands_project.uproject").Replace('\\', '/') + "\",LastOpenTime=2026.11.01-09.21.17)\n");
        var game = Path.Combine(local, "DriftGame", "Saved", "Logs");
        Directory.CreateDirectory(game);
        File.WriteAllText(Path.Combine(game, "DriftGame.log"), "x");
        var found = LogFinder.FindUnrealLogs(local);
        var names = found.Select(f => Path.GetFileName(f.Item2)).OrderBy(x => x, StringComparer.Ordinal).ToList();
        Eq("DriftGame.log,FBlands_project.log,FBlands_project_2.log", string.Join(",", names), "найдены логи");
        Check(found.Any(f => f.Item1.Contains("редактор")) && found.Any(f => f.Item1.Contains("собранная игра")), "подписи");
        Eq("unreal", LogFinder.GuessEngine(@"D:\UEprojects\FBlands_project\Saved\Logs\FBlands_project.log"), "guess UE");
        Eq("unity", LogFinder.GuessEngine(@"C:\Users\nv\AppData\LocalLow\Studio\Game\Player.log"), "guess Unity");
        Eq(null, LogFinder.GuessEngine(@"C:\logs\game.txt"), "guess неизвестно");
        Directory.Delete(local, true);
    }
}
