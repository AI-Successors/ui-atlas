using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UiAtlas.Core.Contracts;

namespace UiAtlas.Core.Recording.Windows;

public sealed record OpaqueSurfaceScanResult(
    IReadOnlyList<AutomationObservation> Controls,
    bool InterruptedByUser,
    bool TimedOut,
    int HoverProbeCount,
    int FocusProbeCount,
    IReadOnlyList<string> DiagnosticCodes,
    int HoverStateCount = 0);

/// <summary>
/// Builds non-actionable shadow controls for a provider-opaque surface. It may
/// move the pointer and traverse keyboard focus, but never sends a mouse button,
/// Enter or Space. Every side effect is restored in a finally block.
/// </summary>
public static class OpaqueSurfaceScanner
{
    private const int MaximumHoverProbes = 42;
    private const int MaximumFocusProbes = 32;
    private const int MaximumHoverStates = 4;

    public static async Task<OpaqueSurfaceScanResult> ScanAsync(
        ManualRecordingSession session,
        WindowTarget target,
        IReadOnlyList<CoverageGapObservation> gaps,
        TimeSpan budget,
        CancellationToken cancellationToken,
        IReadOnlyList<AutomationObservation>? knownControls = null,
        bool enableHoverAndFocusDiscovery = true,
        bool allowOcrFallback = true)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(gaps);
        if (budget <= TimeSpan.Zero)
            return new([], false, true, 0, 0, ["shadow-budget-exhausted"]);

        var regions = SelectRegions(gaps, target.Bounds);

        using var timeout = new CancellationTokenSource(budget);
        using var inputGuard = new UserInputCancellationMonitor();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, timeout.Token, inputGuard.Token);
        var token = linked.Token;
        var hasOriginalCursor = NativeMethods.GetCursorPos(out var originalCursor);
        var controls = new List<AutomationObservation>();
        var diagnostics = new List<string>();
        var timer = Stopwatch.StartNew();
        var hoverCount = 0;
        var focusCount = 0;
        var hoverStateCount = 0;
        var pointVerificationIncomplete = false;
        var initialWindowHandles = WindowCatalog.ListProcessWindows(target)
            .Select(window => window.Hwnd)
            .ToHashSet();
        var capturedPopupShapes = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            var baselineCapture = await session.CaptureScreenshotAsync(
                captureToken => WindowSnapshotCapture.CapturePngAsync(
                    [target], captureToken, preferScreenBounds: true),
                token).ConfigureAwait(false);
            var baseline = PixelFrame.Decode(baselineCapture.Png);
            var recorderBounds = RecorderWindowExclusion.Find(target);
            var nativeControls = knownControls ?? [];
            var opaqueRegions = VisualFallbackPolicy.FindOpaqueRegions(nativeControls, target.Bounds);
            var scanRegions = opaqueRegions.Count > 0
                ? regions.Concat(opaqueRegions).Distinct().ToArray()
                : regions;
            var useOcr = allowOcrFallback &&
                         (VisualFallbackPolicy.ShouldUseOcrFallback(nativeControls) || opaqueRegions.Count > 0);
            IReadOnlyList<AutomationObservation> visualControls;
            if (useOcr)
            {
                var remaining = budget - timer.Elapsed - TimeSpan.FromMilliseconds(250);
                if (remaining > TimeSpan.FromMilliseconds(100))
                {
                    using var visualCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
                    var visualTask = VisualSurfaceScanner.DiscoverAsync(
                        target, baseline, scanRegions, nativeControls,
                        visualCancellation.Token, recorderBounds);
                    var visualResult = await TryCompleteWithinAsync(
                        visualTask, remaining, token).ConfigureAwait(false);
                    if (visualResult.Completed)
                    {
                        visualControls = visualResult.Result;
                    }
                    else
                    {
                        visualCancellation.Cancel();
                        ObserveBackgroundFailure(visualTask);
                        visualControls = VisualSurfaceScanner.DiscoverGeometry(
                            target, baseline, scanRegions, nativeControls, recorderBounds);
                        diagnostics.Add("visual-ocr-timeout");
                    }
                }
                else
                {
                    visualControls = VisualSurfaceScanner.DiscoverGeometry(
                        target, baseline, scanRegions, nativeControls, recorderBounds);
                    diagnostics.Add("visual-ocr-budget-exhausted");
                }
            }
            else
            {
                visualControls = VisualSurfaceScanner.DiscoverGeometry(
                    target, baseline, scanRegions, nativeControls, recorderBounds);
            }
            visualControls = RecorderWindowExclusion.FilterControls(visualControls, recorderBounds);
            // Preserve passive visual evidence if the isolated native worker is
            // cancelled with the overall scan budget before it can answer.
            if (useOcr) controls.AddRange(visualControls);
            var inspectionPoints = VisualNativeVerification.PlanAll(visualControls, nativeControls);
            if (inspectionPoints.Count > 0)
            {
                controls.Clear();
                var inspectedNative = new List<AutomationObservation>();
                foreach (var batch in inspectionPoints.Chunk(VisualNativeVerification.MaximumProbePoints))
                {
                    var inspection = await session.CollectInspectionPointsAutomationAsync(
                        target.Hwnd,
                        batch,
                        TimeSpan.FromMilliseconds(3_000),
                        Math.Max(1_200, batch.Length * 16),
                        token).ConfigureAwait(false);
                    inspectedNative.AddRange(inspection.Items);
                    pointVerificationIncomplete |= inspection.TimedOut ||
                                                   inspection.Status is not ("ok" or "node-limit");
                }
                controls.AddRange(inspectedNative);
                if (useOcr)
                    controls.AddRange(VisualNativeVerification.RetainUnconfirmedVisuals(
                        visualControls, inspectedNative));
                else
                    controls.AddRange(VisualNativeVerification.RetainUnconfirmedStructures(
                        visualControls, inspectedNative));
                diagnostics.Add(pointVerificationIncomplete
                    ? "native-point-verification-partial"
                    : $"native-point-controls:{inspectedNative.Count}");
            }
            else if (!useOcr)
            {
                controls.AddRange(VisualNativeVerification.RetainUnconfirmedStructures(
                    visualControls, []));
            }
            diagnostics.Add(visualControls.Count == 0
                ? "visual-no-rectangles"
                : useOcr
                    ? $"visual-ocr-fallback:{visualControls.Count}"
                    : $"visual-native-probes:{visualControls.Count}");
            if (!enableHoverAndFocusDiscovery)
                diagnostics.Add("shadow-active-probes-disabled");
            else
                // Pointer or keyboard activity must only cancel the invasive
                // hover/focus probes. The passive screenshot recognition above
                // is still valid and must remain available for Auto labels.
                inputGuard.Start();

            foreach (var point in enableHoverAndFocusDiscovery
                         ? ProbePoints(regions).Take(MaximumHoverProbes)
                         : [])
            {
                token.ThrowIfCancellationRequested();
                if (WindowCatalog.GetRootOwnerHandle(NativeMethods.GetForegroundWindow()).ToInt64() != target.RootOwnerHwnd)
                {
                    diagnostics.Add("shadow-target-lost-focus");
                    break;
                }
                if (!SafeSyntheticInput.MovePointer(point.X, point.Y))
                {
                    diagnostics.Add("shadow-pointer-move-failed");
                    break;
                }
                hoverCount++;
                await Task.Delay(55, token).ConfigureAwait(false);
                var currentCapture = await session.CaptureScreenshotAsync(
                    captureToken => WindowSnapshotCapture.CapturePngAsync(
                        [target], captureToken, preferScreenBounds: true),
                    token).ConfigureAwait(false);
                var current = PixelFrame.Decode(currentCapture.Png);
                var changed = DetectHoverBounds(baseline, current, target.Bounds, point);
                var popupWindows = WindowCatalog.ListProcessWindows(target)
                    .Where(window => !initialWindowHandles.Contains(window.Hwnd) &&
                                     window.Bounds.Width > 0 && window.Bounds.Height > 0)
                    .ToArray();

                if (changed is not null)
                {
                    var existing = controls.FindIndex(control => Overlaps(control.Bounds, changed));
                    if (existing < 0 || controls[existing].ClassName == "UiAtlas.VisualControlRegion")
                    {
                        var pointRead = await session.CollectNativePointAutomationAsync(
                            target.Hwnd,
                            new RectI(point.X, point.Y, 1, 1),
                            TimeSpan.FromMilliseconds(450),
                            32,
                            token).ConfigureAwait(false);
                        var actual = pointRead.Items
                            .Where(item => item.Bounds.Width > 0 && item.Bounds.Height > 0 &&
                                           Contains(item.Bounds, point) && !IsContainer(item.ControlType))
                            .OrderBy(item => (long)item.Bounds.Width * item.Bounds.Height)
                            .FirstOrDefault();
                        if (actual is not null)
                        {
                            if (existing >= 0) controls[existing] = actual;
                            else controls.Add(actual);
                        }
                        else if (existing < 0)
                        {
                            var tooltipName = await TryReadTooltipAsync(session, target, token).ConfigureAwait(false);
                            controls.Add(CreateHoverControl(target, changed, tooltipName));
                        }
                    }
                }

                var stateShape = popupWindows.Length > 0
                    ? "popup:" + PopupShape(popupWindows, target.Bounds)
                    : changed is not null && IsMaterializedHoverState(changed)
                        ? $"inline:{changed.X / 8}:{changed.Y / 8}:{changed.Width / 8}:{changed.Height / 8}"
                        : string.Empty;
                if (hoverStateCount < MaximumHoverStates && stateShape.Length > 0 &&
                    capturedPopupShapes.Add(stateShape))
                {
                    foreach (var popup in popupWindows.Take(2))
                    {
                        var popupRead = await session.CollectNativeAutomationViewAsync(
                            popup.Hwnd,
                            AutomationTreeView.Raw,
                            TimeSpan.FromMilliseconds(250),
                            200,
                            token).ConfigureAwait(false);
                        controls.AddRange(popupRead.Items);
                        if (popupRead.Items.Count > 1) continue;
                        var legacyRead = await session.CollectLegacyAutomationAsync(
                            popup.Hwnd,
                            TimeSpan.FromMilliseconds(250),
                            200,
                            token).ConfigureAwait(false);
                        controls.AddRange(legacyRead.Items);
                    }
                    await session.CaptureAsync(
                        "quick-map:hover-state",
                        token,
                        new FrameCaptureOptions(
                            IncludeAutomation: false,
                            CapturePhase: "materialized",
                            ObservationScope: "full-root",
                            ObservedWindowHwnds: [target.Hwnd, .. popupWindows.Select(window => window.Hwnd)],
                            ScreenshotWindowHwnds: [target.Hwnd, .. popupWindows.Select(window => window.Hwnd)],
                            AutomationOverride: controls.ToArray(),
                            AutomationStatusOverride: "partial",
                            ScreenshotTimeout: TimeSpan.FromMilliseconds(500),
                            AdditionalScopedWindowHwnds: popupWindows.Select(window => window.Hwnd).ToArray(),
                            PreferScreenBoundsScreenshot: true)).ConfigureAwait(false);
                    hoverStateCount++;
                }
            }

            if (enableHoverAndFocusDiscovery)
            {
                token.ThrowIfCancellationRequested();
                var focusRead = await session.CollectNativeFocusWalkAutomationAsync(
                    target.Hwnd,
                    TimeSpan.FromMilliseconds(Math.Max(250, Math.Min(2_400, budget.TotalMilliseconds / 3))),
                    MaximumFocusProbes,
                    token).ConfigureAwait(false);
                focusCount = focusRead.Items.Count;
                foreach (var focused in focusRead.Items)
                {
                    if (focused.Bounds.Width <= 0 || focused.Bounds.Height <= 0 ||
                        !Contains(target.Bounds, focused.Bounds)) continue;
                    var existing = controls.FindIndex(control => Overlaps(control.Bounds, focused.Bounds));
                    if (existing >= 0)
                        controls[existing] = focused;
                    else
                        controls.Add(focused);
                }
            }

            diagnostics.Add(controls.Count == 0 ? "shadow-no-controls" : "shadow-probe-complete");
            return new(Distinct(controls), false, pointVerificationIncomplete,
                hoverCount, focusCount, diagnostics, hoverStateCount);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            var interrupted = inputGuard.WasCancelledByUser;
            diagnostics.Add(interrupted ? "shadow-cancelled-user-input" :
                inputGuard.StartupFailed ? "shadow-input-monitor-unavailable" : "shadow-timeout");
            return new(Distinct(controls), interrupted, !interrupted || pointVerificationIncomplete,
                hoverCount, focusCount, diagnostics, hoverStateCount);
        }
        finally
        {
            // Never fight a person who has started using the mouse or keyboard.
            // Otherwise leave the application exactly as the passive probe found it.
            if (enableHoverAndFocusDiscovery && !inputGuard.WasCancelledByUser && !inputGuard.StartupFailed)
            {
                _ = SafeSyntheticInput.PressKey(NativeMethods.VkEscape);
                _ = SafeSyntheticInput.PressKey(NativeMethods.VkEscape);
                if (hasOriginalCursor)
                    _ = SafeSyntheticInput.MovePointer(originalCursor.X, originalCursor.Y);
            }
        }
    }

    internal static async Task<(bool Completed, T Result)> TryCompleteWithinAsync<T>(
        Task<T> task,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (timeout <= TimeSpan.Zero)
            return (false, default!);

        var delay = Task.Delay(timeout, cancellationToken);
        if (await Task.WhenAny(task, delay).ConfigureAwait(false) == task)
            return (true, await task.ConfigureAwait(false));

        cancellationToken.ThrowIfCancellationRequested();
        return (false, default!);
    }

    private static void ObserveBackgroundFailure(Task task) =>
        _ = task.ContinueWith(
            completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    internal static IReadOnlyList<RectI> SelectRegions(
        IReadOnlyList<CoverageGapObservation> gaps,
        RectI root)
    {
        var candidates = gaps
            .Where(gap => gap.Kind is CoverageGapKind.EmptyContainer or CoverageGapKind.LargeContainer or
                          CoverageGapKind.ViewDivergence)
            .Select(gap => Intersect(gap.Bounds, root))
            .Where(bounds => bounds.Width >= 24 && bounds.Height >= 18)
            .Distinct()
            .ToArray();
        if (candidates.Length > 0)
        {
            var selected = new List<RectI>(3);
            var commandOrUpperBody = candidates
                .Where(bounds => bounds.Y < root.Y + root.Height / 3)
                .OrderByDescending(bounds => (long)bounds.Width * bounds.Height)
                .ThenBy(bounds => bounds.Y)
                .FirstOrDefault();
            if (commandOrUpperBody is { Width: > 0, Height: > 0 })
                selected.Add(commandOrUpperBody);

            foreach (var candidate in candidates
                         .OrderByDescending(bounds => (long)bounds.Width * bounds.Height)
                         .ThenBy(bounds => bounds.Y))
            {
                if (selected.Any(existing => Overlaps(existing, candidate))) continue;
                selected.Add(candidate);
                if (selected.Count == 3) break;
            }
            return selected;
        }

        // A fully opaque provider can expose no container geometry at all.
        // Probe a command band and the remaining body instead of treating the
        // missing accessibility tree as if the window contained no interface.
        var commandHeight = Math.Min(root.Height, Math.Clamp(root.Height / 4, 72, 260));
        var commandBand = new RectI(root.X, root.Y, root.Width, commandHeight);
        if (commandHeight >= root.Height) return [commandBand];
        return [commandBand, new RectI(root.X, root.Y + commandHeight, root.Width, root.Height - commandHeight)];
    }

    internal static IReadOnlyList<RectI> ProbePoints(IReadOnlyList<RectI> regions)
    {
        var perRegion = new List<IReadOnlyList<RectI>>();
        foreach (var region in regions)
        {
            var columns = Math.Clamp(region.Width / 36, 2, 16);
            var rows = Math.Clamp(region.Height / 30, 1, 3);
            var points = new List<RectI>();
            for (var row = 0; row < rows; row++)
                for (var column = 0; column < columns; column++)
                    points.Add(new(
                        region.X + (column * 2 + 1) * region.Width / (columns * 2),
                        region.Y + (row * 2 + 1) * region.Height / (rows * 2),
                        1,
                        1));
            perRegion.Add(points);
        }

        // Share the bounded probe budget across all opaque regions. Previously
        // one wide top panel could consume all 42 probes and hide the body.
        var result = new List<RectI>();
        for (var index = 0; result.Count < MaximumHoverProbes; index++)
        {
            var added = false;
            foreach (var points in perRegion)
            {
                if (index >= points.Count) continue;
                result.Add(points[index]);
                added = true;
                if (result.Count == MaximumHoverProbes) break;
            }
            if (!added) break;
        }
        return result;
    }

    internal static RectI? DetectHoverBounds(
        PixelFrame baseline,
        PixelFrame current,
        RectI screenBounds,
        RectI point)
    {
        if (baseline.Width != current.Width || baseline.Height != current.Height ||
            baseline.Pixels.Length != current.Pixels.Length) return null;
        var localX = point.X - screenBounds.X;
        var localY = point.Y - screenBounds.Y;
        var left = Math.Max(0, localX - 96);
        var right = Math.Min(current.Width - 1, localX + 96);
        var top = Math.Max(0, localY - 64);
        var bottom = Math.Min(current.Height - 1, localY + 64);
        var minX = int.MaxValue; var minY = int.MaxValue; var maxX = -1; var maxY = -1; var count = 0;
        for (var y = top; y <= bottom; y++)
            for (var x = left; x <= right; x++)
            {
                var offset = (y * current.Width + x) * 4;
                var delta = Math.Abs(baseline.Pixels[offset] - current.Pixels[offset]) +
                            Math.Abs(baseline.Pixels[offset + 1] - current.Pixels[offset + 1]) +
                            Math.Abs(baseline.Pixels[offset + 2] - current.Pixels[offset + 2]);
                if (delta < 42) continue;
                count++;
                minX = Math.Min(minX, x); minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y);
            }
        if (count < 10 || maxX < minX || maxY < minY) return null;
        var width = maxX - minX + 1;
        var height = maxY - minY + 1;
        if (width < 6 || height < 6 || width > 193 || height > 129) return null;
        var detected = new RectI(screenBounds.X + minX, screenBounds.Y + minY, width, height);
        return DistanceTo(detected, point) <= 18 ? detected : null;
    }

    private static async Task<string> TryReadTooltipAsync(
        ManualRecordingSession session,
        WindowTarget target,
        CancellationToken cancellationToken)
    {
        await Task.Delay(320, cancellationToken).ConfigureAwait(false);
        foreach (var window in WindowCatalog.ListScopedWindows(target)
                     .Where(window => window.Hwnd != target.RootOwnerHwnd &&
                         (window.ClassName.Contains("tooltip", StringComparison.OrdinalIgnoreCase) ||
                          window.ClassName.Contains("popup", StringComparison.OrdinalIgnoreCase)))
                     .Take(2))
        {
            var title = window.Title?.Trim() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(title)) return title;
            var read = await session.CollectNativeAutomationViewAsync(
                window.Hwnd, AutomationTreeView.Raw, TimeSpan.FromMilliseconds(350), 40, cancellationToken)
                .ConfigureAwait(false);
            var name = read.Items.Select(item => item.Name).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
            if (!string.IsNullOrWhiteSpace(name)) return name;
        }
        return string.Empty;
    }

    private static AutomationObservation CreateHoverControl(WindowTarget target, RectI bounds, string name)
    {
        var material = $"{target.ProcessName}|{target.ProductVersion}|{target.ClassName}|" +
                       $"{bounds.X - target.Bounds.X}:{bounds.Y - target.Bounds.Y}:{bounds.Width}:{bounds.Height}|{name}";
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant()[..24];
        return new(
            "shadow-hover:" + id,
            "",
            "shadow:" + id,
            string.IsNullOrWhiteSpace(name) ? "Unverified control" : name,
            "ControlType.Custom",
            "UiAtlas.HoverRegion",
            bounds,
            IsEnabled: false,
            IsOffscreen: true,
            FrameworkId: "UiAtlas.Shadow.Hover",
            WindowHwnd: target.Hwnd,
            SupportedPatterns: []);
    }

    private static string PopupShape(IReadOnlyList<WindowTarget> windows, RectI rootBounds) => string.Join('|', windows
        .OrderBy(window => window.ClassName, StringComparer.Ordinal)
        .ThenBy(window => window.Bounds.Y)
        .ThenBy(window => window.Bounds.X)
        .Select(window => $"{window.ClassName}:{window.Bounds.X - rootBounds.X}:{window.Bounds.Y - rootBounds.Y}:" +
                          $"{window.Bounds.Width}:{window.Bounds.Height}"));

    internal static bool IsMaterializedHoverState(RectI changed) =>
        changed.Height >= 56 || (long)changed.Width * changed.Height >= 5_000;

    private static IReadOnlyList<AutomationObservation> Distinct(IEnumerable<AutomationObservation> controls)
    {
        var result = new List<AutomationObservation>();
        foreach (var control in controls
                     .OrderBy(item => item.ClassName is "UiAtlas.VisualControlRegion" or "UiAtlas.HoverRegion" ? 1 : 0)
                     .ThenBy(item => item.Bounds.Y)
                     .ThenBy(item => item.Bounds.X))
        {
            if (result.Any(existing => Same(existing, control))) continue;
            result.Add(control);
        }
        return result;
    }

    private static bool Same(AutomationObservation left, AutomationObservation right)
    {
        if (!string.IsNullOrWhiteSpace(left.RuntimeId) &&
            left.RuntimeId.Equals(right.RuntimeId, StringComparison.Ordinal)) return true;
        if (!string.IsNullOrWhiteSpace(left.AutomationId) &&
            left.AutomationId.Equals(right.AutomationId, StringComparison.OrdinalIgnoreCase)) return true;
        if (IsContainer(left.ControlType) || IsContainer(right.ControlType)) return false;
        var leftArea = Math.Max(1L, (long)left.Bounds.Width * left.Bounds.Height);
        var rightArea = Math.Max(1L, (long)right.Bounds.Width * right.Bounds.Height);
        var sizeRatio = Math.Min(leftArea, rightArea) / (double)Math.Max(leftArea, rightArea);
        return sizeRatio >= .38 && Overlaps(left.Bounds, right.Bounds);
    }

    private static bool Overlaps(RectI left, RectI right)
    {
        var overlapWidth = Math.Max(0, Math.Min(left.X + left.Width, right.X + right.Width) - Math.Max(left.X, right.X));
        var overlapHeight = Math.Max(0, Math.Min(left.Y + left.Height, right.Y + right.Height) - Math.Max(left.Y, right.Y));
        var intersection = (long)overlapWidth * overlapHeight;
        var smaller = Math.Max(1L, Math.Min((long)left.Width * left.Height, (long)right.Width * right.Height));
        return intersection / (double)smaller >= .58;
    }

    private static int DistanceTo(RectI bounds, RectI point)
    {
        var dx = point.X < bounds.X ? bounds.X - point.X : point.X >= bounds.X + bounds.Width ? point.X - bounds.X - bounds.Width + 1 : 0;
        var dy = point.Y < bounds.Y ? bounds.Y - point.Y : point.Y >= bounds.Y + bounds.Height ? point.Y - bounds.Y - bounds.Height + 1 : 0;
        return Math.Max(dx, dy);
    }

    private static bool Contains(RectI bounds, RectI point) =>
        point.X >= bounds.X && point.Y >= bounds.Y && point.X < bounds.X + bounds.Width && point.Y < bounds.Y + bounds.Height;

    private static bool IsContainer(string value)
    {
        var type = value.Contains('.') ? value[(value.LastIndexOf('.') + 1)..] : value;
        return type is "Window" or "Pane" or "Group" or "Custom" or "List" or "ToolBar";
    }

    private static RectI Intersect(RectI left, RectI right)
    {
        var x = Math.Max(left.X, right.X); var y = Math.Max(left.Y, right.Y);
        var r = Math.Min(left.X + left.Width, right.X + right.Width);
        var b = Math.Min(left.Y + left.Height, right.Y + right.Height);
        return new(x, y, Math.Max(0, r - x), Math.Max(0, b - y));
    }

    public sealed record PixelFrame(int Width, int Height, byte[] Pixels)
    {
        public static PixelFrame Decode(byte[] png)
        {
            using var input = new MemoryStream(png, writable: false);
            var decoder = new PngBitmapDecoder(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            BitmapSource bitmap = decoder.Frames[0];
            if (bitmap.Format != PixelFormats.Bgra32 && bitmap.Format != PixelFormats.Pbgra32)
                bitmap = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
            var stride = checked(bitmap.PixelWidth * 4);
            var pixels = new byte[checked(stride * bitmap.PixelHeight)];
            bitmap.CopyPixels(pixels, stride, 0);
            return new(bitmap.PixelWidth, bitmap.PixelHeight, pixels);
        }
    }
}
