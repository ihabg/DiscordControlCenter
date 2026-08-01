using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace DiscordControlCenter.App;

internal static class NativeWindowDiagnostics
{
    private const int GwlStyle = -16;
    private const int GwlExStyle = -20;
    private const uint WsMinimize = 0x20000000;
    private const uint WsExToolWindow = 0x00000080;
    private const uint WsExNoActivate = 0x08000000;
    private const uint WsExLayered = 0x00080000;
    private const int DwmwaCloaked = 14;

    internal static object Capture(Window? window)
    {
        var handle = window is null ? IntPtr.Zero : new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return new { HasHandle = false, window?.Title, window?.ShowInTaskbar, window?.WindowState };
        }

        GetWindowRect(handle, out var bounds);
        GetClientRect(handle, out var client);
        var style = unchecked((uint)GetWindowLongPtr(handle, GwlStyle).ToInt64());
        var exStyle = unchecked((uint)GetWindowLongPtr(handle, GwlExStyle).ToInt64());
        var classNameBuffer = new System.Text.StringBuilder(256);
        _ = GetClassName(handle, classNameBuffer, classNameBuffer.Capacity);
        var cloaked = 0;
        _ = DwmGetWindowAttribute(handle, DwmwaCloaked, out cloaked, Marshal.SizeOf<int>());

        return new
        {
            HasHandle = true,
            Handle = $"0x{handle.ToInt64():X}",
            window!.Title,
            window.ShowInTaskbar,
            window.WindowState,
            ClassName = classNameBuffer.ToString(),
            IsVisible = IsWindowVisible(handle),
            IsEnabled = IsWindowEnabled(handle),
            Bounds = new { bounds.Left, bounds.Top, bounds.Right, bounds.Bottom, Width = bounds.Right - bounds.Left, Height = bounds.Bottom - bounds.Top },
            ClientBounds = new { client.Left, client.Top, client.Right, client.Bottom, Width = client.Right - client.Left, Height = client.Bottom - client.Top },
            Owner = $"0x{GetWindow(handle, 4).ToInt64():X}",
            Parent = $"0x{GetParent(handle).ToInt64():X}",
            Root = $"0x{GetAncestor(handle, 2).ToInt64():X}",
            Style = $"0x{style:X8}",
            ExtendedStyle = $"0x{exStyle:X8}",
            IsToolWindow = (exStyle & WsExToolWindow) != 0,
            IsNoActivate = (exStyle & WsExNoActivate) != 0,
            IsLayered = (exStyle & WsExLayered) != 0,
            IsMinimized = (style & WsMinimize) != 0,
            IsCloaked = cloaked != 0
        };
    }

    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr handle, out RectNative rect);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr handle, out RectNative rect);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr handle);
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr handle);
#pragma warning disable CA1838
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr handle, System.Text.StringBuilder name, int count);
#pragma warning restore CA1838
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr handle, int index);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr handle, uint command);
    [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr handle);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr handle, uint flags);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr handle, int attribute, out int value, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct RectNative { public int Left; public int Top; public int Right; public int Bottom; }
}
