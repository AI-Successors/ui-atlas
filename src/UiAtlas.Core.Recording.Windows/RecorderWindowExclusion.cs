using UiAtlas.Core.Contracts;

namespace UiAtlas.Core.Recording.Windows;

internal static class RecorderWindowExclusion
{
    public static IReadOnlyList<RectI> Find(WindowTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.ProcessId == Environment.ProcessId)
            return [];

        var recorderBounds = new List<RectI>();
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            NativeMethods.GetWindowThreadProcessId(hwnd, out var processId);
            if (processId != Environment.ProcessId || !NativeMethods.IsWindowVisible(hwnd) ||
                (NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GwlExStyle).ToInt64() & NativeMethods.WsExTopmost) == 0 ||
                WindowCatalog.GetClass(hwnd).Contains("UiAtlas recording highlight overlay", StringComparison.OrdinalIgnoreCase) ||
                !NativeMethods.GetWindowRect(hwnd, out var rect) ||
                rect.Right <= rect.Left || rect.Bottom <= rect.Top)
                return true;

            var bounds = new RectI(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
            if (OverlappingArea(bounds, target.Bounds) > 0)
                recorderBounds.Add(bounds);
            return true;
        }, 0);

        return recorderBounds;
    }

    public static IReadOnlyList<AutomationObservation> FilterControls(
        IReadOnlyList<AutomationObservation> controls,
        IReadOnlyList<RectI> recorderBounds)
    {
        ArgumentNullException.ThrowIfNull(controls);
        ArgumentNullException.ThrowIfNull(recorderBounds);
        if (controls.Count == 0 || recorderBounds.Count == 0)
            return controls;

        return controls.Where(control =>
        {
            var centerX = control.Bounds.X + control.Bounds.Width / 2;
            var centerY = control.Bounds.Y + control.Bounds.Height / 2;
            return !recorderBounds.Any(bounds => Contains(bounds, centerX, centerY));
        }).ToArray();
    }

    private static bool Contains(RectI bounds, int x, int y) =>
        x >= bounds.X && x < bounds.X + bounds.Width && y >= bounds.Y && y < bounds.Y + bounds.Height;

    private static long OverlappingArea(RectI left, RectI right)
    {
        var width = Math.Max(0, Math.Min(left.X + left.Width, right.X + right.Width) - Math.Max(left.X, right.X));
        var height = Math.Max(0, Math.Min(left.Y + left.Height, right.Y + right.Height) - Math.Max(left.Y, right.Y));
        return (long)width * height;
    }
}
