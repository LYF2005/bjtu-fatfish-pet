using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;

namespace BigBlueFish.Windows;

// A no-activate owner cannot reliably capture clicks outside its process.
// Observe button-down only while this menu is open and always pass input through.
internal sealed class CompanionMenuDismissal : IDisposable
{
    private readonly ContextMenu menu;
    private readonly MouseCallback callback;
    private IntPtr hook;
    private long session;
    private bool disposed;
    internal bool IsListening => hook != IntPtr.Zero;

    internal CompanionMenuDismissal(ContextMenu menu)
    {
        this.menu = menu;
        callback = MouseMessage;
        menu.Opened += Opened;
        menu.Closed += Closed;
    }

    private void Opened(object sender, RoutedEventArgs args)
    {
        if (disposed || IsListening) return;
        session++;
        hook = SetWindowsHookEx(14, callback, GetModuleHandle(null), 0); // WH_MOUSE_LL
        if (hook == IntPtr.Zero)
            return;
    }

    private void Closed(object sender, RoutedEventArgs args) => Stop();
    private void Stop()
    {
        session++;
        if (hook == IntPtr.Zero) return;
        UnhookWindowsHookEx(hook);
        hook = IntPtr.Zero;
    }

    private IntPtr MouseMessage(int code, IntPtr message, IntPtr data)
    {
        long kind = message.ToInt64();
        if (code >= 0 && kind is 0x201 or 0x207 or 0x20B && !disposed)
        {
            var position = Marshal.PtrToStructure<MouseData>(data).Position;
            long capturedSession = session;
            // Release menu capture before Windows routes this button-down. Only
            // bounds checks/closing run here; never perform layout, IO or model work.
            if (menu.Dispatcher.CheckAccess())
                DismissOutside(new Point(position.X, position.Y));
            else if (!menu.Dispatcher.HasShutdownStarted)
                menu.Dispatcher.BeginInvoke(DispatcherPriority.Input, (Action)(() =>
                {
                    if (!disposed && capturedSession == session)
                        DismissOutside(new Point(position.X, position.Y));
                }));
        }
        return CallNextHookEx(hook, code, message, data);
    }

    internal void DismissOutside(Point physicalPoint)
    {
        if (!menu.IsOpen || PresentationSource.FromVisual(menu) == null) return;
        var first = menu.PointToScreen(new Point(0, 0));
        var last = menu.PointToScreen(new Point(menu.ActualWidth, menu.ActualHeight));
        var main = new Rect(first, last);
        if (!main.Contains(physicalPoint) && !AnyOpenSubmenuContains(physicalPoint)) menu.IsOpen = false;
    }

    // 子菜单 Popup 在系统里是独立窗口，位置落在主菜单矩形之外。点击子菜单项时
    // 必须在点位于任一打开的子菜单内时不关闭主菜单，否则子项永远点不到。
    private bool AnyOpenSubmenuContains(Point point) => AnyOpenSubmenuContains(menu.Items, point);

    private static bool AnyOpenSubmenuContains(ItemCollection items, Point point)
    {
        foreach (var item in items)
        {
            if (item is not MenuItem menuItem) continue;
            if (menuItem.IsSubmenuOpen && SubmenuBounds(menuItem) is Rect bounds && bounds.Contains(point))
                return true;
            if (AnyOpenSubmenuContains(menuItem.Items, point))
                return true;
        }
        return false;
    }

    private static Rect? SubmenuBounds(MenuItem menuItem)
    {
        // 子菜单 Popup 由 CompanionMenuStyles.xaml 的模板以 PART_Popup 命名；FindName 走模板命名空间，无需依赖可视化树。
        if (menuItem.Template?.FindName("PART_Popup", menuItem) is not Popup popup || !popup.IsOpen)
            return null;
        if (popup.Child is not FrameworkElement child || child.ActualWidth <= 0 || child.ActualHeight <= 0)
            return null;
        var origin = child.PointToScreen(new Point(0, 0));
        return new Rect(origin, new Size(child.ActualWidth, child.ActualHeight));
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        menu.Opened -= Opened;
        menu.Closed -= Closed;
        Stop();
        GC.KeepAlive(callback);
    }

    private delegate IntPtr MouseCallback(int code, IntPtr message, IntPtr data);
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseData
    {
        public NativePoint Position;
        public uint Mouse, Flags, Time;
        public UIntPtr Extra;
    }
    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int kind, MouseCallback callback, IntPtr module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
}
