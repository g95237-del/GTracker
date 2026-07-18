using System.Runtime.InteropServices;
using System.Text;
using System.Diagnostics;

namespace GTracker.App.Capture;

public sealed record WindowInfo(
    nint Handle,
    string Title,
    uint ProcessId,
    string ProcessName,
    string ExecutablePath,
    bool IsMinimized,
    bool IsProcessMainWindow)
{
    public string DisplayName => $"{Title}  [{ProcessName}, {ProcessId}]" + (IsMinimized ? "  (minimized)" : string.Empty);
}

public static partial class WindowCatalog
{
    public static IReadOnlyList<WindowInfo> GetWindows()
    {
        var windows = new List<WindowInfo>();
        EnumWindows((handle, _) =>
        {
            if (TryGetWindow(handle) is not { } window)
            {
                return true;
            }
            windows.Add(window);
            return true;
        }, nint.Zero);

        return windows.OrderBy(window => window.Title, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    public static WindowInfo? GetForegroundWindowInfo() => TryGetWindow(GetForegroundWindow());

    public static bool RestoreAndActivate(nint handle)
    {
        if (!IsWindow(handle)) return false;
        if (IsIconic(handle)) ShowWindow(handle, 9);
        BringWindowToTop(handle);
        return SetForegroundWindow(handle);
    }

    private static WindowInfo? TryGetWindow(nint handle)
    {
        if (!IsWindow(handle)) return null;
        var minimized = IsIconic(handle);
        if (!IsWindowVisible(handle) && !minimized) return null;
        var titleLength = GetWindowTextLength(handle);
        if (titleLength == 0) return null;
        var title = new StringBuilder(titleLength + 1);
        GetWindowText(handle, title, title.Capacity);
        GetWindowThreadProcessId(handle, out var processId);
        var processName = processId.ToString();
        var executablePath = string.Empty;
        var isProcessMainWindow = false;
        try
        {
            using var process = Process.GetProcessById((int)processId);
            processName = process.ProcessName;
            executablePath = process.MainModule?.FileName ?? string.Empty;
            isProcessMainWindow = process.MainWindowHandle == handle;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Some elevated or protected processes do not expose their executable path.
        }
        return new(handle, title.ToString(), processId, processName, executablePath, minimized, isProcessMainWindow);
    }

    public static bool TryGetClientScreenRect(nint handle, out NativeRect rect)
    {
        rect = default;
        if (!IsWindow(handle) || IsIconic(handle) || !GetClientRect(handle, out var client))
        {
            return false;
        }

        var topLeft = new NativePoint(client.Left, client.Top);
        var bottomRight = new NativePoint(client.Right, client.Bottom);
        if (!ClientToScreen(handle, ref topLeft) || !ClientToScreen(handle, ref bottomRight))
        {
            return false;
        }

        rect = new(topLeft.X, topLeft.Y, bottomRight.X, bottomRight.Y);
        return rect.Width > 0 && rect.Height > 0;
    }

    public static nint MonitorForWindow(nint handle) => MonitorFromWindow(handle, 2);

    public readonly record struct NativeRect(int Left, int Top, int Right, int Bottom)
    {
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint(int x, int y)
    {
        public int X = x;
        public int Y = y;
    }

    private delegate bool EnumWindowsProc(nint handle, nint parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);
    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint handle);
    [DllImport("user32.dll")]
    private static extern bool IsWindow(nint handle);
    [DllImport("user32.dll")]
    private static extern bool IsIconic(nint handle);
    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint handle, int command);
    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(nint handle);
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint handle);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint handle, StringBuilder text, int maximumCount);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(nint handle);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint handle, out uint processId);
    [DllImport("user32.dll")]
    private static extern bool GetClientRect(nint handle, out NativeRect rect);
    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(nint handle, ref NativePoint point);
    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint handle, uint flags);
}
