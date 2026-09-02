using System.Runtime.Versioning;
using UiAtlas.Core.Contracts;

namespace UiAtlas.Core.Recording.Windows;

[SupportedOSPlatform("windows")]
public static class TabHighlightLayerResolver
{
    public const string GlobalLayerKey = "__global__";

    public static string ResolveLayerKey(FrameObservation frame, IReadOnlyList<RectI> absoluteBounds)
        => ResolveLayerKey(frame, absoluteBounds, fallbackLayerKey: null);

    public static string ResolveLayerKey(FrameObservation frame, IReadOnlyList<RectI> absoluteBounds, string? fallbackLayerKey)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(absoluteBounds);

        var discoveredTabs = AutoTabDiscovery.Discover(frame);
        if (OverlapsAnyTab(discoveredTabs, absoluteBounds))
            return GlobalLayerKey;

        var outlookModule = OutlookNavigationDiscovery.ResolveActive(frame);
        if (outlookModule is not null && absoluteBounds.Any(bounds =>
                Intersects(bounds, outlookModule.Observation.Bounds) ||
                OutlookNavigationDiscovery.Discover(frame).Any(candidate =>
                    Intersects(bounds, candidate.Observation.Bounds))))
            return GlobalLayerKey;

        var activeTab = discoveredTabs.FirstOrDefault(candidate => candidate.IsSelected);
        var tabAnchor = activeTab ?? discoveredTabs.FirstOrDefault();
        if (tabAnchor is null)
            return outlookModule is null
                ? GlobalLayerKey
                : OutlookNavigationDiscovery.ModuleLayerKey(outlookModule);

        var tabRowBottom = ResolveTabRowBottom(frame, tabAnchor);
        var commandBandTop = tabRowBottom + 2;
        var commandBandHeight = Math.Clamp((int)Math.Round(frame.Window.Bounds.Height * 0.13), 84, 124);
        var commandBandBottom = Math.Min(frame.Window.Bounds.Y + frame.Window.Bounds.Height, commandBandTop + commandBandHeight);

        foreach (var bounds in absoluteBounds.Where(bounds => bounds.Width > 0 && bounds.Height > 0))
        {
            var top = bounds.Y;
            var bottom = bounds.Y + bounds.Height;
            if (bottom <= tabRowBottom + 4)
                return GlobalLayerKey;

            var overlapsCommandBand =
                bottom >= commandBandTop - 6 &&
                top <= commandBandBottom + 6;
            if (overlapsCommandBand)
            {
                var tabLayerKey = activeTab?.StableKey ??
                                  OutlookNavigationDiscovery.ExtractTabLayerKey(fallbackLayerKey) ??
                                  GlobalLayerKey;
                return outlookModule is null
                    ? tabLayerKey
                    : OutlookNavigationDiscovery.CombineWithTab(
                        OutlookNavigationDiscovery.ModuleLayerKey(outlookModule), tabLayerKey);
            }
        }

        return outlookModule is null
            ? GlobalLayerKey
            : OutlookNavigationDiscovery.ModuleLayerKey(outlookModule);
    }

    public static string ResolveVisibleLayerKey(WindowObservation window, IReadOnlyList<AutomationObservation> automation)
        => ResolveVisibleLayerKey(window, automation, fallbackLayerKey: null);

    public static string ResolveVisibleLayerKey(WindowObservation window, IReadOnlyList<AutomationObservation> automation, string? fallbackLayerKey)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(automation);

        var frame = CreateFrame(window, automation);
        return ResolveVisibleLayerKey(frame, [], fallbackLayerKey);
    }

    public static string ResolveVisibleLayerKey(FrameObservation frame, IReadOnlyList<RectI> absoluteBounds, string? fallbackLayerKey)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(absoluteBounds);

        var discoveredTabs = AutoTabDiscovery.Discover(frame);
        var outlookModule = OutlookNavigationDiscovery.ResolveActive(frame);
        if (TryResolveClickedTab(discoveredTabs, absoluteBounds, out var clickedTabLayerKey))
            return outlookModule is null
                ? clickedTabLayerKey
                : OutlookNavigationDiscovery.CombineWithTab(
                    OutlookNavigationDiscovery.ModuleLayerKey(outlookModule), clickedTabLayerKey);

        var activeTab = discoveredTabs.FirstOrDefault(candidate => candidate.IsSelected);
        if (activeTab is not null)
            return outlookModule is null
                ? activeTab.StableKey
                : OutlookNavigationDiscovery.CombineWithTab(
                    OutlookNavigationDiscovery.ModuleLayerKey(outlookModule), activeTab.StableKey);
        if (outlookModule is not null)
            return OutlookNavigationDiscovery.ModuleLayerKey(outlookModule);
        return NormalizeLayerKey(fallbackLayerKey);
    }

    private static AutoTabCandidate? ResolveSelectedTab(FrameObservation frame) =>
        AutoTabDiscovery.Discover(frame).FirstOrDefault(candidate => candidate.IsSelected);

    private static int ResolveTabRowBottom(FrameObservation frame, AutoTabCandidate activeTab)
    {
        var activeTabCenterY = activeTab.Observation.Bounds.Y + activeTab.Observation.Bounds.Height / 2.0;
        return frame.Automation
            .Where(control => control.IsEnabled && !control.IsOffscreen)
            .Where(control => control.Bounds.Width > 0 && control.Bounds.Height > 0)
            .Where(control => Math.Abs((control.Bounds.Y + control.Bounds.Height / 2.0) - activeTabCenterY) <= 24)
            .Select(control => control.Bounds.Y + control.Bounds.Height)
            .DefaultIfEmpty(activeTab.Observation.Bounds.Y + activeTab.Observation.Bounds.Height)
            .Max();
    }

    private static bool OverlapsAnyTab(IReadOnlyList<AutoTabCandidate> discoveredTabs, IReadOnlyList<RectI> absoluteBounds) =>
        discoveredTabs.Any(tab => absoluteBounds.Any(bounds => Intersects(bounds, tab.Observation.Bounds)));

    private static bool TryResolveClickedTab(
        IReadOnlyList<AutoTabCandidate> discoveredTabs,
        IReadOnlyList<RectI> absoluteBounds,
        out string layerKey)
    {
        foreach (var tab in discoveredTabs)
        {
            if (absoluteBounds.Any(bounds => Intersects(bounds, tab.Observation.Bounds)))
            {
                layerKey = tab.StableKey;
                return true;
            }
        }

        layerKey = GlobalLayerKey;
        return false;
    }

    private static bool Intersects(RectI first, RectI second) =>
        first.X < (long)second.X + second.Width &&
        second.X < (long)first.X + first.Width &&
        first.Y < (long)second.Y + second.Height &&
        second.Y < (long)first.Y + first.Height;

    private static string NormalizeLayerKey(string? layerKey) =>
        string.IsNullOrWhiteSpace(layerKey) ? GlobalLayerKey : layerKey;

    private static FrameObservation CreateFrame(WindowObservation window, IReadOnlyList<AutomationObservation> automation) =>
        new(
            Sequence: 0,
            TimestampUtc: DateTimeOffset.UtcNow,
            FrameEntry: "",
            Window: window,
            Automation: automation,
            AutomationTimedOut: false,
            AutomationStatus: "ok",
            Trigger: "overlay-layer");
}
