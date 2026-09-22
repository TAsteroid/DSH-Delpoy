using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using DSHDeploy.Core;
using DSHDeploy.Ui;

namespace DSHDeploy
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            // The exe is a GUI app, so a terminal run of --self-test / --headless needs the
            // parent console re-attached (and its code page set) to show text at all.
            bool attached = false;
            try { attached = NativeMethods.AttachConsole(NativeMethods.ATTACH_PARENT_PROCESS); } catch { }
            if (!attached)
            {
                try { NativeMethods.AllocConsole(); } catch { }
            }
            ConfigureConsole();
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

            Options options;
            try
            {
                options = Options.Parse(args);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("参数解析失败: " + ex.Message);
                return 2;
            }

            string logPath = Path.Combine(
                Path.GetTempPath(), "DSH-Deploy",
                "deploy-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".log");

            if (options.ShowVersion)
            {
                Console.WriteLine(Options.HelpText());
                return 0;
            }

            if (options.SelfTest)
            {
                Log.Init(new SilentUi(), logPath);
                try
                {
                    return SelfTest.Run();
                }
                finally
                {
                    Log.Close();
                }
            }

            if (options.Headless)
            {
                if (!options.AssumeYes)
                {
                    Console.Error.WriteLine("--headless 需要同时指定 --yes, 以免在无人值守时静默安装。");
                    return 2;
                }
                Log.Init(new ConsoleUi(true), logPath);
                try
                {
                    var deployment = new Deployment(options, new ConsoleUi(true));
                    Outcome outcome = deployment.Run();
                    Console.WriteLine();
                    Console.WriteLine("结果: " + outcome.Summary);
                    foreach (string action in outcome.Actions) Console.WriteLine("  - " + action);
                    Console.WriteLine("日志: " + outcome.LogPath);
                    return outcome.Success ? 0 : 1;
                }
                finally
                {
                    Log.Close();
                }
            }

            // Interactive wizard.
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Log.Init(new SilentUi(), logPath);
            try
            {
                using (var form = new MainForm(options))
                {
                    DialogResult result = form.ShowDialog();
                    if (form.Outcome != null && !form.Outcome.Success && result == DialogResult.Abort) return 1;
                }
                return 0;
            }
            finally
            {
                Log.Close();
            }
        }

        private static void ConfigureConsole()
        {
            try
            {
                Console.OutputEncoding = Encoding.UTF8;
                Console.InputEncoding = Encoding.UTF8;
            }
            catch { }
        }

        private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            var exception = e.ExceptionObject as Exception;
            Log.Error("未处理的异常: {0}", exception == null ? "(未知)" : exception.ToString());
            Log.Close();
            Console.Error.WriteLine("DSH-Deploy 遇到未预期的错误, 详情见日志: " + Log.Path);
        }
    }

    /// <summary>Absorbs log lines until the real UI is ready (used during startup).</summary>
    internal sealed class SilentUi : IUi
    {
        public void Log(LogEntry entry) { }
        public void SetStatus(string text) { }
        public void SetProgress(int percent) { }
        public bool Consent(string title, string message) { return false; }
        public string AskString(string title, string message, bool secret) { return null; }
        public void Finish(Outcome outcome) { }
    }

    internal static class NativeMethods
    {
        public const int ATTACH_PARENT_PROCESS = -1;

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool AttachConsole(int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool AllocConsole();
    }
}
