using System.Runtime.Versioning;
using UiAtlas.Core.Contracts;

namespace UiAtlas.Core.Recording.Windows;

[SupportedOSPlatform("windows")]
public enum AutoBackstageActionKind
{
    Popup,
    Dialog,
    Inline
}

[SupportedOSPlatform("windows")]
public sealed record AutoBackstageActionCandidate(
    string StableKey,
    string DisplayName,
    AutoBackstageActionKind Kind,
    AutomationObservation Observation);

[SupportedOSPlatform("windows")]
public static class AutoBackstageActionDiscovery
{
    private static readonly IReadOnlyDictionary<string, AutoBackstageActionKind> SafeActions =
        new Dictionary<string, AutoBackstageActionKind>(StringComparer.OrdinalIgnoreCase)
        {
            ["Protect Workbook"] = AutoBackstageActionKind.Popup,
            ["Check for Issues"] = AutoBackstageActionKind.Popup,
            ["Manage Workbook"] = AutoBackstageActionKind.Popup,
            ["Browser View Options"] = AutoBackstageActionKind.Dialog,
            ["Version History"] = AutoBackstageActionKind.Inline
        };

    public static IReadOnlyList<AutoBackstageActionCandidate> Discover(FrameObservation frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (!VisualSurfaceScanner.IsOfficeBackstageSurface(frame.Automation)) return [];

        var root = frame.Window.Bounds;
        var bodyLeft = root.X + Math.Min(200, (int)Math.Round(root.Width * 0.10));
        return frame.Automation
            .Where(control => control.Bounds.IsValid &&
                              control.Bounds.X >= bodyLeft &&
                              control.Bounds.X + control.Bounds.Width <= root.X + root.Width + 4)
            .Where(IsUsableAction)
            .Select(control => new AutoBackstageActionCandidate(
                StableKey(control),
                control.Name.Trim(),
                SafeActions[control.Name.Trim()],
                control))
            .GroupBy(candidate => candidate.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(candidate => IsNative(candidate.Observation))
                .ThenBy(candidate => candidate.Observation.Bounds.Y)
                .First())
            // Leave an inline navigation change until last so it cannot hide the
            // remaining popup and dialog controls on the Info page.
            .OrderBy(candidate => candidate.Kind == AutoBackstageActionKind.Inline ? 1 : 0)
            .ThenBy(candidate => candidate.Observation.Bounds.Y)
            .ThenBy(candidate => candidate.Observation.Bounds.X)
            .ToArray();
    }

    private static bool IsUsableAction(AutomationObservation control)
    {
        var name = control.Name?.Trim() ?? string.Empty;
        if (!SafeActions.ContainsKey(name)) return false;

        var type = NormalizeControlType(control.ControlType);
        if (type is not ("Button" or "MenuItem" or "Hyperlink")) return false;

        // Screenshot-derived controls intentionally remain unverified in the
        // graph, but their bounds are valid hit targets for this narrow allowlist.
        return IsVisual(control) || control.IsEnabled && !control.IsOffscreen;
    }

    private static bool IsNative(AutomationObservation control) => !IsVisual(control);

    private static bool IsVisual(AutomationObservation control) =>
        control.ClassName.Equals("UiAtlas.VisualControlRegion", StringComparison.OrdinalIgnoreCase) &&
        control.FrameworkId.StartsWith("UiAtlas.Visual", StringComparison.OrdinalIgnoreCase);

    private static string StableKey(AutomationObservation control) => string.Join('|',
        control.Name.Trim().ToLowerInvariant(),
        NormalizeControlType(control.ControlType).ToLowerInvariant(),
        control.Bounds.X,
        control.Bounds.Y,
        control.Bounds.Width,
        control.Bounds.Height);

    private static string NormalizeControlType(string value)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        const string prefix = "ControlType.";
        return trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? trimmed[prefix.Length..]
            : trimmed;
    }
}
