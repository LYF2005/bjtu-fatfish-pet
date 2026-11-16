using System;
using System.IO;
using System.Reflection;
using System.Windows;

namespace BigBlueFish.Windows;

public partial class App : Application
{
    public static string[] Args { get; set; } = [];

    public App()
    {
        Environment.CurrentDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
#if !DEBUG
        DispatcherUnhandledException += (_, e) => { e.Handled = true; RecordException(e.Exception, false); };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => RecordException((Exception)e.ExceptionObject, true);
#endif
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        Args = e.Args;
        base.OnStartup(e);
    }

    private static readonly object LogLock = new();
    private static string? lastSignature;
    private static DateTime lastWrite = DateTime.MinValue;
    private static int repeatCount;

    private void RecordException(Exception exception, bool fatal)
    {
        try
        {
            var signature = (exception.GetType().FullName ?? "?") + "|" + (exception.Message ?? "?");
            lock (LogLock)
            {
                var now = DateTime.UtcNow;
                if (!fatal && signature == lastSignature)
                {
                    repeatCount++;
                    // 限流：同一异常在 5 秒内只汇总写一次，防止循环异常刷爆磁盘与内存。
                    if ((now - lastWrite).TotalSeconds < 5)
                    {
                        if (fatal) Shutdown(1);
                        return;
                    }
                }
                else
                {
                    lastSignature = signature;
                    repeatCount = 0;
                }
                lastWrite = now;
                // 轮转：超过 20MB 就归档，避免日志无限膨胀。
                var log = "companion-errors.log";
                var info = new FileInfo(log);
                if (info.Exists && info.Length > 20L * 1024 * 1024)
                {
                    try { File.Move(log, log + ".old", true); } catch { }
                }
                var suffix = repeatCount > 0 ? $" [重复x{repeatCount}]" : "";
                File.AppendAllText(log, DateTime.Now.ToString("s") + suffix + " " + exception + Environment.NewLine);
            }
        }
        catch { }
        if (fatal) Shutdown(1);
    }
}
