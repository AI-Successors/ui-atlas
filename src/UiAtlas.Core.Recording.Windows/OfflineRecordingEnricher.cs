using UiAtlas.Core.Contracts;
using UiAtlas.Core.Recording;

namespace UiAtlas.Core.Recording.Windows;

/// <summary>
/// Repairs partial and visual-only observations from their own immutable PNG.
/// This is a build-time projection only; the sealed recording bundle is never edited.
/// </summary>
public static class OfflineRecordingEnricher
{
    public static async Task<IReadOnlyList<FrameObservation>> RepairAsync(
        RecordingBundle bundle,
        IReadOnlyList<FrameObservation> observations,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentNullException.ThrowIfNull(observations);
        var bySequence = observations.ToDictionary(frame => frame.Sequence);
        var result = new List<FrameObservation>(observations.Count);
        foreach (var frame in observations.OrderBy(frame => frame.Sequence))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var legacyStructureRecovery = RequiresLegacyStructureRecovery(frame.Automation);
            var incompleteRootRecovery = RequiresIncompleteRootRecovery(frame);
            var opaqueGalleryRecovery = RequiresOpaqueGalleryRecovery(frame.Automation);
            var visualReclassification = RequiresVisualReclassification(frame.Automation);
            var cachedExcelGridRecovery = RequiresCachedExcelGridRecovery(frame.Automation);
            if ((!string.Equals(frame.Trigger, "adaptive-root-change", StringComparison.Ordinal) ||
                  !frame.AutomationTimedOut && frame.AutomationStatus != "partial") &&
                !legacyStructureRecovery && !opaqueGalleryRecovery && !visualReclassification &&
                !cachedExcelGridRecovery)
            {
                result.Add(frame);
                continue;
            }

            try
            {
                var png = ResolvePng(bundle, frame, bySequence);
                if (png is null)
                {
                    result.Add(frame);
                    continue;
                }
                var screenshot = OpaqueSurfaceScanner.PixelFrame.Decode(png);
                var screenshotBounds = frame.ScreenshotBounds ?? CompositeBounds(frame);
                var window = ResolveVisualTarget(frame);
                var pixels = CropToWindow(screenshot, screenshotBounds, window.Bounds);
                var target = new WindowTarget(
                    window.Hwnd, window.RootOwnerHwnd, window.ProcessId, "recorded-process",
                    DateTimeOffset.UnixEpoch, window.Title, window.ClassName, window.Bounds,
                    window.OwnerHwnd, window.ZOrder, window.Style, window.ExStyle);
                var words = await WindowsOcrTextRecognizer.RecognizeAsync(pixels, cancellationToken)
                    .ConfigureAwait(false);
                var alignedAutomation = VisualSurfaceScanner.RealignOfficeBackstageControls(
                    window.Bounds, pixels.Width, pixels.Height, frame.Automation, words);
                // Office Backstage mixes coordinate spaces for a few compact
                // children even though the rest of its native tree is valid.
                // Those children were aligned to their painted labels above;
                // treating the whole frame as stale would throw away the
                // Templates container needed to recover its painted cards.
                var stale = !opaqueGalleryRecovery &&
                            LooksStale(alignedAutomation, words, window.Bounds, pixels.Width, pixels.Height);
                var opaqueRegions = VisualFallbackPolicy.FindOpaqueRegions(alignedAutomation, window.Bounds);
                var reclassifyVisual = visualReclassification || RequiresVisualReclassification(alignedAutomation);
                if (!stale && opaqueRegions.Count == 0 && !reclassifyVisual &&
                    !legacyStructureRecovery && !incompleteRootRecovery && !cachedExcelGridRecovery)
                {
                    result.Add(frame);
                    continue;
                }

                var stableNative = stale
                    ? alignedAutomation.Where(control => IsStableChrome(control, window.Bounds)).ToArray()
                    : reclassifyVisual
                        ? alignedAutomation.Where(control => !IsVisualFallbackControl(control)).ToArray()
                        : alignedAutomation.ToArray();
                // A cached Excel grid can be geometrically stale while still being the only
                // reliable signal that this painted surface is a worksheet. Keep just its
                // container as a structural hint for the pixel scanner; the stale container
                // and cells are removed from the repaired result below.
                var legacyKnown = cachedExcelGridRecovery
                    ? stableNative.Concat(alignedAutomation.Where(IsCachedExcelGridContainer))
                        .GroupBy(ControlIdentity, StringComparer.Ordinal)
                        .Select(group => group.First())
                        .ToArray()
                    : stableNative;
                Task<IReadOnlyList<AutomationObservation>> visualTask =
                    Task.FromResult<IReadOnlyList<AutomationObservation>>([]);
                if (stale || reclassifyVisual || opaqueRegions.Count > 0 || incompleteRootRecovery)
                {
                    var visualRegions = stale || reclassifyVisual || incompleteRootRecovery
                        ? [window.Bounds]
                        : opaqueRegions;
                    visualTask = Task.Run(
                        () => VisualSurfaceScanner.DiscoverWithWordsAsync(
                            target, pixels, visualRegions, stableNative, words, cancellationToken),
                        cancellationToken);
                }
                Task<IReadOnlyList<AutomationObservation>> legacyTask =
                    legacyStructureRecovery || stale || reclassifyVisual || incompleteRootRecovery ||
                    cachedExcelGridRecovery
                        ? Task.Run(
                            () => VisualSurfaceScanner.DiscoverLegacySurfaceControlsWithWordsAsync(
                                target, pixels, legacyKnown, words, cancellationToken),
                            cancellationToken)
                        : Task.FromResult<IReadOnlyList<AutomationObservation>>([]);
                await Task.WhenAll(visualTask, legacyTask).ConfigureAwait(false);
                var visual = await visualTask.ConfigureAwait(false);
                var legacy = await legacyTask.ConfigureAwait(false);
                var repairedExcelTables = cachedExcelGridRecovery
                    ? legacy.Where(control =>
                            string.Equals(control.Name, "Worksheet grid", StringComparison.Ordinal) &&
                            NormalizeType(control.ControlType) == "Table")
                        .ToArray()
                    : [];
                if (repairedExcelTables.Length > 0)
                {
                    stableNative = stableNative.Where(control => !IsCachedExcelWorksheetControl(control)).ToArray();
                    // The general visual pass may also interpret every worksheet cell as a
                    // generic field/table cell. Prefer the Excel-specific reconstruction so
                    // one precise overlay is emitted instead of two competing grids.
                    visual = visual.Where(control => !repairedExcelTables.Any(table =>
                        IsVisualFallbackControl(control) && ContainsCenter(table.Bounds, control.Bounds))).ToArray();
                }
                var hasStructuredPopupGallery = visual.Any(control =>
                    string.Equals(control.VisualRole, "cell-style-button", StringComparison.Ordinal));
                var popupText = IsVisualPopupFallback(frame) && !hasStructuredPopupGallery
                    ? VisualSurfaceScanner.DiscoverPopupTextListControls(target, pixels, words)
                    : [];
                if (popupText.Count > 0)
                    visual = visual.Where(control => !IsVisualFallbackControl(control)).ToArray();
                IReadOnlyList<AutomationObservation> repaired = DeduplicateGeometry(
                        stableNative.Concat(legacy).Concat(visual).Concat(popupText))
                    .GroupBy(ControlIdentity, StringComparer.Ordinal)
                    .Select(group => group.OrderByDescending(ControlQuality).First())
                    .OrderBy(control => control.Bounds.Y)
                    .ThenBy(control => control.Bounds.X)
                    .ThenBy(control => control.RuntimeId, StringComparer.Ordinal)
                    .ToArray();
                if (IsVisualPopupFallback(frame))
                    repaired = AttachVisualPopupHierarchy(window, repaired, alignedAutomation);
                result.Add(frame with
                {
                    Automation = repaired,
                    AutomationTimedOut = true,
                    AutomationStatus = "partial",
                    Extraction = null
                });
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or
                                           NotSupportedException or System.Runtime.InteropServices.COMException)
            {
                result.Add(frame);
            }
        }
        return result;
    }

    internal static bool RequiresVisualReclassification(
        IReadOnlyList<AutomationObservation> controls) =>
        controls.Any(IsVisualFallbackControl);

    internal static bool RequiresOpaqueGalleryRecovery(
        IReadOnlyList<AutomationObservation> controls) =>
        controls.Any(VisualFallbackPolicy.IsOpaqueGalleryContainer);

    internal static bool RequiresCachedExcelGridRecovery(
        IReadOnlyList<AutomationObservation> controls) =>
        controls.Any(control =>
            control.FrameworkId.StartsWith("UiAtlas.Cached", StringComparison.OrdinalIgnoreCase) &&
            control.ClassName.Equals("XLSpreadsheetGrid", StringComparison.OrdinalIgnoreCase) &&
            control.Bounds.Width >= 240 && control.Bounds.Height >= 80);

    internal static bool RequiresIncompleteRootRecovery(FrameObservation frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return string.Equals(frame.Trigger, "adaptive-root-change", StringComparison.Ordinal) &&
               (frame.AutomationTimedOut || frame.AutomationStatus == "partial");
    }

    internal static bool RequiresLegacyStructureRecovery(
        IReadOnlyList<AutomationObservation> controls)
    {
        if (controls.Any(control => NormalizeType(control.ControlType) == "Table")) return false;
        return controls.Any(control =>
            control.ClassName.Contains("DBGrid", StringComparison.OrdinalIgnoreCase) &&
            !control.FrameworkId.StartsWith("UiAtlas.Cached", StringComparison.OrdinalIgnoreCase) &&
            control.Bounds.Width >= 240 && control.Bounds.Height >= 80);
    }

    private static bool IsVisualFallbackControl(AutomationObservation control) =>
        control.FrameworkId.StartsWith("UiAtlas.Visual.", StringComparison.OrdinalIgnoreCase);

    private static bool IsCachedExcelWorksheetControl(AutomationObservation control) =>
        control.FrameworkId.StartsWith("UiAtlas.Cached", StringComparison.OrdinalIgnoreCase) &&
        (control.ClassName.Equals("XLSpreadsheetGrid", StringComparison.OrdinalIgnoreCase) ||
         control.ClassName.Equals("XLGridColumnHeader", StringComparison.OrdinalIgnoreCase) ||
         control.ClassName.Equals("XLGridRowHeader", StringComparison.OrdinalIgnoreCase) ||
         control.ClassName.Equals("XLSpreadsheetCell", StringComparison.OrdinalIgnoreCase));

    private static bool IsCachedExcelGridContainer(AutomationObservation control) =>
        control.FrameworkId.StartsWith("UiAtlas.Cached", StringComparison.OrdinalIgnoreCase) &&
        control.ClassName.Equals("XLSpreadsheetGrid", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<AutomationObservation> DeduplicateGeometry(
        IEnumerable<AutomationObservation> controls)
    {
        var values = controls.ToArray();
        var leafGroups = values
            .Where(control => NormalizeType(control.ControlType) is "Button" or "Edit")
            .GroupBy(control => control.Bounds)
            .ToDictionary(group => group.Key, group => group
                .OrderByDescending(control => !control.Name.StartsWith("Unlabelled", StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(control => NormalizeType(control.ControlType) == "Button")
                .ThenByDescending(ControlQuality)
                .First());
        return values.Where(control => NormalizeType(control.ControlType) is not ("Button" or "Edit") ||
                                       ReferenceEquals(control, leafGroups[control.Bounds]));
    }

    internal static bool LooksStale(
        IReadOnlyList<AutomationObservation> controls,
        IReadOnlyList<VisualTextObservation> words,
        RectI windowBounds,
        int pixelWidth,
        int pixelHeight)
    {
        if (words.Count < 8 || pixelWidth <= 0 || pixelHeight <= 0) return false;
        if (HasContradictingPageAnchor(controls, words, windowBounds, pixelWidth, pixelHeight))
            return true;
        var candidates = controls.Where(control =>
                !control.IsOffscreen && control.Bounds.Width > 0 && control.Bounds.Height > 0 &&
                control.Bounds.X + control.Bounds.Width / 2 >= windowBounds.X + windowBounds.Width * .18 &&
                control.Bounds.Y + control.Bounds.Height / 2 >= windowBounds.Y + windowBounds.Height * .10 &&
                !IsStableChrome(control, windowBounds) &&
                MeaningfulLeaf(control) && Tokens(control.Name).Count > 0)
            .ToArray();
        if (candidates.Length < 5) return false;

        var scaleX = pixelWidth / (double)Math.Max(1, windowBounds.Width);
        var scaleY = pixelHeight / (double)Math.Max(1, windowBounds.Height);
        var matches = candidates.Count(control =>
        {
            var bounds = new RectI(
                (int)Math.Round((control.Bounds.X - windowBounds.X) * scaleX),
                (int)Math.Round((control.Bounds.Y - windowBounds.Y) * scaleY),
                Math.Max(1, (int)Math.Round(control.Bounds.Width * scaleX)),
                Math.Max(1, (int)Math.Round(control.Bounds.Height * scaleY)));
            var expected = Tokens(control.Name);
            var observed = words.Where(word => ContainsCenter(bounds, word.Bounds))
                .SelectMany(word => Tokens(word.Text)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return expected.Any(observed.Contains);
        });
        return matches < Math.Max(2, (int)Math.Ceiling(candidates.Length * .28));
    }

    private static bool HasContradictingPageAnchor(
        IReadOnlyList<AutomationObservation> controls,
        IReadOnlyList<VisualTextObservation> words,
        RectI windowBounds,
        int pixelWidth,
        int pixelHeight)
    {
        var minimumWideWidth = Math.Max(240, windowBounds.Width * 55 / 100);
        var anchors = controls
            .Where(control => !control.IsOffscreen && !string.IsNullOrWhiteSpace(control.Name) &&
                              control.Bounds.Width >= minimumWideWidth &&
                              control.Bounds.Y >= windowBounds.Y + Math.Max(50, windowBounds.Height / 12) &&
                              control.Bounds.Y < windowBounds.Y + windowBounds.Height * 3 / 5 &&
                              (control.ClassName.Contains("Panel", StringComparison.OrdinalIgnoreCase) ||
                               NormalizeType(control.ControlType) is "Pane" or "Group"))
            .OrderBy(control => control.Bounds.Y)
            .ThenBy(control => control.Bounds.Height)
            .Take(2)
            .ToArray();
        if (anchors.Length == 0) return false;

        var scaleX = pixelWidth / (double)Math.Max(1, windowBounds.Width);
        var scaleY = pixelHeight / (double)Math.Max(1, windowBounds.Height);
        foreach (var anchor in anchors)
        {
            var bounds = new RectI(
                (int)Math.Round((anchor.Bounds.X - windowBounds.X) * scaleX),
                (int)Math.Round((anchor.Bounds.Y - windowBounds.Y) * scaleY),
                Math.Max(1, (int)Math.Round(anchor.Bounds.Width * scaleX)),
                Math.Max(1, (int)Math.Round(anchor.Bounds.Height * scaleY)));
            var expected = Tokens(anchor.Name);
            var observed = words.Where(word => ContainsCenter(bounds, word.Bounds))
                .SelectMany(word => Tokens(word.Text))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (observed.Count == 0) continue;
            if (expected.Any(observed.Contains)) return false;
            return true;
        }
        return false;
    }

    private static byte[]? ResolvePng(
        RecordingBundle bundle,
        FrameObservation frame,
        IReadOnlyDictionary<long, FrameObservation> bySequence)
    {
        var current = frame;
        var visited = new HashSet<long>();
        while (string.IsNullOrWhiteSpace(current.FrameEntry))
        {
            if (!visited.Add(current.Sequence) || current.BaseFrameSequence is not { } baseSequence ||
                !bySequence.TryGetValue(baseSequence, out current))
                return null;
        }
        return bundle.Entries.Contains(current.FrameEntry)
            ? bundle.ReadBytes(current.FrameEntry, 16 * 1024 * 1024)
            : null;
    }

    private static WindowObservation ResolveVisibleRoot(FrameObservation frame) =>
        (frame.ScopedWindows is { Count: > 0 } ? frame.ScopedWindows : [frame.Window])
        .Where(window => window.IsVisible && !window.IsCloaked && !window.IsMinimized &&
                         !window.IsToolWindow && window.Bounds.Width > 0 && window.Bounds.Height > 0)
        .OrderByDescending(window => (long)window.Bounds.Width * window.Bounds.Height)
        .ThenBy(window => window.ZOrder)
        .FirstOrDefault() ?? frame.Window;

    private static WindowObservation ResolveVisualTarget(FrameObservation frame)
    {
        if (IsVisualPopupFallback(frame) && frame.ObservedWindowHwnds is { Count: 1 } observed)
        {
            var popupHwnd = observed[0];
            var popup = (frame.ScopedWindows is { Count: > 0 } ? frame.ScopedWindows : [frame.Window])
                .FirstOrDefault(window => window.Hwnd == popupHwnd && window.Bounds.IsValid);
            if (popup is not null)
                return popup;
        }
        return ResolveVisibleRoot(frame);
    }

    private static bool IsVisualPopupFallback(FrameObservation frame) =>
        string.Equals(frame.ObservationScope, "popup-delta", StringComparison.Ordinal) &&
        string.Equals(frame.AutomationStatus, "visual-only", StringComparison.Ordinal);

    private static IReadOnlyList<AutomationObservation> AttachVisualPopupHierarchy(
        WindowObservation popup,
        IReadOnlyList<AutomationObservation> repaired,
        IReadOnlyList<AutomationObservation> original)
    {
        var root = repaired.FirstOrDefault(control =>
            control.WindowHwnd == popup.Hwnd &&
            string.IsNullOrWhiteSpace(control.ParentRuntimeId) &&
            !IsVisualFallbackControl(control));
        if (root is null || string.IsNullOrWhiteSpace(root.RuntimeId))
            return repaired;

        var controls = repaired.ToList();
        if (!controls.Any(control => !ReferenceEquals(control, root) && IsVisualFallbackControl(control)))
        {
            var retainedSurface = original.FirstOrDefault(control =>
                IsVisualFallbackControl(control) && control.Bounds.IsValid);
            if (retainedSurface is not null)
                controls.Add(retainedSurface);
        }

        var ids = controls
            .Where(control => !string.IsNullOrWhiteSpace(control.RuntimeId))
            .Select(control => control.RuntimeId)
            .ToHashSet(StringComparer.Ordinal);
        return controls.Select(control =>
        {
            if (ReferenceEquals(control, root) || !IsVisualFallbackControl(control))
                return control;
            return control with
            {
                ParentRuntimeId = string.IsNullOrWhiteSpace(control.ParentRuntimeId) ||
                                  !ids.Contains(control.ParentRuntimeId)
                    ? root.RuntimeId
                    : control.ParentRuntimeId,
                IsOffscreen = false,
                WindowHwnd = popup.Hwnd
            };
        }).ToArray();
    }

    private static RectI CompositeBounds(FrameObservation frame)
    {
        var windows = frame.ScopedWindows is { Count: > 0 } ? frame.ScopedWindows : [frame.Window];
        var left = windows.Min(window => window.Bounds.X);
        var top = windows.Min(window => window.Bounds.Y);
        var right = windows.Max(window => window.Bounds.X + window.Bounds.Width);
        var bottom = windows.Max(window => window.Bounds.Y + window.Bounds.Height);
        return new(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));
    }

    private static OpaqueSurfaceScanner.PixelFrame CropToWindow(
        OpaqueSurfaceScanner.PixelFrame screenshot,
        RectI screenshotBounds,
        RectI windowBounds)
    {
        var scaleX = screenshot.Width / (double)Math.Max(1, screenshotBounds.Width);
        var scaleY = screenshot.Height / (double)Math.Max(1, screenshotBounds.Height);
        var left = Math.Clamp((int)Math.Round((windowBounds.X - screenshotBounds.X) * scaleX), 0, screenshot.Width - 1);
        var top = Math.Clamp((int)Math.Round((windowBounds.Y - screenshotBounds.Y) * scaleY), 0, screenshot.Height - 1);
        var right = Math.Clamp((int)Math.Round((windowBounds.X + windowBounds.Width - screenshotBounds.X) * scaleX), left + 1, screenshot.Width);
        var bottom = Math.Clamp((int)Math.Round((windowBounds.Y + windowBounds.Height - screenshotBounds.Y) * scaleY), top + 1, screenshot.Height);
        if (left == 0 && top == 0 && right == screenshot.Width && bottom == screenshot.Height)
            return screenshot;
        var width = right - left;
        var height = bottom - top;
        var pixels = new byte[checked(width * height * 4)];
        for (var y = 0; y < height; y++)
            Buffer.BlockCopy(screenshot.Pixels, ((top + y) * screenshot.Width + left) * 4,
                pixels, y * width * 4, width * 4);
        return new(width, height, pixels);
    }

    private static bool IsStableChrome(AutomationObservation control, RectI windowBounds)
    {
        if (control.IsOffscreen || control.Bounds.Width <= 0 || control.Bounds.Height <= 0) return false;
        var type = NormalizeType(control.ControlType);
        if (type is "Window" or "TitleBar" or "MenuBar" or "MenuItem" or "StatusBar") return true;
        if (type != "Button") return false;
        var identity = string.IsNullOrWhiteSpace(control.AutomationId) ? control.Name : control.AutomationId;
        return identity is "Minimize" or "Maximize" or "Restore" or "Restore Down" or "Close" ||
               control.ClassName.Equals("TAbacreButton", StringComparison.OrdinalIgnoreCase) &&
               (control.Bounds.X + control.Bounds.Width <= windowBounds.X + windowBounds.Width * .18 ||
                control.Bounds.Y + control.Bounds.Height <= windowBounds.Y + windowBounds.Height * .18);
    }

    private static bool MeaningfulLeaf(AutomationObservation control) =>
        NormalizeType(control.ControlType) is
            "Button" or "SplitButton" or "CheckBox" or "RadioButton" or "ComboBox" or "Edit" or "DataItem" or
            "HeaderItem" or "ListItem" or "TreeItem" or "TabItem" or "Text" ||
        control.ClassName.EndsWith("Button", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlySet<string> Tokens(string value) => string.Join(' ', value
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        .Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .Select(token => new string(token.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant())
        .Where(token => token.Length >= 3)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static bool ContainsCenter(RectI outer, RectI inner)
    {
        var x = inner.X + inner.Width / 2;
        var y = inner.Y + inner.Height / 2;
        return x >= outer.X && x < outer.X + outer.Width && y >= outer.Y && y < outer.Y + outer.Height;
    }

    private static string ControlIdentity(AutomationObservation control) =>
        $"{control.WindowHwnd}|{control.RuntimeId}|{control.Bounds.X},{control.Bounds.Y},{control.Bounds.Width},{control.Bounds.Height}";

    private static int ControlQuality(AutomationObservation control) =>
        (control.FrameworkId.StartsWith("UiAtlas.Visual", StringComparison.OrdinalIgnoreCase) ? 0 : 100) +
        (string.IsNullOrWhiteSpace(control.Name) ? 0 : 10);

    private static string NormalizeType(string value) =>
        value.Contains('.') ? value[(value.LastIndexOf('.') + 1)..] : value;
}
