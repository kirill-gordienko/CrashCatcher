using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CrashCatcher.Core;

public static class LogDump
{
    /// <summary>Разбирает лог целиком и печатает, что нашлось (для проверки правил на реальных логах).</summary>
    public static void Run(string path, string engine)
    {
        var rules = RuleConfig.Defaults(engine);
        var p = new LogParser(rules, EngineProfile.ById(engine));
        var book = new ErrorBook { MaxItems = 10000 };
        int n = 0;
        p.Entry += e => { n++; bool isNew; DateTime prev; book.Add(e, out isNew, out prev); };
        foreach (var l in File.ReadAllText(path).Replace("\r\n", "\n").Split('\n')) p.Feed(l);
        p.Flush();
        Console.WriteLine("== " + Path.GetFileName(path) + ": срабатываний " + n + ", строк в консоли " + book.Items.Count);
        foreach (var r in book.Items)
        {
            var msg = r.Message.Split('\n')[0];
            if (msg.Length > 110) msg = msg.Substring(0, 110) + "…";
            Console.WriteLine(string.Format("  {0,-6} x{1,-3} clip={2} stack={3,-3} {4}", r.Abbr, r.Count, r.Rule.SaveClip ? "Y" : "-", r.Stack.Count, msg));
        }
    }
}
