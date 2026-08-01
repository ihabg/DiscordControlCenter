using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Automation;

namespace DiscordControlCenter.UiAutomationProbe;

internal static class Program
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(12);

    private static int Main(string[] args)
    {
        var expectedPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "DiscordControlCenter.App", "bin", "Release", "net10.0-windows", "DiscordControlCenter.App.exe"));
        var targetPath = args.Length == 0 ? expectedPath : Path.GetFullPath(args[0]);
        if (!string.Equals(targetPath, expectedPath, StringComparison.OrdinalIgnoreCase) || !File.Exists(targetPath))
        {
            Console.Error.WriteLine("The probe only launches the exact local DiscordControlCenter.App Release executable.");
            return 2;
        }

        using var process = Process.Start(new ProcessStartInfo(targetPath, "--ui-automation-probe") { UseShellExecute = false })
            ?? throw new InvalidOperationException("The Discord Control Center process could not be started.");
        try
        {
            var deadline = DateTime.UtcNow + Timeout;
            IntPtr handle;
            do
            {
                process.Refresh();
                handle = FindTopLevelWindow(process.Id);
                if (handle != IntPtr.Zero)
                {
                    break;
                }

                Thread.Sleep(100);
            }
            while (!process.HasExited && DateTime.UtcNow < deadline);

            var report = new ProbeReport(
                process.Id,
                process.ProcessName,
                process.MainModule?.FileName,
                process.HasExited,
                DateTime.UtcNow - process.StartTime.ToUniversalTime(),
                NativeWindowReport.Capture(process.Id, handle),
                CaptureAutomation(handle));
            Console.WriteLine(JsonSerializer.Serialize(report, JsonOptions));
            return report.NativeWindow.Handle != "0x0" && report.Automation.RootFound ? 0 : 1;
        }
        finally
        {
            if (!process.HasExited)
            {
                process.CloseMainWindow();
                if (!process.WaitForExit(3000))
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(3000);
                }
            }
        }
    }

    private static AutomationReport CaptureAutomation(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
        {
            return new AutomationReport(false, null, [], [], [], []);
        }

        try
        {
            var root = AutomationElement.FromHandle(handle);
            var rootInfo = ElementInfo.From(root);
            var expectedIds = new[]
            {
                "DiscordControlCenter.MainWindow", "DiscordControlCenter.Navigation",
                "DiscordControlCenter.Navigation.Messages", "DiscordControlCenter.Messages",
                "DiscordControlCenter.ManualApprovals.Queue.Refresh", "DiscordControlCenter.ManualApprovals.Queue.Search",
                "DiscordControlCenter.ManualApprovals.Queue.List", "DiscordControlCenter.ManualApprovals.History.List"
            };
            var found = expectedIds
                .Where(id => root.FindFirst(TreeScope.Descendants | TreeScope.Element, new PropertyCondition(AutomationElement.AutomationIdProperty, id)) is not null)
                .ToArray();
            var messagesNavigation = root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "DiscordControlCenter.Navigation.Messages"));
            if (messagesNavigation?.TryGetCurrentPattern(InvokePattern.Pattern, out var pattern) == true)
            {
                ((InvokePattern)pattern).Invoke();
                Thread.Sleep(150);
                found = expectedIds
                    .Where(id => root.FindFirst(TreeScope.Descendants | TreeScope.Element, new PropertyCondition(AutomationElement.AutomationIdProperty, id)) is not null)
                    .ToArray();
            }

            return new AutomationReport(true, rootInfo, found, InspectView(TreeWalker.RawViewWalker, root), InspectView(TreeWalker.ControlViewWalker, root), InspectView(TreeWalker.ContentViewWalker, root));
        }
        catch (Exception exception) when (exception is ElementNotAvailableException or InvalidOperationException or COMException)
        {
            return new AutomationReport(false, null, [], [], [], [$"UIA failure: {exception.GetType().Name}"]);
        }
    }

    private static List<string> InspectView(TreeWalker walker, AutomationElement root)
    {
        var result = new List<string>();
        var node = walker.GetFirstChild(root);
        while (node is not null && result.Count < 40)
        {
            result.Add(ElementInfo.From(node).ToString());
            node = walker.GetNextSibling(node);
        }

        return result;
    }

    private static IntPtr FindTopLevelWindow(int processId)
    {
        var matchingWindows = IntPtr.Zero;
        EnumWindows(
            (window, _) =>
            {
                var threadId = GetWindowThreadProcessId(window, out var ownerProcessId);
                if (threadId != 0 && ownerProcessId == processId && IsWindowVisible(window) && GetWindow(window, 4) == IntPtr.Zero)
                {
                    matchingWindows = window;
                    return false;
                }

                return true;
            },
            IntPtr.Zero);
        return matchingWindows;
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private sealed record ProbeReport(int ProcessId, string ProcessName, string? ExecutablePath, bool Exited, TimeSpan Lifetime, NativeWindowReport NativeWindow, AutomationReport Automation);
    private sealed record AutomationReport(bool RootFound, ElementInfo? Root, IReadOnlyList<string> FoundAutomationIds, IReadOnlyList<string> RawView, IReadOnlyList<string> ControlView, IReadOnlyList<string> ContentView);
    private sealed record ElementInfo(string Name, string AutomationId, string ControlType, int ProcessId)
    {
        internal static ElementInfo From(AutomationElement element) => new(
            element.Current.Name,
            element.Current.AutomationId,
            element.Current.ControlType.ProgrammaticName,
            element.Current.ProcessId);
        public override string ToString() => $"{ControlType}|{AutomationId}|{Name}|pid={ProcessId}";
    }

    private sealed record NativeWindowReport(string Handle, string Title, string ClassName, bool Visible, bool Enabled, object Bounds, object ClientBounds, string Owner, string Parent, string Root, string Style, string ExtendedStyle, bool ToolWindow, bool NoActivate, bool Layered, bool Cloaked, bool Minimized, IReadOnlyList<string> EnumeratedWindows)
    {
        internal static NativeWindowReport Capture(int processId, IntPtr handle)
        {
            var windows = new List<string>();
            EnumWindows((window, _) =>
            {
                var threadId = GetWindowThreadProcessId(window, out var ownerProcessId);
                if (threadId != 0 && ownerProcessId == processId)
                {
                    windows.Add($"0x{window.ToInt64():X}");
                }
                return true;
            }, IntPtr.Zero);
            if (handle == IntPtr.Zero)
            {
                return new("0x0", string.Empty, string.Empty, false, false, new { }, new { }, "0x0", "0x0", "0x0", "0x0", "0x0", false, false, false, false, false, windows);
            }

            GetWindowRect(handle, out var bounds);
            GetClientRect(handle, out var client);
            var className = new System.Text.StringBuilder(256);
            _ = GetClassName(handle, className, className.Capacity);
            var title = new char[512];
            var titleLength = GetWindowText(handle, title, title.Length);
            var style = unchecked((uint)GetWindowLongPtr(handle, -16).ToInt64());
            var exStyle = unchecked((uint)GetWindowLongPtr(handle, -20).ToInt64());
            _ = DwmGetWindowAttribute(handle, 14, out var cloaked, sizeof(int));
            return new($"0x{handle.ToInt64():X}", new string(title, 0, titleLength), className.ToString(), IsWindowVisible(handle), IsWindowEnabled(handle), bounds, client, $"0x{GetWindow(handle, 4).ToInt64():X}", $"0x{GetParent(handle).ToInt64():X}", $"0x{GetAncestor(handle, 2).ToInt64():X}", $"0x{style:X8}", $"0x{exStyle:X8}", (exStyle & 0x80) != 0, (exStyle & 0x08000000) != 0, (exStyle & 0x00080000) != 0, cloaked != 0, (style & 0x20000000) != 0, windows);
        }
    }

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    private delegate bool EnumWindowsProc(IntPtr window, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr handle, out int processId);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr handle);
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr handle);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr handle, uint command);
    [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr handle);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr handle, uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr handle, out RectNative rect);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr handle, out RectNative rect);
#pragma warning disable CA1838
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr handle, System.Text.StringBuilder name, int count);
#pragma warning restore CA1838
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr handle, [Out] char[] text, int count);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr handle, int index);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr handle, int attribute, out int value, int size);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct RectNative(int Left, int Top, int Right, int Bottom);
}
