using System.Runtime.Versioning;
using UiAtlas.Core.Contracts;

namespace UiAtlas.Core.Recording.Windows;

[SupportedOSPlatform("windows")]
public sealed record AutoRibbonDialogLauncherCandidate(
    string StableKey,
    string DisplayName,
    AutomationObservation Observation);

[SupportedOSPlatform("windows")]
public static class AutoRibbonDialogLauncherDiscovery
{
    public static IReadOnlyList<AutoRibbonDialogLauncherCandidate> Discover(
        FrameObservation frame,
        AutoTabCandidate activeTab)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(activeTab);

        var tabBottom = activeTab.Observation.Bounds.Y + activeTab.Observation.Bounds.Height;
        var ribbonBottom = tabBottom + Math.Clamp((int)Math.Round(frame.Window.Bounds.Height * 0.13), 84, 116);
        return frame.Automation
            .Where(control => AutomaticInteractionSafety.CanActivate(control, frame.Automation))
            .Where(control => IsLauncher(control, frame.Window.Bounds, tabBottom, ribbonBottom))
            .Select(control => new AutoRibbonDialogLauncherCandidate(
                StableKey(control),
                DisplayName(control),
                control))
            .GroupBy(candidate => candidate.StableKey, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(candidate => candidate.Observation.Bounds.X)
            .ThenBy(candidate => candidate.Observation.Bounds.Y)
            .ToArray();
    }

    private static bool IsLauncher(AutomationObservation control, RectI root, int tabBottom, int ribbonBottom)
    {
        if (!control.IsEnabled || control.IsOffscreen || control.Bounds.Width <= 0 || control.Bounds.Height <= 0)
            return false;
        if (control.Bounds.X < root.X || control.Bounds.X + control.Bounds.Width > root.X + root.Width + 4 ||
            control.Bounds.Y < tabBottom || control.Bounds.Y + control.Bounds.Height > ribbonBottom + 30)
            return false;

        var type = NormalizeControlType(control.ControlType);
        if (type is not ("Button" or "MenuItem" or "Custom"))
            return false;
        if (control.SupportedPatterns?.Any(pattern =>
                pattern.Contains("Invoke", StringComparison.OrdinalIgnoreCase)) != true)
            return false;

        var identity = $"{control.AutomationId} {control.Name} {control.ClassName}";
        var namedLauncher = identity.Contains("dialoglauncher", StringComparison.OrdinalIgnoreCase) ||
                            identity.Contains("dialog launcher", StringComparison.OrdinalIgnoreCase) ||
                            identity.Contains("dialog box launcher", StringComparison.OrdinalIgnoreCase) ||
                            identity.Contains("format cell", StringComparison.OrdinalIgnoreCase) ||
                            control.AutomationId.EndsWith("Options", StringComparison.OrdinalIgnoreCase);
        if (namedLauncher)
            return true;

        // Office versions localize the accessible name, but the launcher remains
        // a tiny NetUI invoke button at the bottom-right edge of a Ribbon group.
        var isTinyNetUiButton = control.Bounds.Width <= 22 && control.Bounds.Height <= 22 &&
                                control.Bounds.Y + control.Bounds.Height >= ribbonBottom - 12 &&
                                control.ClassName.Contains("NetUISimpleButton", StringComparison.OrdinalIgnoreCase);
        return isTinyNetUiButton &&
               (control.AutomationId.Contains("launcher", StringComparison.OrdinalIgnoreCase) ||
                control.Name.Contains("settings", StringComparison.OrdinalIgnoreCase) ||
                control.Name.Contains("dialog", StringComparison.OrdinalIgnoreCase) ||
                control.Name.Contains("options", StringComparison.OrdinalIgnoreCase));
    }

    private static string DisplayName(AutomationObservation control)
    {
        if (!string.IsNullOrWhiteSpace(control.Name)) return control.Name.Trim();
        if (!string.IsNullOrWhiteSpace(control.AutomationId)) return control.AutomationId.Trim();
        return "Dialog launcher";
    }

    private static string StableKey(AutomationObservation control) => string.Join('|',
        control.AutomationId,
        control.Name,
        NormalizeControlType(control.ControlType),
        control.ClassName,
        control.Bounds.X,
        control.Bounds.Y,
        control.Bounds.Width,
        control.Bounds.Height).ToLowerInvariant();

    private static string NormalizeControlType(string value)
    {
        const string prefix = "ControlType.";
        return value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? value[prefix.Length..] : value;
    }
}
