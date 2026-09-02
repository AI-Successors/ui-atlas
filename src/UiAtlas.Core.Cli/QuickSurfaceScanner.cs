using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Recording;
using UiAtlas.Core.Recording.Windows;
using UiAtlas.Core.Storage;

namespace UiAtlas.Core.Cli;

internal sealed record QuickSurfaceScanResult(
    FrameObservation Frame,
    QuickMapCaptureStatus Status,
    string SurfaceFingerprint,
    int VisibleControlCount,
    int UnverifiedControlCount,
    IReadOnlyList<string> DiagnosticCodes,
    int ConfirmedControlCount = 0,
    int ObservedControlCount = 0,
    int CoverageGapCount = 0,
    ExtractionCoverageStatus? ExtractionStatus = null)
{
    public bool HasUsableControls => VisibleControlCount + UnverifiedControlCount > 0;
}

internal static class QuickSurfaceScanner
{
    internal static readonly TimeSpan CaptureBudget = TimeSpan.FromSeconds(8);
    internal const int MaximumControlCount = 2_500;
    public static async Task<QuickSurfaceScanResult> CaptureAsync(
        ManualRecordingSession session,
        string trigger,
        CancellationToken cancellationToken,
        Action<string>? status = null,
        bool enableHoverAndFocusDiscovery = true)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (string.IsNullOrWhiteSpace(trigger)) throw new ArgumentException("A trigger is required.", nameof(trigger));

        var timer = Stopwatch.StartNew();
        // A number of legacy Win32/Delphi applications keep a hidden zero-sized
        // application window as GA_ROOTOWNER. Scan the visible window selected by
        // the user; the root-owner handle remains available for scope validation.
        var target = WindowCatalog.Resolve(session.TargetHwnd);
        // Persist visual and Win32 evidence before asking an application UIA
        // provider anything. A blocked WPF provider must never make recording
        // look as though it did not start.
        var baseFrame = await session.CaptureAsync(
            "quick-map-screen:" + trigger,
            cancellationToken,
            new FrameCaptureOptions(
                IncludeAutomation: false,
                CapturePhase: "materialized",
                ObservationScope: "full-root",
                ScreenshotTimeout: TimeSpan.FromMilliseconds(500),
                PreferScreenBoundsScreenshot: true)).ConfigureAwait(false);
        status?.Invoke("Stage 2 of 5: scanning visible controls and tables. Complex applications can take several minutes.");
        var catalog = new LocalArtifactCatalog();
        var cacheStore = new ApplicationSurfaceCacheStore(catalog.Root);
        var cacheKey = SurfaceCacheKey(target);
        IReadOnlyList<AutomationObservation> cachedControls;
        try
        {
            var cache = cacheStore.Load(cacheKey);
            if (cache.Controls.Count < 24)
                cache = BootstrapCacheFromRecentRecordings(cacheStore, catalog, cacheKey, cache);
            cachedControls = cacheStore.Project(cache, target.Bounds, target.RootOwnerHwnd);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            cachedControls = [];
        }
        var raw = IsRibbonTarget(target)
            ? await CollectRibbonApplicationAsync(session, target, cachedControls.Count, cancellationToken).ConfigureAwait(false)
            : await session.CollectWindowAutomationAsync(
                target.Hwnd,
                TimeSpan.FromMilliseconds(3_200),
                MaximumControlCount,
                cancellationToken).ConfigureAwait(false);
        var cascadeBudget = CaptureBudget - TimeSpan.FromMilliseconds(650) - timer.Elapsed;
        if (cascadeBudget < TimeSpan.Zero) cascadeBudget = TimeSpan.Zero;
        var cascade = await AdaptiveExtractionCascade.EnrichAsync(
            session, target, raw.Items, raw.TimedOut, raw.Status, cascadeBudget,
            MaximumControlCount, cancellationToken, cachedControls).ConfigureAwait(false);
        var shadowBudget = CaptureBudget - TimeSpan.FromMilliseconds(650) - timer.Elapsed;
        if (NeedsOpaqueSurfaceScan(cascade.Snapshot) && shadowBudget >= TimeSpan.FromMilliseconds(350))
        {
            status?.Invoke("Recording is active. Windows exposed only a control group; mapping it safely without clicks...");
            var shadow = await OpaqueSurfaceScanner.ScanAsync(
                session, target, cascade.Snapshot.Gaps, shadowBudget, cancellationToken,
                cascade.Controls, enableHoverAndFocusDiscovery,
                allowOcrFallback: true).ConfigureAwait(false);
            cascade = MergeShadowEvidence(cascade, target, shadow);
            foreach (var diagnostic in shadow.DiagnosticCodes)
                session.AddCaptureHealth("opaque-surface", diagnostic,
                    $"Hover probes: {shadow.HoverProbeCount}; focus probes: {shadow.FocusProbeCount}; " +
                    $"hover states: {shadow.HoverStateCount}.");
            if (shadow.InterruptedByUser)
                status?.Invoke("Background mapping stopped because you started interacting. Manual recording is active.");
        }
        var combinedControls = MergeCachedControls(cascade.Controls, cachedControls, MaximumControlCount);
        status?.Invoke($"Stage 3 of 5: verifying {combinedControls.Count} discovered controls and attaching them to this screen.");
        var frame = await session.CaptureAutomationDeltaAsync(
            "quick-map:" + trigger,
            combinedControls,
            cancellationToken,
            baseFrame.Sequence,
            target.Hwnd,
            cascade.TimedOut,
            cascade.Status,
            cascade.Snapshot).ConfigureAwait(false);

        try
        {
            _ = cacheStore.Observe(cacheKey, target.Bounds, cascade.Controls, DateTimeOffset.UtcNow);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            // The scan is immutable evidence even when the optional accelerator
            // cache cannot be updated. Never fail recording for a cache problem.
        }

        return Describe(frame);
    }

    internal static IReadOnlyList<AutomationObservation> MergeCachedControls(
        IReadOnlyList<AutomationObservation> observed,
        IReadOnlyList<AutomationObservation> cached,
        int maximum)
    {
        ArgumentNullException.ThrowIfNull(observed);
        ArgumentNullException.ThrowIfNull(cached);
        if (maximum < 1) throw new ArgumentOutOfRangeException(nameof(maximum));
        var result = observed.Take(maximum).ToList();
        if (result.Count == 0) return result;
        var nativeTreeAvailable = VisualFallbackPolicy.HasUsableNativeTree(observed);
        foreach (var candidate in cached)
        {
            if (result.Count >= maximum) break;
            if (nativeTreeAvailable && candidate.ClassName.Equals(
                    "UiAtlas.VisualControlRegion", StringComparison.OrdinalIgnoreCase)) continue;
            if (result.Any(control => SameStableControl(control, candidate))) continue;
            result.Add(candidate with { IsEnabled = false, IsOffscreen = true, FrameworkId = "UiAtlas.Cached" });
        }
        return result;
    }

    internal static bool HasOpaqueProvider(AdaptiveExtractionSnapshot snapshot) =>
        snapshot.Sources.Any(source => source.Status == "provider-opaque");

    internal static bool NeedsOpaqueSurfaceScan(AdaptiveExtractionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (HasOpaqueProvider(snapshot) || snapshot.CoverageStatus == ExtractionCoverageStatus.Unavailable)
            return true;

        var visualGapExists = snapshot.Gaps.Any(gap => gap.Kind is
            CoverageGapKind.EmptyContainer or CoverageGapKind.LargeContainer or
            CoverageGapKind.ViewDivergence or CoverageGapKind.EmptyBounds);
        if (!visualGapExists) return false;

        // Partially accessible applications such as Revit can expose a healthy
        // ribbon tree while leaving individual panels or custom-drawn buttons
        // opaque. Scan the unresolved regions even when other controls exist.
        return snapshot.CoverageStatus is ExtractionCoverageStatus.Partial or
            ExtractionCoverageStatus.LimitReached or ExtractionCoverageStatus.Unavailable;
    }

    internal static AdaptiveExtractionResult MergeShadowEvidence(
        AdaptiveExtractionResult cascade,
        WindowTarget target,
        OpaqueSurfaceScanResult shadow)
    {
        ArgumentNullException.ThrowIfNull(cascade);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(shadow);
        if (shadow.Controls.Count == 0) return cascade;

        var surfaceId = AdaptiveExtractionCascade.SurfaceId(target, target.Bounds);
        var sourceGroups = shadow.Controls.GroupBy(control =>
            control.HasKeyboardFocus
                ? ControlEvidenceSource.Focus
                : control.ClassName == "UiAtlas.VisualControlRegion"
                    ? ControlEvidenceSource.Visual
                : ControlEvidenceSource.UiaFromPoint);
        var additions = sourceGroups.Select(group => new ExtractionSourceResult(
            group.Key,
            surfaceId,
            group.Select((control, index) => new ControlEvidenceObservation(
                "evidence-" + AdaptiveExtractionCascade.Hash(
                    $"shadow|{surfaceId}|{control.RuntimeId}|{control.Bounds}|{index}", 24),
                group.Key,
                surfaceId,
                control,
                group.Key == ControlEvidenceSource.Focus ? .86 :
                    group.Key == ControlEvidenceSource.Visual ? .52 : .62,
                group.Key == ControlEvidenceSource.Focus ? "shadow-focus-observed" :
                    group.Key == ControlEvidenceSource.Visual ? "visual-rectangle-unverified" : "shadow-hover-unverified"))
                .ToArray(),
            group.Key == ControlEvidenceSource.Focus ? "shadow-focus" :
                group.Key == ControlEvidenceSource.Visual ? "visual-rectangle" : "shadow-hover",
            0)).ToArray();
        var sources = cascade.Snapshot.Sources.Concat(additions).ToArray();
        var candidates = ControlEvidenceMerger.Merge(sources);
        var snapshot = cascade.Snapshot with
        {
            Sources = sources,
            Candidates = candidates,
            ProbeCount = cascade.Snapshot.ProbeCount + shadow.HoverProbeCount + shadow.FocusProbeCount,
            StopReason = shadow.InterruptedByUser ? "user-input" :
                shadow.TimedOut ? "time-limit" : cascade.Snapshot.StopReason
        };
        return new(
            candidates.Select(candidate => candidate.Control).Take(MaximumControlCount).ToArray(),
            snapshot,
            cascade.TimedOut || shadow.TimedOut,
            shadow.TimedOut ? "partial" : cascade.Status);
    }

    private static bool SameStableControl(AutomationObservation left, AutomationObservation right)
    {
        var leftType = NormalizeType(left.ControlType);
        var rightType = NormalizeType(right.ControlType);
        if (!leftType.Equals(rightType, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.IsNullOrWhiteSpace(left.AutomationId) &&
            left.AutomationId.Equals(right.AutomationId, StringComparison.OrdinalIgnoreCase) &&
            left.ClassName.Equals(right.ClassName, StringComparison.OrdinalIgnoreCase))
            return true;
        return !string.IsNullOrWhiteSpace(left.Name) &&
               left.Name.Equals(right.Name, StringComparison.OrdinalIgnoreCase) &&
               left.ClassName.Equals(right.ClassName, StringComparison.OrdinalIgnoreCase);
    }

    private static ApplicationPlanningProfileKey SurfaceCacheKey(WindowTarget target) => new(
        target.ProcessName,
        target.ProductName ?? string.Empty,
        MajorVersion(target.ProductVersion),
        target.ClassName ?? string.Empty);

    private static string MajorVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return string.Empty;
        var separator = version.IndexOf('.');
        return separator > 0 ? version[..separator] : version;
    }

    private static string NormalizeType(string value) =>
        value.Contains('.') ? value[(value.LastIndexOf('.') + 1)..] : value;

    private static ApplicationSurfaceCache BootstrapCacheFromRecentRecordings(
        ApplicationSurfaceCacheStore store,
        LocalArtifactCatalog catalog,
        ApplicationPlanningProfileKey key,
        ApplicationSurfaceCache current)
    {
        FrameObservation? best = null;
        var bestVisibleCount = 0;
        try
        {
            foreach (var file in new DirectoryInfo(catalog.RecordingsDirectory)
                         .EnumerateFiles("*.mlrec", SearchOption.TopDirectoryOnly)
                         .OrderByDescending(file => file.LastWriteTimeUtc)
                         .Take(12))
            {
                try
                {
                    using var bundle = RecordingBundle.Open(file.FullName);
                    var manifest = bundle.ReadJson<RecordingManifest>("manifest.json");
                    if (!MatchesApplication(manifest.Target, key)) continue;

                    foreach (var entry in bundle.Entries
                                 .Where(entry => entry.StartsWith("raw/observations/frame-", StringComparison.Ordinal) &&
                                                 entry.EndsWith(".json", StringComparison.Ordinal))
                                 .OrderByDescending(entry => entry, StringComparer.Ordinal)
                                 .Take(24))
                    {
                        var frame = bundle.ReadJson<FrameObservation>(entry);
                        if (!frame.Window.ClassName.Equals(key.WindowClass, StringComparison.OrdinalIgnoreCase))
                            continue;
                        var visibleCount = AutomationObservationVisibility.FilterEffectivelyVisible(frame.Automation).Count;
                        if (visibleCount <= bestVisibleCount) continue;
                        best = frame;
                        bestVisibleCount = visibleCount;
                    }
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or
                                           UnauthorizedAccessException or System.Text.Json.JsonException)
                {
                    // Historical evidence is only an accelerator. Ignore a stale
                    // or incomplete bundle and continue with the live scan.
                }

                if (bestVisibleCount >= 400) break;
            }

            return best is null
                ? current
                : store.Observe(key, best.Window.Bounds, best.Automation, DateTimeOffset.UtcNow);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return current;
        }
    }

    internal static bool MatchesApplication(TargetScope target, ApplicationPlanningProfileKey key)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(key);
        return target.ProcessName.Equals(key.ProcessName, StringComparison.OrdinalIgnoreCase) &&
               (string.IsNullOrWhiteSpace(target.ProductName) || string.IsNullOrWhiteSpace(key.ProductName) ||
                target.ProductName.Equals(key.ProductName, StringComparison.OrdinalIgnoreCase)) &&
               (string.IsNullOrWhiteSpace(key.MajorVersion) ||
                MajorVersion(target.ProductVersion).Equals(key.MajorVersion, StringComparison.OrdinalIgnoreCase));
    }

    internal static QuickSurfaceScanResult Describe(FrameObservation frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var visible = AutomationObservationVisibility.FilterEffectivelyVisible(frame.Automation).ToHashSet();
        var diagnostics = new List<string>();
        if (frame.AutomationTimedOut) diagnostics.Add("uia-timeout");
        if (!string.Equals(frame.AutomationStatus, "ok", StringComparison.Ordinal))
            diagnostics.Add("uia-" + frame.AutomationStatus);
        if (frame.Automation.Count == 0) diagnostics.Add("no-controls");
        if (frame.Extraction is { } extractionDetails)
        {
            diagnostics.AddRange(extractionDetails.Gaps.Select(gap => "coverage-" + gap.Kind.ToString().ToLowerInvariant()));
            diagnostics.AddRange(extractionDetails.Sources
                .Where(source => source.Status.StartsWith("provider-", StringComparison.Ordinal))
                .Select(source => source.Status));
            if (extractionDetails.CoverageStatus is ExtractionCoverageStatus.Partial or ExtractionCoverageStatus.LimitReached)
                diagnostics.Add("adaptive-" + extractionDetails.CoverageStatus.ToString().ToLowerInvariant());
        }

        var material = string.Join('\n', frame.Automation
            .Select(control => AutoMappingTargetFingerprint.Create(control, frame.Window.Bounds))
            .Order(StringComparer.Ordinal));
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)))
            .ToLowerInvariant()[..32];
        var partial = frame.AutomationTimedOut || frame.AutomationStatus != "ok";
        var extraction = frame.Extraction;
        return new(
            frame,
            partial ? QuickMapCaptureStatus.Partial : QuickMapCaptureStatus.Complete,
            fingerprint,
            visible.Count,
            Math.Max(0, frame.Automation.Count - visible.Count),
            diagnostics.Distinct(StringComparer.Ordinal).ToArray(),
            extraction?.Candidates.Count(candidate => candidate.CoverageStatus == ExtractionCoverageStatus.Confirmed) ?? 0,
            extraction?.Candidates.Count(candidate => candidate.CoverageStatus == ExtractionCoverageStatus.Observed) ?? 0,
            extraction?.Gaps.Count ?? 0,
            extraction?.CoverageStatus);
    }

    private static async Task<(IReadOnlyList<AutomationObservation> Items, bool TimedOut, string Status)> CollectRibbonApplicationAsync(
        ManualRecordingSession session,
        WindowTarget target,
        int cachedControlCount,
        CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        var isRevit = IsRevitTarget(target);
        var isExcel = IsExcelTarget(target);
        (IReadOnlyList<AutomationObservation> Items, bool TimedOut, string Status) nativeBand =
            ([], false, "ok");
        (IReadOnlyList<AutomationObservation> Items, bool TimedOut, string Status) nativePeripheral =
            ([], false, "ok");
        (IReadOnlyList<AutomationObservation> Items, bool TimedOut, string Status) revitBrowser =
            ([], false, "ok");
        (IReadOnlyList<AutomationObservation> Items, bool TimedOut, string Status) worksheet =
            ([], false, "ok");

        if (isExcel)
        {
            // Excel's visible worksheet is a separate EXCEL7 provider island. A
            // generic application or Ribbon walk can exhaust its visit budget
            // before it ever reaches the grid, which drops column headers, row
            // headers and cells even though the dedicated provider exposes them.
            // Capture that bounded island first so command density can never
            // evict the worksheet from the initial map.
            worksheet = await session.CollectWorksheetAutomationAsync(
                session.TargetRootOwnerHwnd,
                TimeSpan.FromMilliseconds(3_000),
                2_000,
                cancellationToken).ConfigureAwait(false);
        }

        if (isRevit)
        {
            // Revit's full managed WPF walk may consume the whole public budget.
            // Sample the navigation strip through the native UIA3 client first:
            // ElementFromPoint reaches WPF Buttons even when the exported child
            // collection is truncated or the managed UIA bridge regresses.
            var bandWidth = Math.Min(target.Bounds.Width, 600);
            // Revit's main-tab row is centered about 51 physical pixels below
            // the outer window top at 100% scaling. A narrow adaptive strip is
            // much faster than sampling the entire Ribbon and still tolerates
            // border/DPI variation.
            var bandTop = target.Bounds.Y + Math.Clamp(target.Bounds.Height / 20, 38, 64);
            const int bandHeight = 18;
            nativeBand = await session.CollectNativeBandAutomationAsync(
                session.TargetRootOwnerHwnd,
                new RectI(target.Bounds.X, bandTop, bandWidth, bandHeight),
                24,
                32,
                TimeSpan.FromMilliseconds(3_200),
                700,
                cancellationToken).ConfigureAwait(false);

            // The Project Browser and native child surfaces are cheap and useful;
            // collect them before any provider-wide traversal can consume the
            // budget. This preserves the lower/left controls in partial scans.
            revitBrowser = await session.CollectRevitBrowserAutomationAsync(
                session.TargetRootOwnerHwnd,
                TimeSpan.FromMilliseconds(1_900),
                600,
                cancellationToken).ConfigureAwait(false);
            nativePeripheral = await session.CollectNativePeripheralAutomationAsync(
                session.TargetRootOwnerHwnd,
                TimeSpan.FromMilliseconds(450),
                800,
                cancellationToken).ConfigureAwait(false);
        }

        var nativeTabsFound = AutoTabDiscovery.Discover(new FrameObservation(
            0,
            DateTimeOffset.UtcNow,
            string.Empty,
            WindowSnapshotCapture.Observe(target),
            nativeBand.Items,
            nativeBand.TimedOut,
            nativeBand.Status,
            "native-band")).Count;
        (IReadOnlyList<AutomationObservation> Items, bool TimedOut, string Status) navigation =
            ([], false, "ok");
        if (!isRevit || nativeTabsFound < 3)
        {
            navigation = await session.CollectNavigationAutomationAsync(
                session.TargetRootOwnerHwnd,
                isRevit ? TimeSpan.FromMilliseconds(1_100) : TimeSpan.FromMilliseconds(1_850),
                400,
                cancellationToken).ConfigureAwait(false);
        }

        // Leave two seconds for terminating an unresponsive isolated provider and
        // half a second for the screenshot. The remaining time is the Ribbon walk.
        var ribbonCeiling = isExcel
            ? TimeSpan.FromMilliseconds(6_800)
            : cachedControlCount >= 24
            ? TimeSpan.FromMilliseconds(1_100)
            : isRevit ? TimeSpan.FromMilliseconds(4_900) : TimeSpan.FromMilliseconds(3_200);
        var ribbonBudget = ribbonCeiling - timer.Elapsed;
        (IReadOnlyList<AutomationObservation> Items, bool TimedOut, string Status) ribbon =
            ([], true, "timeout");
        if (ribbonBudget > TimeSpan.FromMilliseconds(250))
        {
            ribbon = await session.CollectRibbonAutomationAsync(
                session.TargetRootOwnerHwnd,
                ribbonBudget,
                MaximumControlCount - 400,
                cancellationToken).ConfigureAwait(false);
        }

        var controls = worksheet.Items.Concat(nativeBand.Items).Concat(navigation.Items).Concat(ribbon.Items)
            .Concat(nativePeripheral.Items).Concat(revitBrowser.Items)
            .GroupBy(control => string.IsNullOrWhiteSpace(control.RuntimeId)
                ? $"{control.WindowHwnd}:{control.AutomationId}:{control.ControlType}:{control.Bounds}"
                : $"{control.WindowHwnd}:{control.RuntimeId}", StringComparer.Ordinal)
            .Select(group => group.First())
            .Take(MaximumControlCount)
            .ToArray();
        var timedOut = worksheet.TimedOut || nativeBand.TimedOut || navigation.TimedOut || ribbon.TimedOut ||
                       nativePeripheral.TimedOut || revitBrowser.TimedOut;
        var statuses = new[]
        {
            worksheet.Status, nativeBand.Status, navigation.Status, ribbon.Status,
            nativePeripheral.Status, revitBrowser.Status
        };
        var status = statuses.All(value => value == "ok")
            ? "ok"
            : statuses.Any(value => value == "node-limit")
                ? "node-limit"
                : controls.Length > 0 ? "partial" : statuses.First(value => value != "ok");

        return (controls, timedOut, status);
    }

    internal static bool IsRevitTarget(WindowTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var identity = $"{target.ProcessName} {target.OriginalFilename} {target.ProductName}";
        return identity.Contains("Revit", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsExcelTarget(WindowTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var identity = $"{target.ProcessName} {target.OriginalFilename} {target.ProductName} {target.ClassName}";
        return identity.Contains("EXCEL", StringComparison.OrdinalIgnoreCase) ||
               target.ClassName.Equals("XLMAIN", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsRibbonTarget(WindowTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var identity = $"{target.ProcessName} {target.OriginalFilename} {target.ProductName}";
        return new[] { "Revit", "EXCEL", "WINWORD", "POWERPNT", "OUTLOOK" }
            .Any(value => identity.Contains(value, StringComparison.OrdinalIgnoreCase));
    }
}
