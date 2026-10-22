using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace WinDndToggle;

internal static class Program
{
    private const string MutexName = @"Local\WinDndToggle.SingleInstance";

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);

    [STAThread]
    private static void Main(string[] args)
    {
        // 命令行模式：不开界面，直接干活然后退出（方便挂到别的工具里）
        string? cli = args.FirstOrDefault(a => a.StartsWith("--", StringComparison.Ordinal))?.ToLowerInvariant();
        if (cli is "--probe" or "--toggle" or "--on" or "--off")
        {
            RunCli(cli);
            return;
        }

        using var mutex = new Mutex(true, MutexName, out bool isFirst);
        if (!isFirst)
        {
            MessageBox.Show(
                Res.Tr("勿扰开关已经在运行了，看任务栏托盘图标。", "WinDndToggle is already running — check the tray icons."),
                "WinDndToggle",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        // net48 没有这个 API，靠 app.manifest 里的 dpiAwareness 声明
#if NET8_0_OR_GREATER
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
#endif
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new TrayContext(args));
    }

    private static void RunCli(string mode)
    {
        AttachConsole(-1); // 从命令行启动时接回父窗口；输出被重定向时这句失败也无所谓

        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch
        {
            // 没有控制台时会抛，忽略
        }

        int exit = 0;

        if (mode == "--probe")
        {
            Console.WriteLine("os_build     = " + Environment.OSVersion.Version.Build);
            bool ok = QuietHours.TryReadRaw(out string profile, out string error);
            Console.WriteLine("readable     = " + ok);
            Console.WriteLine("profile      = " + (ok ? profile : "(读不到)"));
            Console.WriteLine("state        = " + (ok ? QuietHours.Describe(profile) : "unknown"));
            if (!ok)
            {
                Console.WriteLine("error        = " + error);
            }

            exit = ok ? 0 : 1;
        }
        else
        {
            bool ok;
            DndState state;
            string error;

            if (mode == "--toggle")
            {
                ok = QuietHours.TryToggle(out state, out error);
            }
            else
            {
                DndState target = mode == "--on" ? DndState.PriorityOnly : DndState.Off;
                ok = QuietHours.TrySet(target, out error);
                state = ok ? target : DndState.Unknown;
            }

            if (ok)
            {
                Console.WriteLine("ok  -> " + state);
            }
            else
            {
                Console.WriteLine("failed: " + error);
                exit = 1;
            }
        }

        Environment.Exit(exit);
    }
}
