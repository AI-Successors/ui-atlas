using System.Globalization;
using System.Text.Json;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Recording;

namespace UiAtlas.Core.Recording.Windows;

/// <summary>
/// Hosts one bounded UI Automation collection request inside an isolated helper process.
/// Entry-point applications must dispatch <see cref="Command"/> before starting their UI.
/// </summary>
public static class UiaWorkerHost
{
    public const string Command = "__uia-worker";

    public static int Run(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Length is not (5 or 6) ||
            !long.TryParse(args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var rootOwnerHwnd) ||
            !int.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var maxNodes) ||
            !int.TryParse(args[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var processId) ||
            !long.TryParse(args[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var startedTicks) ||
            !long.TryParse(args[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var scopeHwnd) ||
            rootOwnerHwnd == 0 || scopeHwnd == 0 || processId <= 0 ||
            maxNodes is < 1 or > RecordingContractLimits.MaxControlsPerFrame)
            return 64;

        try
        {
            var expectedStart = new DateTimeOffset(startedTicks, TimeSpan.Zero);
            var before = WindowCatalog.Resolve(rootOwnerHwnd);
            if (before.RootOwnerHwnd != rootOwnerHwnd ||
                before.ProcessId != processId || before.ProcessStartedUtc != expectedStart)
                return 66;

            var mode = args.Length == 6 ? args[5] : "full";
            var scope = WindowCatalog.Resolve(scopeHwnd);
            var sameRootScope = scope.RootOwnerHwnd == rootOwnerHwnd;
            var sameProcessScope = scope.ProcessId == processId && scope.ProcessStartedUtc == expectedStart;
            var detachedProcessScopeAllowed = mode is "full" or "popup" or "dialog" ||
                                               mode.StartsWith("point:", StringComparison.Ordinal) ||
                                               mode.StartsWith("inspect-points:", StringComparison.Ordinal) ||
                                               mode.StartsWith("subtree:", StringComparison.Ordinal) ||
                                               mode.StartsWith("native-", StringComparison.Ordinal);
            if (!sameProcessScope || (!sameRootScope && !detachedProcessScopeAllowed))
                return 66;

            var values = RunCollection(() => mode switch
            {
                "navigation" => BoundedAutomationCollector.CollectNavigationWindow(scopeHwnd, maxNodes),
                "ribbon" => BoundedAutomationCollector.CollectRibbonWindow(scopeHwnd, maxNodes),
                "native-peripheral" => BoundedAutomationCollector.CollectNativePeripheralWindow(scopeHwnd, maxNodes),
                "revit-browser" => BoundedAutomationCollector.CollectRevitBrowserWindow(scopeHwnd, maxNodes),
                "worksheet" => BoundedAutomationCollector.CollectWorksheetWindow(scopeHwnd, maxNodes),
                "legacy" => BoundedAutomationCollector.CollectLegacyWindow(scopeHwnd, maxNodes),
                "view-raw" => BoundedAutomationCollector.CollectViewWindow(scopeHwnd, AutomationTreeView.Raw, maxNodes),
                "view-control" => BoundedAutomationCollector.CollectViewWindow(scopeHwnd, AutomationTreeView.Control, maxNodes),
                "view-content" => BoundedAutomationCollector.CollectViewWindow(scopeHwnd, AutomationTreeView.Content, maxNodes),
                "native-view-raw" => NativeUiaCollector.CollectView(scopeHwnd, AutomationTreeView.Raw, maxNodes),
                "native-view-control" => NativeUiaCollector.CollectView(scopeHwnd, AutomationTreeView.Control, maxNodes),
                "native-view-content" => NativeUiaCollector.CollectView(scopeHwnd, AutomationTreeView.Content, maxNodes),
                "native-focus" => NativeUiaCollector.CollectFocused(scopeHwnd),
                "native-focus-walk" => NativeUiaCollector.CollectFocusWalk(scopeHwnd, maxNodes),
                "popup" => BoundedAutomationCollector.CollectPopupWindow(rootOwnerHwnd, scopeHwnd, maxNodes),
                "dialog" => BoundedAutomationCollector.CollectDialogWindow(rootOwnerHwnd, scopeHwnd, maxNodes),
                "adobe-disclosures" => CollectAdobeDisclosures(scopeHwnd, maxNodes),
                var pointMode when TryParsePointMode(pointMode, out var pointX, out var pointY) =>
                    BoundedAutomationCollector.CollectPointWindow(scopeHwnd, pointX, pointY, maxNodes),
                var nativePointMode when TryParseNativePointMode(nativePointMode, out var nativePointX, out var nativePointY) =>
                    NativeUiaCollector.CollectPoint(scopeHwnd, nativePointX, nativePointY, maxNodes),
                var nativeBandMode when TryParseNativeBandMode(nativeBandMode, out var nativeBand, out var stepX, out var stepY) =>
                    NativeUiaCollector.CollectBand(scopeHwnd, nativeBand, stepX, stepY, maxNodes),
                var inspectionMode when TryParseInspectionPointsMode(inspectionMode, out var inspectionPoints) =>
                    CollectInspectionPoints(scopeHwnd, inspectionPoints, maxNodes),
                var subtreeMode when TryParseSubtreeMode(subtreeMode, out var subtreeX, out var subtreeY) =>
                    BoundedAutomationCollector.CollectLocalSubtreeWindow(scopeHwnd, subtreeX, subtreeY, maxNodes),
                "full" => scopeHwnd == rootOwnerHwnd
                    ? BoundedAutomationCollector.Collect(rootOwnerHwnd, maxNodes)
                    : BoundedAutomationCollector.CollectExactWindow(scopeHwnd, maxNodes),
                _ => throw new ArgumentException("Unknown UI Automation collection mode.")
            });

            var after = WindowCatalog.Resolve(rootOwnerHwnd);
            if (after.RootOwnerHwnd != rootOwnerHwnd ||
                after.ProcessId != processId || after.ProcessStartedUtc != expectedStart)
                return 66;

            Console.Write(JsonSerializer.Serialize(values, JsonLineOptions));
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.Write($"uia-worker-error:{exception.GetType().Name}:{exception.HResult:X8}");
            return 65;
        }
    }

    internal static T RunCollection<T>(Func<T> collect)
    {
        ArgumentNullException.ThrowIfNull(collect);
        // WPF entry points are STA. Provider calls run on an MTA worker so a
        // blocking provider can be terminated with this helper process.
        return Task.Run(collect).GetAwaiter().GetResult();
    }

    private static bool TryParsePointMode(string mode, out int x, out int y)
    {
        x = 0;
        y = 0;
        var parts = mode.Split(':');
        return parts.Length == 3 && parts[0] == "point" &&
               int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out x) &&
               int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out y);
    }

    private static bool TryParseSubtreeMode(string mode, out int x, out int y)
    {
        x = 0;
        y = 0;
        var parts = mode.Split(':');
        return parts.Length == 3 && parts[0] == "subtree" &&
               int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out x) &&
               int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out y);
    }

    private static bool TryParseNativePointMode(string mode, out int x, out int y)
    {
        x = 0;
        y = 0;
        var parts = mode.Split(':');
        return parts.Length == 3 && parts[0] == "native-point" &&
               int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out x) &&
               int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out y);
    }

    private static bool TryParseNativeBandMode(
        string mode, out RectI band, out int stepX, out int stepY)
    {
        band = new RectI(0, 0, 0, 0);
        stepX = 0;
        stepY = 0;
        var parts = mode.Split(':');
        if (parts.Length != 7 || parts[0] != "native-band" ||
            !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var x) ||
            !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var y) ||
            !int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var width) ||
            !int.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var height) ||
            !int.TryParse(parts[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out stepX) ||
            !int.TryParse(parts[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out stepY) ||
            width < 1 || height < 1)
            return false;
        band = new RectI(x, y, width, height);
        return true;
    }

    internal static bool TryParseInspectionPointsMode(
        string mode,
        out IReadOnlyList<RectI> points)
    {
        points = [];
        const string prefix = "inspect-points:";
        if (!mode.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var payload = mode[prefix.Length..];
        if (payload.Length == 0) return false;

        var parsed = new List<RectI>();
        foreach (var token in payload.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            if (parsed.Count >= VisualNativeVerification.MaximumProbePoints) return false;
            var coordinates = token.Split(',');
            if (coordinates.Length != 2 ||
                !int.TryParse(coordinates[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var x) ||
                !int.TryParse(coordinates[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var y))
                return false;
            parsed.Add(new RectI(x, y, 1, 1));
        }
        if (parsed.Count == 0) return false;
        points = parsed;
        return true;
    }

    private static IReadOnlyList<AutomationObservation> CollectInspectionPoints(
        long hwnd,
        IReadOnlyList<RectI> points,
        int maxNodes)
    {
        var native = NativeUiaCollector.CollectPoints(hwnd, points, maxNodes);
        if (native.Count >= maxNodes) return native;
        var msaa = MsaaDialogCollector.CollectPoints(hwnd, points, maxNodes - native.Count);
        var result = native.ToList();
        foreach (var item in msaa)
        {
            if (result.Any(existing => SameInspectionHit(existing, item))) continue;
            result.Add(item);
            if (result.Count >= maxNodes) break;
        }
        return result;
    }

    private static bool SameInspectionHit(
        AutomationObservation left,
        AutomationObservation right)
    {
        if (!string.IsNullOrWhiteSpace(left.RuntimeId) &&
            left.RuntimeId.Equals(right.RuntimeId, StringComparison.Ordinal)) return true;
        if (!left.ControlType.Equals(right.ControlType, StringComparison.OrdinalIgnoreCase) ||
            !left.Name.Equals(right.Name, StringComparison.OrdinalIgnoreCase)) return false;
        var x = Math.Max(left.Bounds.X, right.Bounds.X);
        var y = Math.Max(left.Bounds.Y, right.Bounds.Y);
        var width = Math.Max(0, Math.Min(left.Bounds.X + left.Bounds.Width,
            right.Bounds.X + right.Bounds.Width) - x);
        var height = Math.Max(0, Math.Min(left.Bounds.Y + left.Bounds.Height,
            right.Bounds.Y + right.Bounds.Height) - y);
        var intersection = (long)width * height;
        var smaller = Math.Max(1L, Math.Min(
            (long)left.Bounds.Width * left.Bounds.Height,
            (long)right.Bounds.Width * right.Bounds.Height));
        return intersection / (double)smaller >= .82;
    }

    private static IReadOnlyList<AutomationObservation> CollectAdobeDisclosures(long hwnd, int maxNodes)
    {
        var target = WindowCatalog.Resolve(hwnd);
        if (!AdobePremiereDisclosureDiscovery.IsSupported(target))
            return [];

        var legacy = BoundedAutomationCollector.CollectLegacyWindow(hwnd, 2_000);
        return AdobePremiereDisclosureDiscovery.DiscoverAsync(target, legacy, CancellationToken.None)
            .GetAwaiter().GetResult().Take(maxNodes).ToArray();
    }

    private static readonly JsonSerializerOptions JsonLineOptions =
        new(JsonDefaults.Options) { WriteIndented = false };
}
