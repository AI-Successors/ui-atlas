using UiAtlas.Core.Contracts;
using UiAtlas.Core.Reader;
using UiAtlas.Core.Recording.Windows;

namespace UiAtlas.Core.Desktop;

internal sealed record LegacyGridEvidenceRepair(
    IReadOnlyList<RectI> Bounds,
    IReadOnlyList<AutomationObservation> Controls,
    int ReplacedControlCount)
{
    public bool ContainsCenter(RectI candidate) => Bounds.Any(bounds =>
    {
        var x = candidate.X + candidate.Width / 2;
        var y = candidate.Y + candidate.Height / 2;
        return x >= bounds.X && x < bounds.X + bounds.Width &&
               y >= bounds.Y && y < bounds.Y + bounds.Height;
    });

    public static async Task<LegacyGridEvidenceRepair?> TryCreateAsync(
        UiEvidenceImage evidence,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        cancellationToken.ThrowIfCancellationRequested();
        var automation = evidence.Observation.Automation;
        // A healthy native Excel tree already describes the painted worksheet.
        // Re-running the pixel repair over it can mistake the sheet-tab/status
        // bands for more rows and make cached offscreen cells appear after the
        // initial correct render.
        if (VisualSurfaceScanner.HasReliableVisibleExcelWorksheet(
                automation, evidence.ScreenshotBounds))
            return null;
        var nativeSemanticControls = automation
            .Where(control => !control.ClassName.Equals("UiAtlas.VisualControlRegion", StringComparison.OrdinalIgnoreCase) &&
                              IsSemanticControl(control))
            .ToArray();
        var coveredTextFragments = automation
            .Where(control => control.ClassName.Equals("UiAtlas.VisualControlRegion", StringComparison.OrdinalIgnoreCase) &&
                              control.ControlType.EndsWith(".Button", StringComparison.OrdinalIgnoreCase) &&
                              nativeSemanticControls.Any(native => OverlapRatio(native.Bounds, control.Bounds) >= .20))
            .ToArray();
        var narrowArtworkFragments = automation
            .Where(control => control.ClassName.Equals("UiAtlas.VisualControlRegion", StringComparison.OrdinalIgnoreCase) &&
                              control.ControlType.EndsWith(".Button", StringComparison.OrdinalIgnoreCase) &&
                              control.Name.Equals("Unlabelled button", StringComparison.OrdinalIgnoreCase) &&
                              control.Bounds.Width <= 30 &&
                              control.Bounds.Height <= 48 &&
                              control.Bounds.Height >= control.Bounds.Width * 1.25)
            .ToArray();

        var screenshotBounds = evidence.ScreenshotBounds;
        var hwnd = automation.Select(control => control.WindowHwnd).FirstOrDefault(value => value != 0);
        var target = new WindowTarget(
            hwnd,
            hwnd,
            0,
            "recorded-evidence",
            DateTimeOffset.UnixEpoch,
            evidence.Observation.Window.Title,
            evidence.Observation.Window.ClassName,
            screenshotBounds);
        var pixels = OpaqueSurfaceScanner.PixelFrame.Decode(evidence.Png);
        var controls = (await VisualSurfaceScanner.DiscoverLegacySurfaceControlsAsync(
                target, pixels, automation, cancellationToken).ConfigureAwait(false))
            .ToArray();
        var structuralIds = controls
            .Where(control => control.ControlType is "ControlType.Table" or "ControlType.Tree" or "ControlType.Tab")
            .Select(control => control.RuntimeId)
            .ToHashSet(StringComparer.Ordinal);
        var replacementBounds = controls
            .Where(control => structuralIds.Contains(control.RuntimeId) ||
                              string.IsNullOrEmpty(control.ParentRuntimeId))
            .Select(control => control.Bounds)
            .ToArray();
        var bounds = replacementBounds
            .Concat(coveredTextFragments.Select(control => control.Bounds))
            .Concat(narrowArtworkFragments.Select(control => control.Bounds))
            .Distinct()
            .ToArray();
        var replacedControlCount = automation
            .Where(control => control.ClassName.Equals("UiAtlas.VisualControlRegion", StringComparison.OrdinalIgnoreCase) &&
                              replacementBounds.Any(replacement => ContainsCenter(replacement, control.Bounds)))
            .Concat(coveredTextFragments)
            .Concat(narrowArtworkFragments)
            .Select(control => control.RuntimeId)
            .Distinct(StringComparer.Ordinal)
            .Count();
        return bounds.Length == 0 || controls.Length == 0 ||
               replacedControlCount == 0 && structuralIds.Count == 0
            ? null
            : new(bounds, controls, replacedControlCount);
    }

    private static bool ContainsCenter(RectI outer, RectI inner)
    {
        var x = inner.X + inner.Width / 2;
        var y = inner.Y + inner.Height / 2;
        return x >= outer.X && x < outer.X + outer.Width &&
               y >= outer.Y && y < outer.Y + outer.Height;
    }

    private static bool IsSemanticControl(AutomationObservation control) =>
        control.ControlType.EndsWith(".MenuItem", StringComparison.OrdinalIgnoreCase) ||
        control.ControlType.EndsWith(".Button", StringComparison.OrdinalIgnoreCase) ||
        control.ControlType.EndsWith(".TitleBar", StringComparison.OrdinalIgnoreCase) ||
        control.ControlType.EndsWith(".Hyperlink", StringComparison.OrdinalIgnoreCase) ||
        control.ControlType.EndsWith(".CheckBox", StringComparison.OrdinalIgnoreCase) ||
        control.ControlType.EndsWith(".RadioButton", StringComparison.OrdinalIgnoreCase) ||
        control.ControlType.EndsWith(".ComboBox", StringComparison.OrdinalIgnoreCase) ||
        control.ControlType.EndsWith(".Edit", StringComparison.OrdinalIgnoreCase) ||
        control.ControlType.EndsWith(".ListItem", StringComparison.OrdinalIgnoreCase) ||
        control.ClassName.EndsWith("Button", StringComparison.OrdinalIgnoreCase);

    private static double OverlapRatio(RectI first, RectI second)
    {
        var width = Math.Max(0,
            Math.Min(first.X + first.Width, second.X + second.Width) - Math.Max(first.X, second.X));
        var height = Math.Max(0,
            Math.Min(first.Y + first.Height, second.Y + second.Height) - Math.Max(first.Y, second.Y));
        var intersection = (long)width * height;
        var smaller = Math.Max(1L, Math.Min((long)first.Width * first.Height, (long)second.Width * second.Height));
        return intersection / (double)smaller;
    }
}
