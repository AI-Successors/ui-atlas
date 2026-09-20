using UiAtlas.Core.Contracts;

namespace UiAtlas.Core.Recording.Windows;

/// <summary>Read-only native host hints, not UIA capability or reading qualification.</summary>
public static class NativeGridHostDiscovery
{
    public static IReadOnlyList<AutomationObservation> Collect(WindowTarget target)
    {
        var result = new List<AutomationObservation>();
        foreach (var value in WindowCatalog.ListDescendantHandles(target.Hwnd, 1024))
        {
            var hwnd = (nint)value;
            if (!NativeMethods.IsWindowVisible(hwnd)) continue;
            _ = NativeMethods.GetWindowThreadProcessId(hwnd, out var processId);
            if (processId != target.ProcessId) continue;
            var className = WindowCatalog.GetClass(hwnd);
            if (!className.Contains("grid", StringComparison.OrdinalIgnoreCase)) continue;
            if (!NativeMethods.GetWindowRect(hwnd, out var rect)) continue;
            var bounds = new RectI(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
            if (bounds.Width < 60 || bounds.Height < 40) continue;
            var name = WindowCatalog.GetText(hwnd);
            result.Add(new($"grid-host:{value:X}", "", "", string.IsNullOrWhiteSpace(name) ? "Data grid" : name,
                "ControlType.DataGrid", className, bounds, true, false, "UiAtlas.GridHostHint", value));
        }
        return result;
    }
}
