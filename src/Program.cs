using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using CrashCatcher.Win;

[assembly: AssemblyTitle("Crash Catcher")]
[assembly: AssemblyProduct("Crash Catcher")]
[assembly: AssemblyDescription("Ловит ошибки в логе Unity и Unreal Engine 5 и сохраняет клип NVIDIA (Alt+F10)")]
[assembly: AssemblyVersion("1.1.0.0")]
[assembly: AssemblyFileVersion("1.1.0.0")]

namespace CrashCatcher
{
    static class Program
    {
        public static bool DemoMode;

        [STAThread]
        static void Main(string[] args)
        {
            if (args.Length >= 2 && args[0] == "--render-test")
            {
                // отладка: отрисовать виджет на демо-данных в PNG и выйти
                Application.EnableVisualStyles();
                using (var app = new AppController(true))
                {
                    app.LoadDemo();
                    app.RenderPreviews(args[1]);
                }
                return;
            }
            if (args.Length >= 1 && args[0] == "--demo") DemoMode = true;

            bool created;
            using (var mutex = new Mutex(true, "Local\\CrashCatcher_SingleInstance", out created))
            {
                if (!created)
                {
                    MessageBox.Show("Crash Catcher уже запущен — его значок в трее.", "Crash Catcher", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                try { Native.SetProcessDPIAware(); } catch { }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += (s, e) => Report(e.Exception, false);
                AppDomain.CurrentDomain.UnhandledException += (s, e) => Report(e.ExceptionObject as Exception, true);

                using (var app = new AppController(false))
                {
                    app.Start();
                    if (DemoMode) app.LoadDemo();
                    Application.Run(new ApplicationContext());
                }
            }
        }

        static void Report(Exception ex, bool fatal)
        {
            try
            {
                var dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                File.AppendAllText(Path.Combine(dir, "CrashCatcher.error.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + (fatal ? " FATAL " : " ") + ex + Environment.NewLine + Environment.NewLine);
            }
            catch { }
            if (fatal)
                MessageBox.Show("Crash Catcher упал:\n" + (ex != null ? ex.Message : "?") + "\n\nПодробности в CrashCatcher.error.log рядом с программой.",
                    "Crash Catcher", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
