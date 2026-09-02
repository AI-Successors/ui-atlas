using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace UiAtlas.Core.Cli;

internal static class RecordingPanelCoordinator
{
    private const uint WmClose = 0x0010;
    private const uint SmtoAbortIfHung = 0x0002;
    private const string RecorderWindowPrefix = "UiAtlas recording - ";

    public static bool CloseOtherRecorderPanels(TimeSpan timeout)
    {
        var currentProcessId = Environment.ProcessId;
        var windows = new List<nint>();
        _ = EnumWindows((hwnd, lParam) =>
        {
            _ = GetWindowThreadProcessId(hwnd, out var processId);
            if (processId == 0 || processId == currentProcessId || !IsRecorderWindowTitle(ReadWindowTitle(hwnd)))
                return true;

            try
            {
                using var process = Process.GetProcessById((int)processId);
                if (!string.Equals(process.ProcessName, "ui-atlas", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                return true;
            }

            windows.Add(hwnd);
            return true;
        }, 0);

        foreach (var hwnd in windows)
            _ = SendMessageTimeout(hwnd, WmClose, 0, 0, SmtoAbortIfHung, 750, out _);

        if (windows.Count == 0)
            return true;
        if (timeout <= TimeSpan.Zero)
            return windows.All(hwnd => !IsWindow(hwnd));

        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout && windows.Any(IsWindow))
            Thread.Sleep(25);
        return windows.All(hwnd => !IsWindow(hwnd));
    }

    internal static bool IsRecorderWindowTitle(string? title) =>
        !string.IsNullOrWhiteSpace(title) && title.StartsWith(RecorderWindowPrefix, StringComparison.Ordinal);

    private static string ReadWindowTitle(nint hwnd)
    {
        var length = GetWindowTextLength(hwnd);
        if (length <= 0)
            return string.Empty;
        var buffer = new StringBuilder(length + 1);
        _ = GetWindowText(hwnd, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    private delegate bool EnumWindowsProc(nint hwnd, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint hwnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SendMessageTimeout(
        nint hwnd,
        uint message,
        nint wParam,
        nint lParam,
        uint flags,
        uint timeout,
        out nint result);
}
