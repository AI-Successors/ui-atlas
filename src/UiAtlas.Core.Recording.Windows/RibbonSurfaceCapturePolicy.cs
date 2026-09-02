using UiAtlas.Core.Contracts;

namespace UiAtlas.Core.Recording.Windows;

internal readonly record struct RibbonSurfaceCaptureProfile(
    TimeSpan NavigationTimeout,
    int NavigationMaxNodes,
    TimeSpan RibbonTimeout,
    int RibbonMaxNodes);

internal static class RibbonSurfaceCapturePolicy
{
    // These are worker deadlines, never mandatory sleeps. A responsive Ribbon
    // returns immediately. The dense retry exists for Home and other large Office
    // tabs whose provider tree cannot be materialized inside the fast ceiling.
    public static RibbonSurfaceCaptureProfile Fast { get; } = new(
        TimeSpan.FromMilliseconds(1_400), 300,
        TimeSpan.FromMilliseconds(3_200), 900);

    public static RibbonSurfaceCaptureProfile DenseRetry { get; } = new(
        TimeSpan.FromMilliseconds(2_500), 600,
        TimeSpan.FromMilliseconds(6_500), 1_800);

    public static RibbonSurfaceCaptureProfile CommandScan { get; } = new(
        TimeSpan.FromMilliseconds(1_400), 300,
        TimeSpan.FromMilliseconds(3_500), 1_200);

    public static RibbonSurfaceCaptureProfile RevitFast { get; } = new(
        TimeSpan.FromMilliseconds(1_800), 320,
        TimeSpan.FromMilliseconds(5_500), 1_200);

    public static RibbonSurfaceCaptureProfile RevitCommandScan { get; } = new(
        TimeSpan.FromMilliseconds(1_800), 320,
        TimeSpan.FromMilliseconds(4_500), 1_200);

    public static RibbonSurfaceCaptureProfile RevitDenseRetry { get; } = new(
        TimeSpan.FromMilliseconds(2_500), 600,
        TimeSpan.FromSeconds(9), 2_000);

    public static RibbonSurfaceCaptureProfile ForTarget(
        WindowTarget target,
        RibbonSurfaceCaptureProfile fallback)
    {
        ArgumentNullException.ThrowIfNull(target);
        var identity = $"{target.ProcessName} {target.Title} {target.ProductName} {target.CompanyName}";
        if (!identity.Contains("Revit", StringComparison.OrdinalIgnoreCase) ||
            !identity.Contains("Autodesk", StringComparison.OrdinalIgnoreCase))
            return fallback;

        // Revit's provider benefits from a little more time than Office, but a
        // blanket 45-second deadline made every missed tab look like a frozen
        // recorder. Preserve the caller's coarse-to-fine intent and spend the
        // larger budget only on the explicit dense retry.
        if (fallback == DenseRetry)
            return RevitDenseRetry;
        if (fallback == CommandScan)
            return RevitCommandScan;
        return RevitFast;
    }

    public static bool NeedsVisibleApplicationBody(WindowTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var identity = $"{target.ProcessName} {target.Title} {target.ProductName} {target.OriginalFilename}";
        return identity.Contains("OUTLOOK", StringComparison.OrdinalIgnoreCase) ||
               identity.Contains("Microsoft Outlook", StringComparison.OrdinalIgnoreCase);
    }

    public static bool HasMaterializedRibbonContent(IReadOnlyList<AutomationObservation> controls)
    {
        ArgumentNullException.ThrowIfNull(controls);
        return controls.Any(control =>
            control.Bounds.Width > 0 &&
            control.Bounds.Height > 0 &&
            !control.IsOffscreen &&
            !control.ControlType.Equals("TabItem", StringComparison.OrdinalIgnoreCase) &&
            !control.ControlType.EndsWith(".TabItem", StringComparison.OrdinalIgnoreCase));
    }
}
