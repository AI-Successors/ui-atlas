using System.Runtime.Versioning;
using System.Text;
using UiAtlas.Core.Contracts;

namespace UiAtlas.Core.Recording.Windows;

[SupportedOSPlatform("windows")]
internal sealed record AutoPassStructureSnapshot(
    bool AutomationReliable,
    string SelectedTabKey,
    string VisibleLayerKey,
    string ScopedWindowSignature,
    string RibbonCommandSignature)
{
    public bool HasStructuralChangeComparedTo(AutoPassStructureSnapshot previous)
    {
        ArgumentNullException.ThrowIfNull(previous);

        if (!string.Equals(ScopedWindowSignature, previous.ScopedWindowSignature, StringComparison.Ordinal))
            return true;

        if (!AutomationReliable || !previous.AutomationReliable)
            return false;

        return !string.Equals(SelectedTabKey, previous.SelectedTabKey, StringComparison.Ordinal) ||
               !string.Equals(VisibleLayerKey, previous.VisibleLayerKey, StringComparison.Ordinal) ||
               !string.Equals(RibbonCommandSignature, previous.RibbonCommandSignature, StringComparison.Ordinal);
    }
}

[SupportedOSPlatform("windows")]
internal static class AutoPassStructureSnapshotFactory
{
    public static AutoPassStructureSnapshot Capture(FrameObservation frame, string? fallbackLayerKey)
    {
        ArgumentNullException.ThrowIfNull(frame);

        var discoveredTabs = AutoTabDiscovery.Discover(frame);
        var selectedTab = discoveredTabs.FirstOrDefault(candidate => candidate.IsSelected);
        var selectedTabKey = selectedTab?.StableKey ?? TabHighlightLayerResolver.GlobalLayerKey;
        var visibleLayerKey = TabHighlightLayerResolver.ResolveVisibleLayerKey(frame.Window, frame.Automation, fallbackLayerKey);
        var activeTab = selectedTab
            ?? ResolveTabByLayer(discoveredTabs, fallbackLayerKey)
            ?? discoveredTabs.FirstOrDefault();
        var automationReliable = frame.Automation.Count > 0 &&
            !string.Equals(frame.AutomationStatus, "not-requested", StringComparison.OrdinalIgnoreCase);

        return new(
            automationReliable,
            selectedTabKey,
            visibleLayerKey,
            BuildScopedWindowSignature(frame.ScopedWindows ?? [frame.Window]),
            BuildRibbonCommandSignature(frame, activeTab, automationReliable));
    }

    private static AutoTabCandidate? ResolveTabByLayer(IReadOnlyList<AutoTabCandidate> discoveredTabs, string? fallbackLayerKey)
    {
        var tabLayerKey = OutlookNavigationDiscovery.ExtractTabLayerKey(fallbackLayerKey);
        if (string.IsNullOrWhiteSpace(tabLayerKey) ||
            string.Equals(tabLayerKey, TabHighlightLayerResolver.GlobalLayerKey, StringComparison.Ordinal))
            return null;

        return discoveredTabs.FirstOrDefault(candidate => candidate.StableKey == tabLayerKey);
    }

    private static string BuildScopedWindowSignature(IReadOnlyList<WindowObservation> windows)
    {
        var builder = new StringBuilder();
        foreach (var window in windows
                     .OrderBy(item => item.Hwnd)
                     .ThenBy(item => item.OwnerHwnd)
                     .ThenBy(item => item.ClassName, StringComparer.Ordinal))
        {
            builder.Append(window.Hwnd).Append('|')
                .Append(window.OwnerHwnd).Append('|')
                .Append(window.ClassName).Append(';');
        }

        return builder.ToString();
    }

    private static string BuildRibbonCommandSignature(
        FrameObservation frame,
        AutoTabCandidate? activeTab,
        bool automationReliable)
    {
        if (!automationReliable || activeTab is null)
            return string.Empty;

        var rootBounds = frame.Window.Bounds;
        var tabRowBottom = ResolveTabRowBottom(frame, activeTab);
        var commandBandTop = tabRowBottom + 2;
        var commandBandHeight = Math.Clamp((int)Math.Round(rootBounds.Height * 0.13), 84, 112);
        var commandBandBottom = Math.Min(rootBounds.Y + rootBounds.Height, commandBandTop + commandBandHeight);
        var builder = new StringBuilder();
        foreach (var control in frame.Automation
                     .Where(control => IsVisibleRibbonBandControl(control, rootBounds, commandBandTop, commandBandBottom))
                     .OrderBy(control => control.Bounds.X)
                     .ThenBy(control => control.Bounds.Y)
                     .ThenBy(control => NormalizeControlType(control.ControlType), StringComparer.Ordinal)
                     .ThenBy(control => control.AutomationId, StringComparer.Ordinal)
                     .ThenBy(control => control.Name, StringComparer.Ordinal))
        {
            var bounds = control.Bounds;
            builder.Append(control.RuntimeId).Append('|')
                .Append(control.AutomationId).Append('|')
                .Append(control.Name).Append('|')
                .Append(NormalizeControlType(control.ControlType)).Append('|')
                .Append(control.ClassName).Append('@')
                .Append(bounds.X).Append(',')
                .Append(bounds.Y).Append(',')
                .Append(bounds.Width).Append(',')
                .Append(bounds.Height).Append(';');
        }

        return builder.ToString();
    }

    private static int ResolveTabRowBottom(FrameObservation frame, AutoTabCandidate activeTab)
    {
        var activeTabCenterY = CenterY(activeTab.Observation.Bounds);
        return frame.Automation
            .Where(control => control.IsEnabled && !control.IsOffscreen)
            .Where(control => control.Bounds.Width > 0 && control.Bounds.Height > 0)
            .Where(control => Math.Abs(CenterY(control.Bounds) - activeTabCenterY) <= 24)
            .Select(control => control.Bounds.Y + control.Bounds.Height)
            .DefaultIfEmpty(activeTab.Observation.Bounds.Y + activeTab.Observation.Bounds.Height)
            .Max();
    }

    private static bool IsVisibleRibbonBandControl(
        AutomationObservation control,
        RectI rootBounds,
        int commandBandTop,
        int commandBandBottom)
    {
        if (!control.IsEnabled || control.IsOffscreen)
            return false;

        if (control.Bounds.Width is <= 0 or > 320 || control.Bounds.Height is <= 0 or > 120)
            return false;

        if (control.Bounds.X < rootBounds.X - 4 || control.Bounds.X > rootBounds.X + rootBounds.Width)
            return false;

        var centerY = CenterY(control.Bounds);
        if (centerY < commandBandTop - 6 || centerY > commandBandBottom + 6)
            return false;

        var controlType = NormalizeControlType(control.ControlType);
        if (controlType is "Pane" or "Group" or "ToolBar" or "StatusBar")
            return false;

        var supportedPatterns = control.SupportedPatterns?.Count ?? 0;
        if (supportedPatterns == 0 &&
            string.IsNullOrWhiteSpace(control.AutomationId) &&
            string.IsNullOrWhiteSpace(control.Name))
            return false;

        var combinedIdentity = $"{control.Name} {control.AutomationId} {control.ClassName}";
        return !combinedIdentity.Contains("formula bar", StringComparison.OrdinalIgnoreCase) &&
               !combinedIdentity.Contains("search", StringComparison.OrdinalIgnoreCase) &&
               !combinedIdentity.Contains("quick access", StringComparison.OrdinalIgnoreCase) &&
               !combinedIdentity.Contains("autosave", StringComparison.OrdinalIgnoreCase);
    }

    private static double CenterY(RectI bounds) => bounds.Y + bounds.Height / 2.0;

    private static string NormalizeControlType(string value)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        const string prefix = "ControlType.";
        return trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? trimmed[prefix.Length..]
            : trimmed;
    }
}
