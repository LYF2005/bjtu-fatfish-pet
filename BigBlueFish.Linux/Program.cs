using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;

namespace BigBlueFish.Windows;

internal static class Program
{
    // 数据目录按平台约定，避免把 macOS/Linux 的配置塞到同一处。
    private static bool IsMacOS => RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

    [STAThread] public static void Main(string[] args)
    {
        App.Args = args;
        if (args.Length == 2 && args[0] == "--wait-for-exit" && int.TryParse(args[1], out int pid)) {
            try { using var old = System.Diagnostics.Process.GetProcessById(pid); if (!old.WaitForExit(30000)) return; } catch (ArgumentException) { }
        }
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var baseDir = IsMacOS
            ? Path.Combine(home, "Library", "Application Support")            // macOS 约定
            : (Environment.GetEnvironmentVariable("XDG_DATA_HOME") ?? Path.Combine(home, ".local/share")); // Linux XDG
        var directory = Environment.GetEnvironmentVariable("BIGBLUEFISH_DATA") ?? Path.Combine(baseDir, "bigbluefish");
        Directory.CreateDirectory(directory); Environment.CurrentDirectory = directory;
        Build().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder Build()
    {
        var builder = AppBuilder.Configure<App>().UsePlatformDetect();
        // Linux(X11)：强制软件渲染，兼容无 GPU/无显卡驱动的桌面环境。
        // macOS：走原生 Quartz/Metal 后端，绝不能注入 X11 选项，否则启动即崩。
        if (!IsMacOS)
            builder = builder.With(new X11PlatformOptions { RenderingMode = [X11RenderingMode.Software] });
        return builder;
    }
}
public sealed class App : Application
{
    public static string[] Args = [];
    public override void Initialize() {
        var theme = new FluentTheme(); theme.Palettes[Avalonia.Styling.ThemeVariant.Light] = new ColorPaletteResources { Accent = Avalonia.Media.Color.FromRgb(80,96,152) };
        Styles.Add(theme); RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;
    }
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        { desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnExplicitShutdown; desktop.MainWindow = new LinuxPetWindow(); }
        base.OnFrameworkInitializationCompleted();
    }
}
