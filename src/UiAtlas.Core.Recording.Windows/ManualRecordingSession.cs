using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Recording;

namespace UiAtlas.Core.Recording.Windows;

[SupportedOSPlatform("windows")]
public sealed class ManualRecordingSession : IAsyncDisposable
{
    private readonly WindowTarget _target;
    private readonly string _outputPath;
    private readonly RecordingBundleWriter _writer;
    private readonly LowLevelInputMonitor _input;
    private readonly ManualTargetInputWaiter _manualInput;
    private readonly Func<CancellationToken, Task>? _beforeScreenshotCapture;
    private readonly Action? _afterScreenshotCapture;
    private readonly UiaWorkerClient _automation = new();
    private readonly SemaphoreSlim _captureGate = new(1, 1);
    // Office exposes one UIA provider for the whole window. Concurrent tree walks
    // do not make it faster: Excel serializes them on its UI thread and the
    // competing worker deadlines can then expire without either returning a
    // partial tree. Keep every provider read in this recording transactionally
    // ordered. Successful reads still return immediately.
    private readonly SemaphoreSlim _automationGate = new(1, 1);
    private readonly object _stateGate = new();
    private readonly List<InputEvent> _events = [];
    private readonly List<FrameSummary> _frames = [];
    private readonly List<FrameObservation> _frameObservations = [];
    private readonly List<CaptureHealthEvent> _health = [];
    private readonly List<ManualEpisodeSummary> _episodes = [];
    private readonly List<InteractionObservation> _interactions = [];
    private readonly DateTimeOffset _started = DateTimeOffset.UtcNow;
    private long _frameSequence;
    private long _latestScreenshotSequence;
    private byte[]? _latestScreenshotPng;
    private bool _startedCapture;
    private bool _finalized;
    private bool _hasPartialCapture;
    private long _reportedDroppedEvents;
    private int _inputCapturePaused;
    private nint _controllerWindow;
    private const int MaxEvents = RecordingContractLimits.MaxEvents;
    private const int MaxMarkerTextLength = 256;
    private static readonly TimeSpan MaxDuration = TimeSpan.FromHours(4);
    private static readonly TimeSpan RichAutomationTimeout = TimeSpan.FromSeconds(35);
    private static readonly TimeSpan DeferredVisualSampleInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan DeferredVisualMinimumObservation = TimeSpan.FromMilliseconds(1_250);
    private static readonly TimeSpan DeferredVisualQuietWindow = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan DeferredVisualMaximumObservation = TimeSpan.FromSeconds(5);

    public ManualRecordingSession(
        WindowTarget target,
        string outputPath,
        Func<CancellationToken, Task>? beforeScreenshotCapture = null,
        Action? afterScreenshotCapture = null)
    {
        RecordingBundleWriter.CleanupAbandonedStaging(TimeSpan.FromDays(1));
        _target = target;
        _outputPath = Path.GetFullPath(outputPath);
        RecordingBundleWriter.CleanupOutputTemporaries(_outputPath, TimeSpan.FromDays(1));
        var staging = Path.Combine(Path.GetTempPath(), "ui-atlas-recording-" + Guid.NewGuid().ToString("N"));
        _writer = new RecordingBundleWriter(staging);
        _input = new LowLevelInputMonitor(target.RootOwnerHwnd, target.ProcessId, target.ProcessStartedUtc);
        _manualInput = new ManualTargetInputWaiter(target);
        _beforeScreenshotCapture = beforeScreenshotCapture;
        _afterScreenshotCapture = afterScreenshotCapture;
    }

    public void Start(bool explicitConsent)
    {
        if (!explicitConsent) throw new InvalidOperationException("Explicit recording consent is required.");
        RevalidateTarget();
        _startedCapture = true;
        try
        {
            _input.Start();
            _health.Add(new(DateTimeOffset.UtcNow, "session", "recording-visible", "Manual recording started with explicit consent.", true));
        }
        catch
        {
            _health.Add(new(DateTimeOffset.UtcNow, "input", "startup-failed", "Input monitoring could not start.", false));
            Finalize(RecordingOutcome.Failed, retainOnCancel: true);
            throw;
        }
    }

    public async Task<FrameObservation> CaptureAsync(
        string trigger,
        CancellationToken cancellationToken,
        FrameCaptureOptions? options = null)
    {
        options ??= new FrameCaptureOptions();
        if (!_startedCapture || _finalized) throw new InvalidOperationException("Recording is not active.");
        if (DateTimeOffset.UtcNow - _started > MaxDuration)
            throw new InvalidOperationException("Recording quota reached; finalize or cancel the session.");
        await _captureGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ObserveFrameAsync(
                trigger,
                cancellationToken,
                options,
                persistFrame: true,
                captureScreenshot: true,
                options.AutomationTimeout ?? RichAutomationTimeout).ConfigureAwait(false);
        }
        finally { _captureGate.Release(); }
    }

    public async Task<FrameObservation> ObserveCurrentUiAsync(TimeSpan automationTimeout, CancellationToken cancellationToken)
    {
        if (automationTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(automationTimeout));
        if (!_startedCapture || _finalized) throw new InvalidOperationException("Recording is not active.");
        if (DateTimeOffset.UtcNow - _started > MaxDuration)
            throw new InvalidOperationException("Recording quota reached; finalize or cancel the session.");
        await _captureGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ObserveFrameAsync(
                "auto-refresh-probe",
                cancellationToken,
                new FrameCaptureOptions(),
                persistFrame: false,
                captureScreenshot: false,
                automationTimeout).ConfigureAwait(false);
        }
        finally { _captureGate.Release(); }
    }

    public Task<FrameObservation> CaptureAutoRefreshFrameAsync(string trigger, CancellationToken cancellationToken)
    {
        if (DateTimeOffset.UtcNow - _started > MaxDuration)
            throw new InvalidOperationException("Recording quota reached; finalize or cancel the session.");
        return CaptureAsync(trigger, cancellationToken);
    }

    // Kept for callers that reserve room before an automatic step. Recordings no
    // longer have a frame-count ceiling; storage quotas remain the safety bound.
    public int RemainingFrameBudget => int.MaxValue;
    public long LatestFrameSequence => Interlocked.Read(ref _frameSequence);
    public long TargetHwnd => _target.Hwnd;
    public long TargetRootOwnerHwnd => _target.RootOwnerHwnd;
    public bool IsInputCapturePaused => Volatile.Read(ref _inputCapturePaused) != 0;

    public InteractionCaptureContext CreateInteractionContext(
        string operationId,
        int attempt,
        InteractionActor actor,
        InteractionGestureKind gesture,
        InteractionActionKind action,
        long sourceFrameSequence,
        AutomationObservation? sourceControl = null,
        DateTimeOffset? startedUtc = null)
    {
        if (!_startedCapture || _finalized) throw new InvalidOperationException("Recording is not active.");
        if (string.IsNullOrWhiteSpace(operationId)) throw new ArgumentException("Operation ID is required.", nameof(operationId));
        if (attempt < 1) throw new ArgumentOutOfRangeException(nameof(attempt));
        if (sourceFrameSequence < 1 || sourceFrameSequence > LatestFrameSequence)
            throw new ArgumentOutOfRangeException(nameof(sourceFrameSequence));
        return new("interaction-" + Guid.NewGuid().ToString("N"), operationId, attempt, actor,
            gesture, action, sourceFrameSequence, sourceControl, startedUtc ?? DateTimeOffset.UtcNow);
    }

    public void CompleteInteraction(
        InteractionCaptureContext context,
        InteractionOutcome outcome,
        IReadOnlyList<long>? resultFrameSequences = null,
        string diagnosticCode = "",
        AutomationObservation? sourceControl = null,
        IReadOnlyList<long>? inputSequences = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!_startedCapture || _finalized) throw new InvalidOperationException("Recording is not active.");
        var results = (resultFrameSequences ?? [])
            .Where(sequence => sequence > 0 && sequence <= LatestFrameSequence)
            .Distinct().Order().ToArray();
        lock (_stateGate)
        {
            if (_interactions.Count >= RecordingContractLimits.MaxInteractions)
            {
                _hasPartialCapture = true;
                _health.Add(new(DateTimeOffset.UtcNow, "interaction", "limit",
                    "Interaction trace reached its bounded record limit.", true));
                return;
            }
            _interactions.Add(new(
                context.InteractionId,
                context.OperationId,
                context.Attempt,
                _interactions.Count + 1L,
                context.Actor,
                context.Gesture,
                context.Action,
                context.SourceFrameSequence,
                sourceControl ?? context.SourceControl,
                (inputSequences ?? []).Distinct().Order().ToArray(),
                results,
                context.StartedUtc,
                DateTimeOffset.UtcNow,
                outcome,
                NormalizeMarkerText(diagnosticCode ?? string.Empty)));
        }
    }

    public void AddMarker(string marker)
    {
        marker = NormalizeMarkerText(marker);
        lock (_stateGate)
        {
            DrainInputUnsafe();
            _events.Add(new(_events.Count == 0 ? 1 : _events.Max(x => x.Sequence) + 1, DateTimeOffset.UtcNow,
                InputEventKind.Marker, 0, 0, 0, marker, 0, _target.RootOwnerHwnd, NativeMethods.GetForegroundWindow().ToInt64()));
        }
    }

    internal static string NormalizeMarkerText(string marker)
    {
        ArgumentNullException.ThrowIfNull(marker);
        if (marker.Length <= MaxMarkerTextLength)
            return marker;

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(marker)))
            .ToLowerInvariant()[..16];
        var prefixLength = MaxMarkerTextLength - hash.Length - 2;
        return marker[..prefixLength] + "~#" + hash;
    }

    public void AddCaptureHealth(string component, string status, string detail, bool recoverable = true)
    {
        if (!_startedCapture || _finalized) return;
        lock (_stateGate)
        {
            _health.Add(new(DateTimeOffset.UtcNow, component, status, detail, recoverable));
            if (!recoverable) _hasPartialCapture = true;
        }
    }

    public bool TryGetCursorPosition(out RectI point)
    {
        if (NativeMethods.GetCursorPos(out var nativePoint))
        {
            point = new(nativePoint.X, nativePoint.Y, 1, 1);
            return true;
        }
        point = new RectI(0, 0, 1, 1);
        return false;
    }

    public bool TryGetLastTargetClickPoint(out RectI point)
    {
        lock (_stateGate)
        {
            var pointerUp = _events.LastOrDefault(item => item.Kind == InputEventKind.PointerUp);
            if (pointerUp is not null)
            {
                point = new RectI(pointerUp.X, pointerUp.Y, 1, 1);
                return true;
            }
        }
        return TryGetCursorPosition(out point);
    }

    public async Task<(IReadOnlyList<AutomationObservation> Items, bool TimedOut, string Status)> CollectWindowAutomationAsync(
        long hwnd,
        TimeSpan timeout,
        int maxNodes,
        CancellationToken cancellationToken)
    {
        if (!_startedCapture || _finalized) throw new InvalidOperationException("Recording is not active.");
        RevalidateTarget();
        if (!WindowCatalog.IsSameProcessWindow(_target, hwnd))
            throw new InvalidOperationException("Automation window is outside the sealed target scope.");
        return await CollectAutomationExclusiveAsync(
            () => _automation.CollectAsync(_target, timeout, maxNodes, cancellationToken, hwnd),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<(IReadOnlyList<AutomationObservation> Items, bool TimedOut, string Status)> CollectPopupAutomationAsync(
        long hwnd,
        TimeSpan timeout,
        int maxNodes,
        CancellationToken cancellationToken)
    {
        if (!_startedCapture || _finalized) throw new InvalidOperationException("Recording is not active.");
        RevalidateTarget();
        if (!WindowCatalog.IsSameProcessWindow(_target, hwnd))
            throw new InvalidOperationException("Popup automation window is outside the sealed target process.");
        return await CollectAutomationExclusiveAsync(
            () => _automation.CollectPopupAsync(_target, hwnd, timeout, maxNodes, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<(IReadOnlyList<AutomationObservation> Items, bool TimedOut, string Status)> CollectNavigationAutomationAsync(
        long hwnd,
        TimeSpan timeout,
        int maxNodes,
        CancellationToken cancellationToken)
    {
        if (!_startedCapture || _finalized) throw new InvalidOperationException("Recording is not active.");
        RevalidateTarget();
        if (WindowCatalog.GetRootOwnerHandle((nint)hwnd).ToInt64() != _target.RootOwnerHwnd)
            throw new InvalidOperationException("Automation window is outside the sealed target scope.");
        return await CollectAutomationExclusiveAsync(
            () => _automation.CollectNavigationAsync(_target, timeout, maxNodes, cancellationToken, hwnd),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<(IReadOnlyList<AutomationObservation> Items, bool TimedOut, string Status)> CollectNativePeripheralAutomationAsync(
        long hwnd,
        TimeSpan timeout,
        int maxNodes,
        CancellationToken cancellationToken)
    {
        if (!_startedCapture || _finalized) throw new InvalidOperationException("Recording is not active.");
        RevalidateTarget();
        if (WindowCatalog.GetRootOwnerHandle((nint)hwnd).ToInt64() != _target.RootOwnerHwnd)
            throw new InvalidOperationException("Automation window is outside the sealed target scope.");
        return await CollectAutomationExclusiveAsync(
            () => _automation.CollectNativePeripheralAsync(_target, timeout, maxNodes, cancellationToken, hwnd),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<(IReadOnlyList<AutomationObservation> Items, bool TimedOut, string Status)> CollectRevitBrowserAutomationAsync(
        long hwnd,
        TimeSpan timeout,
        int maxNodes,
        CancellationToken cancellationToken)
    {
        if (!_startedCapture || _finalized) throw new InvalidOperationException("Recording is not active.");
        RevalidateTarget();
        if (WindowCatalog.GetRootOwnerHandle((nint)hwnd).ToInt64() != _target.RootOwnerHwnd)
            throw new InvalidOperationException("Automation window is outside the sealed target scope.");
        return await CollectAutomationExclusiveAsync(
            () => _automation.CollectRevitBrowserAsync(_target, timeout, maxNodes, cancellationToken, hwnd),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<(IReadOnlyList<AutomationObservation> Items, bool TimedOut, string Status)> CollectAdobeDisclosureAutomationAsync(
        TimeSpan timeout,
        int maxNodes,
        CancellationToken cancellationToken)
    {
        if (!_startedCapture || _finalized) throw new InvalidOperationException("Recording is not active.");
        RevalidateTarget();
        return await CollectAutomationExclusiveAsync(
            () => _automation.CollectAdobeDisclosuresAsync(_target, timeout, maxNodes, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<(IReadOnlyList<AutomationObservation> Items, bool TimedOut, string Status)> CollectPointAutomationAsync(
        RectI point, TimeSpan timeout, int maxNodes, CancellationToken cancellationToken, long? scopeHwnd = null)
    {
        if (!_startedCapture || _finalized) throw new InvalidOperationException("Recording is not active.");
        RevalidateTarget();
        if (scopeHwnd is { } scoped && !WindowCatalog.IsSameProcessWindow(_target, scoped))
            throw new InvalidOperationException("Point automation window is outside the sealed target process.");
        return await CollectAutomationExclusiveAsync(
            () => _automation.CollectPointAsync(_target, point, timeout, maxNodes, cancellationToken, scopeHwnd),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<(IReadOnlyList<AutomationObservation> Items, bool TimedOut, string Status)> CollectLocalSubtreeAutomationAsync(
        RectI point, TimeSpan timeout, int maxNodes, CancellationToken cancellationToken, long? scopeHwnd = null)
    {
        if (!_startedCapture || _finalized) throw new InvalidOperationException("Recording is not active.");
        RevalidateTarget();
        if (scopeHwnd is { } scoped && !WindowCatalog.IsSameProcessWindow(_target, scoped))
            throw new InvalidOperationException("Local subtree window is outside the sealed target process.");
        return await CollectAutomationExclusiveAsync(
            () => _automation.CollectLocalSubtreeAsync(_target, point, timeout, maxNodes, cancellationToken, scopeHwnd),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<(IReadOnlyList<AutomationObservation> Items, bool TimedOut, string Status)> CollectAutomationViewAsync(
        long hwnd, AutomationTreeView view, TimeSpan timeout, int maxNodes, CancellationToken cancellationToken)
    {
        if (!_startedCapture || _finalized) throw new InvalidOperationException("Recording is not active.");
        RevalidateTarget();
        if (!WindowCatalog.IsSameProcessWindow(_target, hwnd))
            throw new InvalidOperationException("Automation window is outside the sealed target process.");
        return await CollectAutomationExclusiveAsync(
            () => _automation.CollectViewAsync(_target, hwnd, view, timeout, maxNodes, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<(IReadOnlyList<AutomationObservation> Items, bool TimedOut, string Status)> CollectNativeAutomationViewAsync(
        long hwnd, AutomationTreeView view, TimeSpan timeout, int maxNodes, CancellationToken cancellationToken)
    {
        if (!_startedCapture || _finalized) throw new InvalidOperationException("Recording is not active.");
        RevalidateTarget();
        if (!WindowCatalog.IsSameProcessWindow(_target, hwnd))
            throw new InvalidOperationException("Automation window is outside the sealed target process.");
        return await CollectAutomationExclusiveAsync(
            () => _automation.CollectNativeViewAsync(_target, hwnd, view, timeout, maxNodes, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<(IReadOnlyList<AutomationObservation> Items, bool TimedOut, string Status)> CollectNativePointAutomationAsync(
        long hwnd, RectI point, TimeSpan timeout, int maxNodes, CancellationToken cancellationToken)
    {
        if (!_startedCapture || _finalized) throw new InvalidOperationException("Recording is not active.");
        RevalidateTarget();
        if (!WindowCatalog.IsSameProcessWindow(_target, hwnd))
            throw new InvalidOperationException("Automation window is outside the sealed target process.");
        return await CollectAutomationExclusiveAsync(
            () => _automation.CollectNativePointAsync(_target, hwnd, point, timeout, maxNodes, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<(IReadOnlyList<AutomationObservation> Items, bool TimedOut, string Status)> CollectNativeBandAutomationAsync(
        long hwnd, RectI band, int stepX, int stepY, TimeSpan timeout, int maxNodes,
        CancellationToken cancellationToken)
    {
        if (!_startedCapture || _finalized) throw new InvalidOperationException("Recording is not active.");
        RevalidateTarget();
        if (!WindowCatalog.IsSameProcessWindow(_target, hwnd))
            throw new InvalidOperationException("Automation window is outside the sealed target process.");
        return await CollectAutomationExclusiveAsync(
            () => _automation.CollectNativeBandAsync(_target, hwnd, band, stepX, stepY, timeout, maxNodes, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<(IReadOnlyList<AutomationObservation> Items, bool TimedOut, string Status)> CollectInspectionPointsAutomationAsync(
        long hwnd, IReadOnlyList<RectI> points, TimeSpan timeout, int maxNodes,
        CancellationToken cancellationToken)
    {
        if (!_startedCapture || _finalized) throw new InvalidOperationException("Recording is not active.");
        RevalidateTarget();
        if (!WindowCatalog.IsSameProcessWindow(_target, hwnd))
            throw new InvalidOperationException("Inspection window is outside the sealed target process.");
        return await CollectAutomationExclusiveAsync(
            () => _automation.CollectInspectionPointsAsync(
                _target, hwnd, points, timeout, maxNodes, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<(IReadOnlyList<AutomationObservation> Items, bool TimedOut, string Status)> CollectNativeFocusedAutomationAsync(
        long hwnd, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!_startedCapture || _finalized) throw new InvalidOperationException("Recording is not active.");
        RevalidateTarget();
        if (!WindowCatalog.IsSameProcessWindow(_target, hwnd))
            throw new InvalidOperationException("Automation window is outside the sealed target process.");
        return await CollectAutomationExclusiveAsync(
            () => _automation.CollectNativeFocusAsync(_target, hwnd, timeout, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<(IReadOnlyList<AutomationObservation> Items, bool TimedOut, string Status)> CollectNativeFocusWalkAutomationAsync(
        long hwnd, TimeSpan timeout, int maxNodes, CancellationToken cancellationToken)
    {
        if (!_startedCapture || _finalized) throw new InvalidOperationException("Recording is not active.");
        RevalidateTarget();
        if (!WindowCatalog.IsSameProcessWindow(_target, hwnd))
            throw new InvalidOperationException("Automation window is outside the sealed target process.");
        return await CollectAutomationExclusiveAsync(
            () => _automation.CollectNativeFocusWalkAsync(_target, hwnd, timeout, maxNodes, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<(IReadOnlyList<AutomationObservation> Items, bool TimedOut, string Status)> CollectLegacyAutomationAsync(
        long hwnd, TimeSpan timeout, int maxNodes, CancellationToken cancellationToken)
    {
        if (!_startedCapture || _finalized) throw new InvalidOperationException("Recording is not active.");
        RevalidateTarget();
        if (!WindowCatalog.IsSameProcessWindow(_target, hwnd))
            throw new InvalidOperationException("Legacy automation window is outside the sealed target process.");
        return await CollectAutomationExclusiveAsync(
            () => _automation.CollectLegacyAsync(_target, hwnd, timeout, maxNodes, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<FrameObservation> CaptureAutomationDeltaAsync(
        string trigger,
        IReadOnlyList<AutomationObservation> automation,
        CancellationToken cancellationToken,
        long? baseFrameSequence = null,
        long? observedWindowHwnd = null,
        bool automationTimedOut = false,
        string automationStatus = "ok",
        AdaptiveExtractionSnapshot? extraction = null)
    {
        if (!_startedCapture || _finalized) throw new InvalidOperationException("Recording is not active.");
        await _captureGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ObserveFrameAsync(trigger, cancellationToken,
                new FrameCaptureOptions(
                    IncludeAutomation: false,
                    CapturePhase: "materialized",
                    ObservationScope: "control-delta",
                    ObservedWindowHwnds: [observedWindowHwnd ?? _target.RootOwnerHwnd],
                    AdditionalScopedWindowHwnds: observedWindowHwnd is { } scoped && scoped != _target.RootOwnerHwnd
                        ? [scoped]
                        : null,
                    BaseFrameSequence: baseFrameSequence,
                    AutomationOverride: automation,
                    AutomationTimedOutOverride: automationTimedOut,
                    AutomationStatusOverride: automationStatus,
                    ExtractionOverride: extraction),
                persistFrame: true,
                captureScreenshot: false,
                automationTimeout: TimeSpan.Zero).ConfigureAwait(false);
        }
        finally { _captureGate.Release(); }
    }

    internal async Task<PopupDeltaPreparation> TryPreparePopupDeltaAsync(
        long hwnd,
        long baseFrameSequence,
        TimeSpan screenshotTimeout,
        Func<long, CancellationToken,
            Task<(IReadOnlyList<AutomationObservation> Items, bool TimedOut, string Status)>> collectAutomationAsync,
        Func<WindowTarget, IReadOnlyList<AutomationObservation>, IReadOnlyList<AutomationObservation>> normalizeAutomation,
        Func<WindowTarget, IReadOnlyList<AutomationObservation>, IReadOnlyList<AutomationObservation>, bool> snapshotsMatch,
        CancellationToken cancellationToken,
        bool waitForDeferredVisualContent = false)
    {
        ArgumentNullException.ThrowIfNull(collectAutomationAsync);
        ArgumentNullException.ThrowIfNull(normalizeAutomation);
        ArgumentNullException.ThrowIfNull(snapshotsMatch);
        if (screenshotTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(screenshotTimeout));
        if (!_startedCapture || _finalized) throw new InvalidOperationException("Recording is not active.");

        await _captureGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RevalidateTarget();
            WindowTarget popup;
            try { popup = WindowCatalog.Resolve(hwnd); }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                return new(null, "window-unavailable");
            }
            if (popup.ProcessId != _target.ProcessId || popup.ProcessStartedUtc != _target.ProcessStartedUtc ||
                popup.Hwnd == popup.RootOwnerHwnd || !NativeMethods.IsWindowVisible((nint)hwnd) ||
                popup.Bounds.Width <= 0 || popup.Bounds.Height <= 0)
                return new(null, "window-unavailable");

            var firstResult = await collectAutomationAsync(hwnd, cancellationToken).ConfigureAwait(false);
            if (!IsUsablePopupAutomationResult(firstResult.TimedOut, firstResult.Status))
                return new(null, "uia-" + firstResult.Status);
            var first = normalizeAutomation(popup, firstResult.Items);
            if (first.Count == 0)
                return new(null, "content-incomplete");

            WindowTarget captureRoot;
            try
            {
                var hostHwnd = popup.RootOwnerHwnd != popup.Hwnd &&
                               WindowCatalog.IsSameProcessWindow(_target, popup.RootOwnerHwnd)
                    ? popup.RootOwnerHwnd
                    : _target.RootOwnerHwnd;
                captureRoot = WindowCatalog.Resolve(hostHwnd);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                return new(null, "window-unavailable");
            }
            var screenshotTargets = new[] { captureRoot, popup }
                .DistinctBy(target => target.Hwnd)
                .ToArray();
            var screenshotBounds = CompositeBounds(screenshotTargets);

            // Readiness transaction: UIA A -> pixels -> UIA B. The screenshot is
            // evidence of the same stable state only when the second UIA reading
            // and popup bounds still match the first reading.
            WindowSnapshotCapture.CaptureResult capture;
            try
            {
                capture = await CaptureStableScreenshotAsync(
                    screenshotTargets,
                    screenshotTimeout,
                    waitForDeferredVisualContent,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new(null, "screenshot-timeout");
            }
            catch (Exception ex) when (WindowSnapshotCapture.IsRecoverableCaptureFailure(ex))
            {
                return new(null, "screenshot-unavailable");
            }
            if (capture.Png.Length > 16 * 1024 * 1024)
                return new(null, "screenshot-too-large");

            var secondResult = await collectAutomationAsync(hwnd, cancellationToken).ConfigureAwait(false);
            if (!IsUsablePopupAutomationResult(secondResult.TimedOut, secondResult.Status))
                return new(null, "uia-" + secondResult.Status);

            WindowTarget finalPopup;
            try { finalPopup = WindowCatalog.Resolve(hwnd); }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                return new(null, "window-unavailable");
            }
            var second = normalizeAutomation(finalPopup, secondResult.Items);
            if (finalPopup.Bounds != popup.Bounds || !NativeMethods.IsWindowVisible((nint)hwnd))
                return new(null, "bounds-changed");
            if (second.Count == 0 || !snapshotsMatch(finalPopup, first, second))
                return new(null, "structure-changing");

            try
            {
                var capturedPopup = WindowCatalog.Resolve(hwnd);
                if (capturedPopup.Bounds != finalPopup.Bounds || !NativeMethods.IsWindowVisible((nint)hwnd))
                    return new(null, "bounds-changed");
                finalPopup = capturedPopup;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                return new(null, "window-unavailable");
            }

            var discoveredTargets = WindowCatalog.ListProcessWindows(_target)
                .Where(target => target.Hwnd == _target.RootOwnerHwnd ||
                                 target.Hwnd == popup.RootOwnerHwnd ||
                                 target.Hwnd == hwnd)
                .OrderByDescending(target => target.Hwnd == _target.RootOwnerHwnd)
                .ThenByDescending(target => target.Hwnd == popup.RootOwnerHwnd)
                .ThenByDescending(target => target.Hwnd == hwnd)
                .ToArray();
            var scopeWasTruncated = discoveredTargets.Length > WindowSnapshotCapture.MaxScopedWindows;
            var scopedTargets = new[] { captureRoot, finalPopup }
                .Concat(discoveredTargets.Where(target => target.Hwnd == _target.RootOwnerHwnd || target.Hwnd == hwnd))
                .Concat(discoveredTargets.Where(target => target.Hwnd != _target.RootOwnerHwnd && target.Hwnd != hwnd))
                .DistinctBy(target => target.Hwnd)
                .Take(WindowSnapshotCapture.MaxScopedWindows)
                .ToArray();
            var scopedWindows = scopedTargets.Select(WindowSnapshotCapture.Observe).ToArray();
            var root = scopedWindows.FirstOrDefault(item => item.Hwnd == _target.RootOwnerHwnd) ?? WindowSnapshotCapture.Observe(_target);
            var popupObservation = scopedWindows.FirstOrDefault(item => item.Hwnd == hwnd) ?? WindowSnapshotCapture.Observe(finalPopup);
            return new(new(finalPopup, root, scopedWindows, screenshotBounds, second,
                secondResult.Status, capture.Png, capture.Method, capture.UsedFallback, capture.IsPartial,
                scopeWasTruncated, baseFrameSequence), "ready");
        }
        finally { _captureGate.Release(); }
    }

    internal async Task<PopupDeltaPreparation> TryPrepareVisualPopupDeltaAsync(
        long hwnd,
        long baseFrameSequence,
        TimeSpan screenshotTimeout,
        CancellationToken cancellationToken)
    {
        if (screenshotTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(screenshotTimeout));
        if (!_startedCapture || _finalized) throw new InvalidOperationException("Recording is not active.");

        await _captureGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RevalidateTarget();
            WindowTarget popup;
            try { popup = WindowCatalog.Resolve(hwnd); }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                return new(null, "window-unavailable");
            }
            if (popup.ProcessId != _target.ProcessId || popup.ProcessStartedUtc != _target.ProcessStartedUtc ||
                popup.Hwnd == popup.RootOwnerHwnd || !NativeMethods.IsWindowVisible((nint)hwnd) ||
                popup.Bounds.Width <= 0 || popup.Bounds.Height <= 0)
                return new(null, "window-unavailable");

            WindowSnapshotCapture.CaptureResult capture;
            try
            {
                // Capture the popup itself before a slow accessibility provider can
                // make the automatic pass miss a state that is already painted.
                capture = await CaptureStableScreenshotAsync(
                    [popup],
                    screenshotTimeout,
                    waitForDeferredVisualContent: false,
                    cancellationToken,
                    preferScreenBounds: true).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new(null, "screenshot-timeout");
            }
            catch (Exception ex) when (WindowSnapshotCapture.IsRecoverableCaptureFailure(ex))
            {
                return new(null, "screenshot-unavailable");
            }
            if (capture.Png.Length > 16 * 1024 * 1024)
                return new(null, "screenshot-too-large");

            WindowTarget finalPopup;
            try { finalPopup = WindowCatalog.Resolve(hwnd); }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                return new(null, "window-unavailable");
            }
            if (finalPopup.Bounds != popup.Bounds || !NativeMethods.IsWindowVisible((nint)hwnd))
                return new(null, "bounds-changed");

            IReadOnlyList<AutomationObservation> visualControls;
            try
            {
                var pixels = OpaqueSurfaceScanner.PixelFrame.Decode(capture.Png);
                visualControls = await Task.Run(
                    () => VisualSurfaceScanner.DiscoverGeometry(
                        finalPopup, pixels, [finalPopup.Bounds], []),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidDataException or InvalidOperationException)
            {
                visualControls = [];
            }
            var automation = BuildVisualPopupFallbackAutomation(finalPopup, visualControls, capture.Png);

            WindowTarget captureRoot;
            try
            {
                var hostHwnd = finalPopup.RootOwnerHwnd != finalPopup.Hwnd &&
                               WindowCatalog.IsSameProcessWindow(_target, finalPopup.RootOwnerHwnd)
                    ? finalPopup.RootOwnerHwnd
                    : _target.RootOwnerHwnd;
                captureRoot = WindowCatalog.Resolve(hostHwnd);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                captureRoot = _target;
            }

            var scopedTargets = new[] { captureRoot, finalPopup }
                .DistinctBy(target => target.Hwnd)
                .ToArray();
            var scopedWindows = scopedTargets.Select(WindowSnapshotCapture.Observe).ToArray();
            var root = scopedWindows.FirstOrDefault(item => item.Hwnd == _target.RootOwnerHwnd) ??
                       WindowSnapshotCapture.Observe(captureRoot);
            return new(new(
                finalPopup,
                root,
                scopedWindows,
                finalPopup.Bounds,
                automation,
                "visual-only",
                capture.Png,
                capture.Method,
                capture.UsedFallback,
                capture.IsPartial,
                ScopeWasTruncated: false,
                BaseFrameSequence: baseFrameSequence,
                AutomationTimedOut: true), "ready-visual-fallback");
        }
        finally { _captureGate.Release(); }
    }

    internal static IReadOnlyList<AutomationObservation> BuildVisualPopupFallbackAutomation(
        WindowTarget popup,
        IReadOnlyList<AutomationObservation> visualControls,
        byte[] png)
    {
        ArgumentNullException.ThrowIfNull(popup);
        ArgumentNullException.ThrowIfNull(visualControls);
        ArgumentNullException.ThrowIfNull(png);

        var rootId = $"popup-visual-root:{popup.Hwnd:x}";
        var root = new AutomationObservation(
            rootId,
            "",
            "",
            string.IsNullOrWhiteSpace(popup.Title) ? "Visible popup" : popup.Title,
            "ControlType.Window",
            popup.ClassName,
            popup.Bounds,
            IsEnabled: true,
            IsOffscreen: false,
            FrameworkId: "Win32",
            WindowHwnd: popup.Hwnd);
        var visualIds = visualControls
            .Where(control => !string.IsNullOrWhiteSpace(control.RuntimeId))
            .Select(control => control.RuntimeId)
            .ToHashSet(StringComparer.Ordinal);
        var normalized = visualControls
            .Where(control => control.Bounds.Width > 0 && control.Bounds.Height > 0)
            .Select(control => control with
            {
                ParentRuntimeId = string.IsNullOrWhiteSpace(control.ParentRuntimeId) ||
                                  !visualIds.Contains(control.ParentRuntimeId)
                    ? rootId
                    : control.ParentRuntimeId,
                IsOffscreen = false,
                WindowHwnd = popup.Hwnd
            })
            .ToList();
        if (!normalized.Any(IsMeaningfulVisualPopupControl))
        {
            var contentHash = Convert.ToHexString(SHA256.HashData(png)).ToLowerInvariant()[..24];
            var contentId = "visual:popup-surface:" + contentHash;
            normalized.Add(new AutomationObservation(
                contentId,
                rootId,
                contentId,
                "Visible popup content",
                "ControlType.Custom",
                "UiAtlas.VisualControlRegion",
                popup.Bounds,
                IsEnabled: false,
                IsOffscreen: false,
                FrameworkId: "UiAtlas.Visual.Geometry",
                WindowHwnd: popup.Hwnd,
                VisualRole: "popup-surface"));
        }
        return new[] { root }.Concat(normalized).ToArray();
    }

    private static bool IsMeaningfulVisualPopupControl(AutomationObservation control)
    {
        var type = control.ControlType.StartsWith("ControlType.", StringComparison.OrdinalIgnoreCase)
            ? control.ControlType[12..]
            : control.ControlType;
        return type is "Button" or "CheckBox" or "ComboBox" or "DataItem" or "Edit" or "Hyperlink" or
            "List" or "ListItem" or "MenuItem" or "RadioButton" or "ScrollBar" or "Slider" or "Spinner" or
            "SplitButton" or "TabItem" or "Thumb" or "Tree" or "TreeItem" or "Custom";
    }

    private async Task<WindowSnapshotCapture.CaptureResult> CaptureStableScreenshotAsync(
        IReadOnlyList<WindowTarget> screenshotTargets,
        TimeSpan screenshotTimeout,
        bool waitForDeferredVisualContent,
        CancellationToken cancellationToken,
        bool preferScreenBounds = false,
        Func<byte[], bool>? contentReady = null)
    {
        async Task<WindowSnapshotCapture.CaptureResult> CaptureOnceAsync(
            CancellationToken captureCancellationToken,
            bool useScreenBounds)
        {
            using var screenshotCancellation = CancellationTokenSource.CreateLinkedTokenSource(captureCancellationToken);
            screenshotCancellation.CancelAfter(screenshotTimeout);
            return await CaptureScreenshotAsync(
                token => WindowSnapshotCapture.CapturePngAsync(
                    screenshotTargets, token, useScreenBounds),
                screenshotCancellation.Token).ConfigureAwait(false);
        }

        var result = await WaitForStableScreenshotAsync(
            token => CaptureOnceAsync(token, preferScreenBounds),
            waitForDeferredVisualContent,
            contentReady,
            DeferredVisualSampleInterval,
            DeferredVisualMinimumObservation,
            DeferredVisualQuietWindow,
            DeferredVisualMaximumObservation,
            cancellationToken).ConfigureAwait(false);

        // A non-empty WGC image can still contain only the shell of an
        // owner-drawn dialog. If UIA says text/buttons exist but none are
        // visibly painted, preserve what is actually on screen instead.
        if (!preferScreenBounds && contentReady is not null && !contentReady(result.Png))
            return await CaptureOnceAsync(cancellationToken, useScreenBounds: true).ConfigureAwait(false);

        return result;
    }

    internal static async Task<WindowSnapshotCapture.CaptureResult> WaitForStableScreenshotAsync(
        Func<CancellationToken, Task<WindowSnapshotCapture.CaptureResult>> captureOnceAsync,
        bool waitForDeferredVisualContent,
        Func<byte[], bool>? contentReady,
        TimeSpan sampleInterval,
        TimeSpan minimumObservation,
        TimeSpan quietWindow,
        TimeSpan maximumObservation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(captureOnceAsync);
        var latest = await captureOnceAsync(cancellationToken).ConfigureAwait(false);
        if (!waitForDeferredVisualContent)
            return latest;

        var latestReady = contentReady?.Invoke(latest.Png) ?? true;
        var lastReady = latestReady ? latest : null;
        var timer = Stopwatch.StartNew();
        var quietSince = timer.Elapsed;
        while (timer.Elapsed < maximumObservation)
        {
            await Task.Delay(sampleInterval, cancellationToken).ConfigureAwait(false);
            var next = await captureOnceAsync(cancellationToken).ConfigureAwait(false);
            if (!WindowSnapshotCapture.AreVisuallyEquivalentPng(latest.Png, next.Png))
                quietSince = timer.Elapsed;
            latest = next;
            latestReady = contentReady?.Invoke(latest.Png) ?? true;
            if (latestReady)
                lastReady = latest;

            // A motionless blank dialog is not ready. Office can expose its UIA
            // text and buttons before those same elements have actually painted.
            if (latestReady && timer.Elapsed >= minimumObservation &&
                timer.Elapsed - quietSince >= quietWindow)
                break;
        }

        return latestReady ? latest : lastReady ?? latest;
    }

    internal Task<WindowSnapshotCapture.CaptureResult> CaptureStableRootScreenshotAsync(
        IReadOnlyList<WindowTarget> screenshotTargets,
        CancellationToken cancellationToken) =>
        CaptureStableScreenshotAsync(
            screenshotTargets,
            TimeSpan.FromMilliseconds(700),
            waitForDeferredVisualContent: true,
            cancellationToken: cancellationToken,
            preferScreenBounds: true);

    internal async Task<FrameObservation> PersistPreparedPopupDeltaAsync(
        PreparedPopupDelta prepared,
        CancellationToken cancellationToken,
        AutomationObservation? interactionSource = null,
        string? interactionId = null)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        if (!_startedCapture || _finalized) throw new InvalidOperationException("Recording is not active.");
        if (DateTimeOffset.UtcNow - _started > MaxDuration)
            throw new InvalidOperationException("Recording quota reached; finalize or cancel the session.");

        await _captureGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var sequence = Interlocked.Increment(ref _frameSequence);
            var frameEntry = $"raw/frames/frame-{sequence:D6}.png";
            var observation = new FrameObservation(
                sequence,
                DateTimeOffset.UtcNow,
                frameEntry,
                prepared.RootWindow,
                prepared.Automation,
                prepared.AutomationTimedOut,
                prepared.AutomationStatus,
                "adaptive-popup",
                prepared.ScopedWindows,
                CapturePhase: "materialized",
                ObservationScope: "popup-delta",
                ObservedWindowHwnds: [prepared.Popup.Hwnd],
                ScreenshotBounds: prepared.ScreenshotBounds,
                BaseFrameSequence: prepared.BaseFrameSequence,
                InteractionSource: interactionSource,
                InteractionId: interactionId);

            lock (_stateGate)
            {
                if (_finalized) throw new OperationCanceledException("Recording finalized before the delta was persisted.");
                _writer.WriteBytes(frameEntry, prepared.Png);
                _latestScreenshotSequence = sequence;
                _latestScreenshotPng = prepared.Png;
                _hasPartialCapture |= prepared.IsPartial || prepared.ScopeWasTruncated;
                _health.Add(new(DateTimeOffset.UtcNow, "screenshot", prepared.CaptureMethod,
                    prepared.UsedFallback ? "Native window capture was unavailable; the scoped fallback was used." :
                    prepared.IsPartial ? "Scoped native window capture reached a cumulative limit; uncaptured regions are blank." :
                    "Scoped native window capture succeeded.", true));
                if (prepared.ScopeWasTruncated)
                    _health.Add(new(DateTimeOffset.UtcNow, "scope", "window-limit",
                        "Owned-window scope exceeded the bounded recorder limit; the popup frame is partial.", true));
                _frames.Add(new(observation.Sequence, observation.TimestampUtc, observation.Trigger, observation.EpisodeSequence,
                    observation.CapturePhase, observation.AutomationStatus, StructuralFingerprint(observation)));
                _frameObservations.Add(observation);
                _writer.WriteJson($"raw/observations/frame-{sequence:D6}.json", observation);
            }
            return observation;
        }
        finally { _captureGate.Release(); }
    }

    private static bool IsUsablePopupAutomationResult(bool timedOut, string status) =>
        !timedOut && status is "ok" or "node-limit";

    internal static RectI CompositeBounds(IReadOnlyList<WindowTarget> windows)
    {
        if (windows.Count == 0) throw new ArgumentException("At least one window is required.", nameof(windows));
        var left = windows.Min(window => window.Bounds.X);
        var top = windows.Min(window => window.Bounds.Y);
        var right = windows.Max(window => checked(window.Bounds.X + window.Bounds.Width));
        var bottom = windows.Max(window => checked(window.Bounds.Y + window.Bounds.Height));
        return new(left, top, checked(right - left), checked(bottom - top));
    }

    public async Task<(IReadOnlyList<AutomationObservation> Items, bool TimedOut, string Status)> CollectRibbonAutomationAsync(
        long hwnd,
        TimeSpan timeout,
        int maxNodes,
        CancellationToken cancellationToken)
    {
        if (!_startedCapture || _finalized) throw new InvalidOperationException("Recording is not active.");
        RevalidateTarget();
        if (WindowCatalog.GetRootOwnerHandle((nint)hwnd).ToInt64() != _target.RootOwnerHwnd)
            throw new InvalidOperationException("Automation window is outside the sealed target scope.");
        return await CollectAutomationExclusiveAsync(
            () => _automation.CollectRibbonAsync(_target, timeout, maxNodes, cancellationToken, hwnd),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<(IReadOnlyList<AutomationObservation> Items, bool TimedOut, string Status)> CollectWorksheetAutomationAsync(
        long hwnd,
        TimeSpan timeout,
        int maxNodes,
        CancellationToken cancellationToken)
    {
        if (!_startedCapture || _finalized) throw new InvalidOperationException("Recording is not active.");
        RevalidateTarget();
        if (WindowCatalog.GetRootOwnerHandle((nint)hwnd).ToInt64() != _target.RootOwnerHwnd)
            throw new InvalidOperationException("Automation window is outside the sealed target scope.");
        return await CollectAutomationExclusiveAsync(
            () => _automation.CollectWorksheetAsync(_target, timeout, maxNodes, cancellationToken, hwnd),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<(IReadOnlyList<AutomationObservation> Items, bool TimedOut, string Status)> CollectAutomationExclusiveAsync(
        Func<Task<(IReadOnlyList<AutomationObservation> Items, bool TimedOut, string Status)>> collect,
        CancellationToken cancellationToken)
    {
        await _automationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await collect().ConfigureAwait(false);
        }
        finally
        {
            _automationGate.Release();
        }
    }

    public Task<DateTimeOffset> WaitForClicksAsync(int clickCount, Action<int, int>? progress, CancellationToken cancellationToken)
    {
        if (!_startedCapture || _finalized) throw new InvalidOperationException("Recording is not active.");
        RevalidateTarget();
        return _manualInput.WaitForClicksAsync(clickCount, progress, cancellationToken);
    }

    public async Task<IReadOnlyList<InputEvent>> WaitForRecordedTargetClicksAsync(
        DateTimeOffset afterExclusive,
        int clickCount,
        Action<int, int>? progress,
        CancellationToken cancellationToken)
    {
        if (!_startedCapture || _finalized) throw new InvalidOperationException("Recording is not active.");
        if (clickCount is < 1 or > 2) throw new ArgumentOutOfRangeException(nameof(clickCount));
        RevalidateTarget();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<InputEvent> clicks;
            lock (_stateGate)
            {
                DrainInputUnsafe();
                clicks = SelectRecordedTargetClicks(_events, _target.RootOwnerHwnd, afterExclusive, clickCount);
            }

            if (clicks.Count >= clickCount)
            {
                for (var index = 0; index < clicks.Count; index++)
                    progress?.Invoke(index + 1, clickCount);
                return clicks;
            }

            await Task.Delay(20, cancellationToken).ConfigureAwait(false);
        }
    }

    internal static IReadOnlyList<InputEvent> SelectRecordedTargetClicks(
        IEnumerable<InputEvent> events,
        long rootOwnerHwnd,
        DateTimeOffset afterExclusive,
        int count)
    {
        ArgumentNullException.ThrowIfNull(events);
        if (count < 1) throw new ArgumentOutOfRangeException(nameof(count));
        return events
            .Where(item => item.Kind == InputEventKind.PointerUp &&
                           item.RootOwnerHwnd == rootOwnerHwnd &&
                           item.TimestampUtc > afterExclusive)
            .OrderBy(item => item.TimestampUtc)
            .ThenBy(item => item.Sequence)
            .Take(count)
            .ToArray();
    }

    public void SetInputCapturePaused(bool paused)
    {
        if (!_startedCapture || _finalized) return;
        Volatile.Write(ref _inputCapturePaused, paused ? 1 : 0);
        _input.SetInputCapturePaused(paused);
    }

    public void DismissTransientPopup()
    {
        if (!_startedCapture || _finalized) return;
        var target = (nint)_target.Hwnd;
        if (NativeMethods.IsWindow(target))
        {
            NativeMethods.BringWindowToTop(target);
            NativeMethods.SetForegroundWindow(target);
        }
        NativeMethods.keybd_event(NativeMethods.VkEscape, 0, 0, 0);
        NativeMethods.keybd_event(NativeMethods.VkEscape, 0, NativeMethods.KeyeventfKeyup, 0);
    }

    public async Task DismissTransientPopupAsync(CancellationToken cancellationToken)
    {
        if (!_startedCapture || _finalized) return;
        var trace = CreateDismissInteraction(null, "auto-dismiss-popup");
        _ = await ActivateTargetAsync(cancellationToken).ConfigureAwait(false);
        DismissTransientPopup();
        await Task.Delay(80, cancellationToken).ConfigureAwait(false);
        if (trace is not null)
            CompleteInteraction(trace, InteractionOutcome.Unobserved, diagnosticCode: "dismiss-sent-result-not-captured");
    }

    public async Task<FrameObservation?> CaptureOwnedDialogAsync(
        long hwnd,
        IReadOnlyList<AutomationObservation> rootAutomation,
        CancellationToken cancellationToken,
        Func<long, CancellationToken,
            Task<(IReadOnlyList<AutomationObservation> Items, bool TimedOut, string Status)>>? collectAutomationOverride = null,
        InteractionCaptureContext? interaction = null)
    {
        ArgumentNullException.ThrowIfNull(rootAutomation);
        if (!_startedCapture || _finalized) throw new InvalidOperationException("Recording is not active.");
        var deadline = DateTimeOffset.UtcNow.AddSeconds(7);
        string? previousFingerprint = null;
        var peerRootObserved = false;
        WindowTarget? peerRoot = null;
        (IReadOnlyList<AutomationObservation> Items, bool TimedOut, string Status)? lastPeerRead = null;
        while (DateTimeOffset.UtcNow < deadline && NativeMethods.IsWindowVisible((nint)hwnd))
        {
            WindowTarget dialog;
            try { dialog = WindowCatalog.Resolve(hwnd); }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                return null;
            }
            if (dialog.ProcessId != _target.ProcessId || dialog.ProcessStartedUtc != _target.ProcessStartedUtc ||
                dialog.Bounds.Width <= 0 || dialog.Bounds.Height <= 0)
                return null;
            var isPeerRoot = AdaptiveCaptureCoordinator.IsPeerRootCaptureCandidate(dialog);
            peerRootObserved |= isPeerRoot;
            if (isPeerRoot) peerRoot = dialog;

            var read = collectAutomationOverride is not null
                ? await collectAutomationOverride(hwnd, cancellationToken).ConfigureAwait(false)
                : await CollectDialogAutomationAsync(
                    hwnd, isPeerRoot ? TimeSpan.FromSeconds(8) : TimeSpan.FromSeconds(4),
                    1_800, cancellationToken).ConfigureAwait(false);
            if (isPeerRoot) lastPeerRead = read;
            if (!read.TimedOut && read.Status is "ok" or "node-limit" && HasMeaningfulDialogContent(read.Items))
            {
                // A peer root is a durable application window, not a transient owned dialog.
                // Outlook custom-form controls can be grafted into the Explorer provider and
                // need not produce two byte-identical reads from the peer HWND. One complete,
                // meaningful bounded read is sufficient when native process identity is sealed.
                if (isPeerRoot)
                    return await CaptureDialogFrameAsync(
                        dialog, rootAutomation, read.Items, read.TimedOut, read.Status,
                        interaction, cancellationToken).ConfigureAwait(false);

                var fingerprint = DialogFingerprint(dialog, read.Items);
                if (string.Equals(previousFingerprint, fingerprint, StringComparison.Ordinal))
                    return await CaptureDialogFrameAsync(
                        dialog, rootAutomation, read.Items, read.TimedOut, read.Status,
                        interaction, cancellationToken).ConfigureAwait(false);
                previousFingerprint = fingerprint;
            }
            else
                previousFingerprint = null;

            // Do not throw away an independently verified peer HWND because its
            // accessibility provider is incomplete. Retain native identity and a
            // screenshot immediately; capture health records the missing controls.
            if (isPeerRoot) break;

            await Task.Delay(80, cancellationToken).ConfigureAwait(false);
        }

        AddCaptureHealth(
            "adaptive",
            peerRootObserved ? "peer-root-controls-missed" : "dialog-controls-missed",
            peerRootObserved
                ? "The same-process peer window did not expose a complete, meaningful accessibility snapshot before the bounded deadline; native window and screenshot evidence were retained."
                : "The owned dialog did not expose two stable, meaningful UIA snapshots before the bounded deadline.",
            recoverable: !peerRootObserved);
        if (peerRoot is not null && NativeMethods.IsWindowVisible((nint)peerRoot.Hwnd))
        {
            var fallback = lastPeerRead ?? (Array.Empty<AutomationObservation>(), true, "unavailable");
            return await CaptureDialogFrameAsync(
                peerRoot, rootAutomation, fallback.Items, fallback.TimedOut, fallback.Status,
                interaction, cancellationToken).ConfigureAwait(false);
        }
        return null;
    }

    private Task<FrameObservation> CaptureDialogFrameAsync(
        WindowTarget dialog,
        IReadOnlyList<AutomationObservation> rootAutomation,
        IReadOnlyList<AutomationObservation> dialogAutomation,
        bool automationTimedOut,
        string automationStatus,
        InteractionCaptureContext? interaction,
        CancellationToken cancellationToken)
    {
        // A dialog frame must describe the dialog itself. Mixing the parent
        // application's tree into this frame made the evidence viewer paint the
        // whole application and caused dialog controls to be attached to the
        // wrong surface in Raw/Semantic World.
        var completeAutomation = dialogAutomation
            .GroupBy(control => string.IsNullOrWhiteSpace(control.RuntimeId)
                ? $"{control.WindowHwnd}:{control.Bounds.X},{control.Bounds.Y},{control.Bounds.Width},{control.Bounds.Height}:{control.AutomationId}"
                : $"{control.WindowHwnd}:{control.RuntimeId}", StringComparer.Ordinal)
            .Select(group => group.Last())
            .ToArray();
        return CaptureAsync(
            "adaptive-dialog:" + (string.IsNullOrWhiteSpace(dialog.Title) ? dialog.ClassName : dialog.Title),
            cancellationToken,
            new FrameCaptureOptions(
                IncludeAutomation: false,
                CapturePhase: "materialized",
                ObservationScope: "full-root",
                ObservedWindowHwnds: [dialog.Hwnd],
                ScreenshotWindowHwnds: [dialog.Hwnd],
                AdditionalScopedWindowHwnds: [dialog.Hwnd],
                PrimaryWindowHwnd: dialog.RootOwnerHwnd == _target.RootOwnerHwnd ? dialog.Hwnd : null,
                AutomationOverride: completeAutomation,
                AutomationTimedOutOverride: automationTimedOut,
                AutomationStatusOverride: automationStatus,
                ScreenshotTimeout: TimeSpan.FromSeconds(3),
                InteractionSource: interaction?.SourceControl,
                InteractionId: interaction?.InteractionId,
                PreferScreenBoundsScreenshot: WindowSnapshotCapture.RequiresScreenBoundsCaptureForDialog(dialog),
                WaitForDeferredVisualContent: true,
                RequireRenderedAutomationContent: true));
    }

    private async Task<(IReadOnlyList<AutomationObservation> Items, bool TimedOut, string Status)> CollectDialogAutomationAsync(
        long hwnd,
        TimeSpan timeout,
        int maxNodes,
        CancellationToken cancellationToken)
    {
        if (!WindowCatalog.IsSameProcessWindow(_target, hwnd))
            throw new InvalidOperationException("Dialog automation window is outside the sealed target process.");
        return await CollectAutomationExclusiveAsync(
            () => _automation.CollectDialogAsync(_target, hwnd, timeout, maxNodes, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    internal async Task<bool> TrySelectDialogTabAsync(
        long dialogHwnd,
        AutomationObservation tab,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tab);
        if (!WindowCatalog.IsSameProcessWindow(_target, dialogHwnd) ||
            tab.WindowHwnd != dialogHwnd ||
            NormalizeAutomationControlType(tab.ControlType) != "TabItem")
            return false;
        TryActivateWindow((nint)dialogHwnd);
        await Task.Delay(30, cancellationToken).ConfigureAwait(false);
        if (tab.ClassName == "OfficeDialogTab")
            return ProgrammaticControlInvoker.TrySelectNextDialogTab();
        if (ShouldUseDirectClickForDialogTab(tab))
            return ProgrammaticControlInvoker.TryClickCenter(tab.Bounds);
        return ProgrammaticControlInvoker.TrySelectObservedControlAtPoint(tab);
    }

    internal static bool ShouldUseDirectClickForDialogTab(AutomationObservation tab) =>
        NormalizeAutomationControlType(tab.ControlType) == "TabItem" &&
        tab.ClassName.StartsWith("MSAA.Role", StringComparison.OrdinalIgnoreCase);

    public async Task<bool> DismissOwnedDialogAsync(long hwnd, CancellationToken cancellationToken)
    {
        if (!_startedCapture || _finalized || !NativeMethods.IsWindow((nint)hwnd)) return true;
        var trace = CreateDismissInteraction(hwnd, "auto-dismiss-dialog");
        var dialog = (nint)hwnd;
        WindowTarget dialogTarget;
        try
        {
            dialogTarget = WindowCatalog.Resolve(hwnd);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return true;
        }
        if (dialogTarget.ProcessId != _target.ProcessId ||
            dialogTarget.ProcessStartedUtc != _target.ProcessStartedUtc)
        {
            if (trace is not null)
                CompleteInteraction(trace, InteractionOutcome.Failed, diagnosticCode: "dialog-outside-target");
            return false;
        }

        // Do not spend another UIA tree walk and do not depend on focus or screen
        // coordinates after the dialog has already been captured. Standard Win32
        // and Office dialogs route IDCANCEL through their normal Cancel handler.
        // BM_CLICK handles dialogs that expose a native Cancel child; WM_COMMAND
        // handles owner-drawn Office dialogs where the child lookup is unavailable.
        var cancel = NativeMethods.GetDlgItem(dialog, NativeMethods.IdCancel);
        if (cancel != 0 && NativeMethods.IsWindow(cancel))
            _ = NativeMethods.PostMessageW(cancel, NativeMethods.BmClick, 0, 0);
        _ = NativeMethods.PostMessageW(
            dialog, NativeMethods.WmCommand, new nint(NativeMethods.IdCancel), cancel);
        if (await WaitForWindowToCloseAsync(dialog, TimeSpan.FromMilliseconds(350), cancellationToken).ConfigureAwait(false))
        {
            if (trace is not null)
                CompleteInteraction(trace, InteractionOutcome.Unobserved, diagnosticCode: "dismissed-via-cancel-result-not-captured");
            return true;
        }

        // Closing from the title-bar is also defined as Cancel for these modal
        // property dialogs. Send it directly to the captured HWND, without trying
        // four or five physical clicks against a potentially stale rectangle.
        _ = NativeMethods.PostMessageW(
            dialog, NativeMethods.WmSysCommand, new nint(NativeMethods.ScClose), 0);
        if (await WaitForWindowToCloseAsync(dialog, TimeSpan.FromMilliseconds(350), cancellationToken).ConfigureAwait(false))
        {
            if (trace is not null)
                CompleteInteraction(trace, InteractionOutcome.Unobserved, diagnosticCode: "dismissed-via-system-close-result-not-captured");
            return true;
        }

        _ = NativeMethods.PostMessageW(dialog, NativeMethods.WmClose, 0, 0);
        if (await WaitForWindowToCloseAsync(dialog, TimeSpan.FromMilliseconds(900), cancellationToken).ConfigureAwait(false))
        {
            if (trace is not null)
                CompleteInteraction(trace, InteractionOutcome.Unobserved, diagnosticCode: "dismissed-via-window-close-result-not-captured");
            return true;
        }

        AddCaptureHealth("auto-tabs", "dialog-dismiss-failed",
            "The owned dialog remained visible after direct IDCANCEL, SC_CLOSE, and WM_CLOSE commands.");
        if (trace is not null)
            CompleteInteraction(trace, InteractionOutcome.Failed, diagnosticCode: "dialog-dismiss-failed");
        return false;
    }

    private InteractionCaptureContext? CreateDismissInteraction(long? hwnd, string operationId)
    {
        FrameObservation? frame;
        lock (_stateGate) frame = _frameObservations.OrderByDescending(item => item.Sequence).FirstOrDefault();
        if (frame is null) return null;
        var source = frame.Automation.FirstOrDefault(control => control.HasKeyboardFocus &&
                         (!hwnd.HasValue || control.WindowHwnd == hwnd.Value))
                     ?? frame.Automation.FirstOrDefault(control => !hwnd.HasValue || control.WindowHwnd == hwnd.Value);
        if (source is null) return null;
        return CreateInteractionContext(operationId, 1, InteractionActor.AutoExplorer,
            InteractionGestureKind.ProgrammaticInvoke, InteractionActionKind.Dismiss,
            frame.Sequence, source);
    }

    private static async Task<bool> WaitForWindowToCloseAsync(
        nint hwnd,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (NativeMethods.IsWindow(hwnd) && NativeMethods.IsWindowVisible(hwnd) && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(40, cancellationToken).ConfigureAwait(false);
        return !NativeMethods.IsWindow(hwnd) || !NativeMethods.IsWindowVisible(hwnd);
    }

    internal static AutomationObservation? ResolveDialogDismissControl(
        IReadOnlyList<AutomationObservation> controls,
        RectI dialogBounds)
    {
        ArgumentNullException.ThrowIfNull(controls);
        return controls
            .Where(control => control.IsEnabled && !control.IsOffscreen &&
                              NormalizeAutomationControlType(control.ControlType) == "Button" &&
                              control.Bounds.Width > 0 && control.Bounds.Height > 0 &&
                              IsInsideDialog(control.Bounds, dialogBounds))
            .Select(control => (Control: control, Rank: DialogDismissRank(control)))
            .Where(candidate => candidate.Rank < int.MaxValue)
            .OrderBy(candidate => candidate.Rank)
            .ThenByDescending(candidate => candidate.Control.Bounds.Y)
            .ThenByDescending(candidate => candidate.Control.Bounds.X)
            .Select(candidate => candidate.Control)
            .FirstOrDefault();
    }

    private static int DialogDismissRank(AutomationObservation control)
    {
        var name = NormalizeDialogAction(control.Name);
        var automationId = NormalizeDialogAction(control.AutomationId);
        if (CancelDialogActions.Contains(name) || CancelDialogActions.Contains(automationId))
            return 0;
        if (CloseDialogActions.Contains(name) || CloseDialogActions.Contains(automationId))
            return 1;
        return int.MaxValue;
    }

    private static string NormalizeDialogAction(string? value) =>
        new((value ?? string.Empty)
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());

    private static bool IsInsideDialog(RectI candidate, RectI dialog) =>
        candidate.X >= dialog.X && candidate.Y >= dialog.Y &&
        candidate.X + candidate.Width <= dialog.X + dialog.Width &&
        candidate.Y + candidate.Height <= dialog.Y + dialog.Height;

    private static readonly HashSet<string> CancelDialogActions = new(StringComparer.Ordinal)
    {
        "cancel", "idcancel", "отмена", "annuler", "abbrechen", "cancelar", "annulla", "cancelar"
    };

    private static readonly HashSet<string> CloseDialogActions = new(StringComparer.Ordinal)
    {
        "close", "closebutton", "закрыть", "fermer", "schließen", "schliessen", "cerrar", "chiudi", "fechar"
    };

    internal static bool HasMeaningfulDialogContent(IReadOnlyList<AutomationObservation> controls)
    {
        var visible = AutomationObservationVisibility.FilterEffectivelyVisible(controls);
        return visible.Count > 1 && visible.Any(control =>
            NormalizeAutomationControlType(control.ControlType) is
                "Button" or "CheckBox" or "ComboBox" or "Edit" or "List" or "ListItem" or
                "RadioButton" or "Tab" or "TabItem" or "Tree" or "TreeItem");
    }

    private static string DialogFingerprint(WindowTarget dialog, IReadOnlyList<AutomationObservation> controls)
    {
        var builder = new StringBuilder(dialog.ClassName).Append('|')
            .Append(dialog.Bounds.Width).Append('x').Append(dialog.Bounds.Height).Append('|');
        foreach (var control in AutomationObservationVisibility.FilterEffectivelyVisible(controls)
                     .OrderBy(control => control.Bounds.Y).ThenBy(control => control.Bounds.X)
                     .ThenBy(control => control.ControlType, StringComparer.Ordinal))
        {
            builder.Append(control.AutomationId).Append('|').Append(control.Name).Append('|')
                .Append(NormalizeAutomationControlType(control.ControlType)).Append('@')
                .Append(control.Bounds.X - dialog.Bounds.X).Append(',')
                .Append(control.Bounds.Y - dialog.Bounds.Y).Append(',')
                .Append(control.Bounds.Width).Append(',').Append(control.Bounds.Height).Append(';');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }

    private static string NormalizeAutomationControlType(string value)
    {
        const string prefix = "ControlType.";
        return value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? value[prefix.Length..] : value;
    }

    public long BeginManualEpisode(DateTimeOffset armedUtc, int expectedClickCount)
    {
        if (!_startedCapture || _finalized) throw new InvalidOperationException("Recording is not active.");
        if (expectedClickCount is < 1 or > 2) throw new ArgumentOutOfRangeException(nameof(expectedClickCount));
        var sequence = _episodes.Count + 1L;
        _episodes.Add(new(sequence, armedUtc, null, null, expectedClickCount, "waiting"));
        return sequence;
    }

    public void MarkManualActionObserved(long episodeSequence, DateTimeOffset actionObservedUtc)
    {
        var index = _episodes.FindIndex(item => item.Sequence == episodeSequence);
        if (index < 0) throw new InvalidOperationException("Manual episode is not active.");
        _episodes[index] = _episodes[index] with { ActionObservedUtc = actionObservedUtc, ObservationStatus = "capturing" };
    }

    public void MarkManualEpisodeSettled(long episodeSequence, DateTimeOffset streamsSettledUtc, string observationStatus)
    {
        var index = _episodes.FindIndex(item => item.Sequence == episodeSequence);
        if (index < 0) throw new InvalidOperationException("Manual episode is not active.");
        _episodes[index] = _episodes[index] with { StreamsSettledUtc = streamsSettledUtc, ObservationStatus = observationStatus };
    }

    public IReadOnlyList<RectI> ResolveEpisodeHighlightBounds(long episodeSequence, FrameObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (!_startedCapture) throw new InvalidOperationException("Recording has not started.");
        DrainInput();
        var summary = _episodes.FirstOrDefault(item => item.Sequence == episodeSequence);
        if (summary is null || !summary.ActionObservedUtc.HasValue) return [];
        var pointerUps = _events
            .Where(item => item.Kind == InputEventKind.PointerUp &&
                           item.TimestampUtc >= summary.ArmedUtc &&
                           item.TimestampUtc <= summary.ActionObservedUtc.Value.AddMilliseconds(250))
            .OrderBy(item => item.TimestampUtc)
            .ThenBy(item => item.Sequence)
            .ToArray();
        return ManualRecordingHighlightResolver.Resolve(observation, pointerUps);
    }

    public async Task<bool> ActivateTargetAsync(CancellationToken cancellationToken)
    {
        if (!_startedCapture || _finalized) throw new InvalidOperationException("Recording is not active.");
        RevalidateTarget();
        // Some Delphi/Win32 programs keep an invisible TApplication window as
        // GA_ROOTOWNER. That handle defines scope, but the selected visible form
        // is the handle that can actually receive foreground and keyboard focus.
        var target = (nint)_target.Hwnd;
        return await ActivateWindowAsync(target, cancellationToken).ConfigureAwait(false);
    }

    public bool IsTargetForeground()
    {
        if (!_startedCapture || _finalized) return false;
        return WindowCatalog.GetRootOwnerHandle(NativeMethods.GetForegroundWindow()).ToInt64() == _target.RootOwnerHwnd;
    }

    public async Task<bool> TryInvokeControlAsync(AutomationObservation control, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(control);
        if (!_startedCapture || _finalized) throw new InvalidOperationException("Recording is not active.");
        RevalidateTarget();
        if (!await ActivateTargetAsync(cancellationToken).ConfigureAwait(false))
            return false;
        await Task.Delay(80, cancellationToken).ConfigureAwait(false);
        return ProgrammaticControlInvoker.TryInvoke(_target, control);
    }

    public async Task<bool> TryClickBoundsAsync(RectI bounds, int clickCount, CancellationToken cancellationToken)
    {
        if (!_startedCapture || _finalized) throw new InvalidOperationException("Recording is not active.");
        if (clickCount < 1) throw new ArgumentOutOfRangeException(nameof(clickCount));
        RevalidateTarget();
        if (!await ActivateTargetAsync(cancellationToken).ConfigureAwait(false))
            return false;
        await Task.Delay(80, cancellationToken).ConfigureAwait(false);
        return ProgrammaticControlInvoker.TryClickCenter(bounds, clickCount);
    }

    public async Task<bool> TryClickControlAsync(AutomationObservation control, int clickCount, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(control);
        if (!_startedCapture || _finalized) throw new InvalidOperationException("Recording is not active.");
        if (clickCount < 1) throw new ArgumentOutOfRangeException(nameof(clickCount));
        RevalidateTarget();
        if (!await ActivateTargetAsync(cancellationToken).ConfigureAwait(false))
            return false;
        await Task.Delay(80, cancellationToken).ConfigureAwait(false);
        return ProgrammaticControlInvoker.TryClickObservedControl(control, clickCount);
    }

    public async Task<bool> TryScrollBoundsAsync(RectI bounds, int wheelDelta, CancellationToken cancellationToken)
    {
        if (!_startedCapture || _finalized) throw new InvalidOperationException("Recording is not active.");
        if (wheelDelta == 0) throw new ArgumentOutOfRangeException(nameof(wheelDelta));
        RevalidateTarget();
        if (!await ActivateTargetAsync(cancellationToken).ConfigureAwait(false))
            return false;
        await Task.Delay(80, cancellationToken).ConfigureAwait(false);
        return ProgrammaticControlInvoker.TryScroll(bounds, wheelDelta);
    }

    public bool RememberControllerWindow() => RememberControllerWindow(NativeMethods.GetForegroundWindow());

    internal bool RememberControllerWindow(nint foregroundWindow)
    {
        if (!_startedCapture || _finalized) throw new InvalidOperationException("Recording is not active.");
        var foreground = WindowCatalog.GetRootOwnerHandle(foregroundWindow);
        if (foreground == 0 || foreground == (nint)_target.RootOwnerHwnd || !NativeMethods.IsWindow(foreground))
        {
            _controllerWindow = 0;
            return false;
        }
        _controllerWindow = foreground;
        return true;
    }

    internal nint RememberedControllerWindow => _controllerWindow;

    public async Task<bool> ActivateControllerWindowAsync(CancellationToken cancellationToken)
    {
        if (_controllerWindow == 0 || !NativeMethods.IsWindow(_controllerWindow)) return false;
        return await ActivateWindowAsync(_controllerWindow, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<bool> ActivateWindowAsync(nint target, CancellationToken cancellationToken)
    {
        if (target == 0 || !NativeMethods.IsWindow(target)) return false;
        var targetRoot = WindowCatalog.GetRootOwnerHandle(target);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(2);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TryActivateWindow(target);
            var foreground = NativeMethods.GetForegroundWindow();
            if (IsActivationForegroundMatch(target, foreground, targetRoot))
                return true;
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
        return false;
    }

    internal static bool IsActivationForegroundMatch(nint target, nint foreground, nint targetRoot = 0)
    {
        if (target == 0 || foreground == 0) return false;
        targetRoot = targetRoot == 0 ? WindowCatalog.GetRootOwnerHandle(target) : targetRoot;
        return foreground == target ||
               targetRoot != 0 && WindowCatalog.GetRootOwnerHandle(foreground) == targetRoot;
    }

    private static void TryActivateWindow(nint target)
    {
        _ = NativeMethods.PeekMessageW(out _, 0, 0, 0, NativeMethods.PmNoRemove);
        var currentThread = NativeMethods.GetCurrentThreadId();
        var foregroundThread = NativeMethods.GetWindowThreadProcessId(NativeMethods.GetForegroundWindow(), out _);
        var targetThread = NativeMethods.GetWindowThreadProcessId(target, out _);
        var attachedForeground = foregroundThread != 0 && foregroundThread != currentThread &&
            NativeMethods.AttachThreadInput(currentThread, foregroundThread, true);
        var attachedTarget = targetThread != 0 && targetThread != currentThread && targetThread != foregroundThread &&
            NativeMethods.AttachThreadInput(currentThread, targetThread, true);
        try
        {
            RestoreWindowIfMinimized(target);
            NativeMethods.SetWindowPos(target, NativeMethods.HwndTopMost, 0, 0, 0, 0,
                NativeMethods.SwpNoMove | NativeMethods.SwpNoSize | NativeMethods.SwpNoOwnerZOrder);
            NativeMethods.SetWindowPos(target, NativeMethods.HwndNoTopMost, 0, 0, 0, 0,
                NativeMethods.SwpNoMove | NativeMethods.SwpNoSize | NativeMethods.SwpNoOwnerZOrder);
            NativeMethods.BringWindowToTop(target);
            NativeMethods.SetForegroundWindow(target);
            NativeMethods.SetActiveWindow(target);
            NativeMethods.SetFocus(target);
        }
        finally
        {
            if (attachedTarget) NativeMethods.AttachThreadInput(currentThread, targetThread, false);
            if (attachedForeground) NativeMethods.AttachThreadInput(currentThread, foregroundThread, false);
        }
    }

    private static void RestoreWindowIfMinimized(nint target)
    {
        var placement = new NativeMethods.WindowPlacement { Length = (uint)Marshal.SizeOf<NativeMethods.WindowPlacement>() };
        if (NativeMethods.GetWindowPlacement(target, ref placement) == 0) return;
        if (placement.ShowCmd == NativeMethods.SwShowMinimized)
            NativeMethods.ShowWindow(target, NativeMethods.SwRestore);
    }

    internal async Task<T> CaptureScreenshotAsync<T>(
        Func<CancellationToken, Task<T>> capture,
        CancellationToken screenshotCancellationToken)
    {
        var overlayHidden = false;
        if (_beforeScreenshotCapture is not null)
        {
            await _beforeScreenshotCapture(screenshotCancellationToken).ConfigureAwait(false);
            overlayHidden = true;
        }

        try
        {
            return await capture(screenshotCancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (overlayHidden)
                _afterScreenshotCapture?.Invoke();
        }
    }

    private async Task<FrameObservation> ObserveFrameAsync(
        string trigger,
        CancellationToken cancellationToken,
        FrameCaptureOptions options,
        bool persistFrame,
        bool captureScreenshot,
        TimeSpan automationTimeout)
    {
        RevalidateTarget();
        if (persistFrame)
            DrainInput();

        var sequence = persistFrame ? Interlocked.Increment(ref _frameSequence) : 0L;
        var additionalTargets = (options.AdditionalScopedWindowHwnds ?? [])
            .Where(hwnd => hwnd != _target.RootOwnerHwnd && WindowCatalog.IsSameProcessWindow(_target, hwnd))
            .Select(hwnd =>
            {
                try { return WindowCatalog.Resolve(hwnd); }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return null; }
            })
            .Where(target => target is not null)
            .Cast<WindowTarget>();
        var ownedTargets = WindowCatalog.ListScopedWindows(_target);
        var discoveredTargets = ownedTargets
            .Where(target => target.Hwnd == _target.RootOwnerHwnd)
            .Concat(additionalTargets)
            .Concat(ownedTargets.Where(target => target.Hwnd != _target.RootOwnerHwnd))
            .DistinctBy(target => target.Hwnd)
            .ToArray();
        var scopedTargets = discoveredTargets.Take(WindowSnapshotCapture.MaxScopedWindows).ToArray();
        if (persistFrame && discoveredTargets.Length > scopedTargets.Length)
        {
            lock (_stateGate)
            {
                _hasPartialCapture = true;
                _health.Add(new(DateTimeOffset.UtcNow, "scope", "window-limit", "Owned-window scope exceeded the bounded recorder limit; the frame is partial.", true));
            }
        }

        var scopedWindows = scopedTargets.Select(WindowSnapshotCapture.Observe).ToArray();
        var window = options.PrimaryWindowHwnd is { } primaryWindowHwnd
            ? scopedWindows.FirstOrDefault(item => item.Hwnd == primaryWindowHwnd)
            : null;
        window ??= scopedWindows.FirstOrDefault(item => item.Hwnd == _target.RootOwnerHwnd) ?? WindowSnapshotCapture.Observe(_target);
        var frameEntry = "";
        RectI? screenshotBounds = null;
        IReadOnlyList<AutomationObservation> automation = [];
        var timedOut = false;
        var status = "not-requested";
        var automationCollected = false;

        async Task CollectAutomationAsync()
        {
            if (automationCollected) return;
            automationCollected = true;
            if (options.AutomationOverride is not null)
            {
                automation = options.AutomationOverride;
                timedOut = options.AutomationTimedOutOverride;
                status = options.AutomationStatusOverride ?? "ok";
            }
            else if (options.IncludeAutomation)
            {
                var maxAutomationNodes = Math.Clamp(options.MaxAutomationNodes ?? RecordingContractLimits.MaxControlsPerFrame,
                    1, RecordingContractLimits.MaxControlsPerFrame);
                (automation, timedOut, status) = await CollectAutomationExclusiveAsync(
                    () => options.PopupAutomation && options.AutomationWindowHwnd is { } popupHwnd
                        ? _automation.CollectPopupAsync(
                            _target, popupHwnd, automationTimeout, maxAutomationNodes, cancellationToken)
                        : _automation.CollectAsync(
                            _target, automationTimeout, maxAutomationNodes, cancellationToken,
                            options.AutomationWindowHwnd ?? _target.Hwnd),
                    cancellationToken).ConfigureAwait(false);
                if (persistFrame && status != "ok")
                {
                    lock (_stateGate)
                    {
                        _hasPartialCapture = true;
                        _health.Add(new(DateTimeOffset.UtcNow, "uia", status,
                            "UI Automation returned a bounded partial result.", true));
                    }
                }
            }
        }

        if (options.AutomationBeforeScreenshot)
            await CollectAutomationAsync().ConfigureAwait(false);

        if (captureScreenshot)
        {
            var screenshotHwnds = options.ScreenshotWindowHwnds is { Count: > 0 }
                ? options.ScreenshotWindowHwnds.ToHashSet()
                : scopedTargets.Select(target => target.Hwnd).ToHashSet();
            var screenshotTargets = scopedTargets
                .Where(target => screenshotHwnds.Contains(target.Hwnd) && WindowSnapshotCapture.IsCapturable(target))
                .ToArray();
            if (screenshotTargets.Length == 0)
                throw new InvalidOperationException("No requested screenshot windows are available.");
            var observationsByHwnd = scopedWindows.ToDictionary(item => item.Hwnd);
            var capturedTargets = screenshotTargets.Select(target =>
                target with { Bounds = observationsByHwnd[target.Hwnd].Bounds }).ToArray();
            var candidateScreenshotBounds = new RectI(
                capturedTargets.Min(target => target.Bounds.X),
                capturedTargets.Min(target => target.Bounds.Y),
                capturedTargets.Max(target => target.Bounds.X + target.Bounds.Width) - capturedTargets.Min(target => target.Bounds.X),
                capturedTargets.Max(target => target.Bounds.Y + target.Bounds.Height) - capturedTargets.Min(target => target.Bounds.Y));
            try
            {
                WindowSnapshotCapture.CaptureResult capture;
                if (options.PreparedScreenshot is { } prepared)
                {
                    capture = new(prepared.Png, prepared.Method, prepared.UsedFallback, prepared.IsPartial);
                    screenshotBounds = prepared.Bounds;
                }
                else
                {
                    capture = options.WaitForDeferredVisualContent
                        ? await CaptureStableScreenshotAsync(
                            capturedTargets,
                            options.ScreenshotTimeout ?? TimeSpan.FromMilliseconds(700),
                            waitForDeferredVisualContent: true,
                            cancellationToken: cancellationToken,
                            preferScreenBounds: options.PreferScreenBoundsScreenshot,
                            contentReady: options.RequireRenderedAutomationContent
                                ? png => WindowSnapshotCapture.HasRenderedAutomationContentPng(
                                    png,
                                    candidateScreenshotBounds,
                                    options.AutomationOverride ?? automation)
                                : null).ConfigureAwait(false)
                        : await CaptureSingleScreenshotAsync().ConfigureAwait(false);
                    screenshotBounds = candidateScreenshotBounds;

                    async Task<WindowSnapshotCapture.CaptureResult> CaptureSingleScreenshotAsync()
                    {
                        using var screenshotCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        if (options.ScreenshotTimeout is { } screenshotTimeout)
                            screenshotCancellation.CancelAfter(screenshotTimeout);
                        return await CaptureScreenshotAsync(
                            token => WindowSnapshotCapture.CapturePngAsync(
                                capturedTargets,
                                token,
                                options.PreferScreenBoundsScreenshot),
                            screenshotCancellation.Token).ConfigureAwait(false);
                    }
                }
                var png = capture.Png;
                if (persistFrame)
                {
                    lock (_stateGate)
                    {
                        _hasPartialCapture |= capture.IsPartial;
                        _health.Add(new(DateTimeOffset.UtcNow, "screenshot", capture.Method,
                            capture.UsedFallback ? "Native window capture was unavailable; the scoped fallback was used." :
                            capture.IsPartial ? "Scoped native window capture reached a cumulative limit; uncaptured regions are blank." : "Scoped native window capture succeeded.", true));
                    }
                }
                if (png.Length > 16 * 1024 * 1024) throw new InvalidOperationException("Encoded frame exceeds quota.");
                frameEntry = $"raw/frames/frame-{sequence:D6}.png";
                lock (_stateGate)
                {
                    if (_finalized) throw new OperationCanceledException("Recording finalized before the delta was persisted.");
                    _writer.WriteBytes(frameEntry, png);
                    _latestScreenshotSequence = sequence;
                    _latestScreenshotPng = png;
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && options.ScreenshotTimeout.HasValue)
            {
                lock (_stateGate)
                {
                    _hasPartialCapture = true;
                    _health.Add(new(DateTimeOffset.UtcNow, "screenshot", "timeout", "The bounded delta screenshot timed out and was skipped.", true));
                }
            }
            catch (Exception ex) when (persistFrame && WindowSnapshotCapture.IsRecoverableCaptureFailure(ex))
            {
                lock (_stateGate)
                {
                    _hasPartialCapture = true;
                    _health.Add(new(DateTimeOffset.UtcNow, "screenshot", "unavailable", "Scoped window frame could not be captured.", true));
                }
            }
        }

        await CollectAutomationAsync().ConfigureAwait(false);

        var observation = new FrameObservation(sequence, DateTimeOffset.UtcNow, frameEntry, window, automation, timedOut, status, trigger, scopedWindows,
            options.EpisodeSequence, options.PostTriggerDelayMs, options.CapturePhase, options.ActionObservedUtc,
            options.ObservationScope, options.ObservedWindowHwnds, screenshotBounds, options.BaseFrameSequence,
            options.InteractionSource, options.InteractionId, options.ExtractionOverride);
        if (!persistFrame)
            return observation;

        lock (_stateGate)
        {
            if (_finalized) throw new OperationCanceledException("Recording finalized before the delta was persisted.");
            _frames.Add(new(observation.Sequence, observation.TimestampUtc, observation.Trigger, observation.EpisodeSequence,
                observation.CapturePhase, observation.AutomationStatus, StructuralFingerprint(observation)));
            _frameObservations.Add(observation);
            _writer.WriteJson($"raw/observations/frame-{sequence:D6}.json", observation);
        }
        return observation;
    }

    internal bool TryGetFrameScreenshot(long sequence, out byte[] png)
    {
        lock (_stateGate)
        {
            if (_latestScreenshotSequence == sequence && _latestScreenshotPng is not null)
            {
                png = _latestScreenshotPng;
                return true;
            }
        }

        png = [];
        return false;
    }

    public void Complete()
    {
        RecordingOutcome outcome;
        lock (_stateGate) outcome = _hasPartialCapture ? RecordingOutcome.Partial : RecordingOutcome.Complete;
        Finalize(outcome, retainOnCancel: true);
    }
    public void CompletePartial() => Finalize(RecordingOutcome.Partial, retainOnCancel: true);
    public void Cancel(bool retain) => Finalize(RecordingOutcome.Cancelled, retain);
    public void Fail() => Finalize(LatestFrameSequence > 0 ? RecordingOutcome.Partial : RecordingOutcome.Failed, retainOnCancel: true);

    private void Finalize(RecordingOutcome outcome, bool retainOnCancel)
    {
        _input.Dispose();
        lock (_stateGate)
        {
            if (_finalized) return;
            _finalized = true;
            DrainInputUnsafe();
            var orderedEvents = _events.Select((item, originalIndex) => (item, originalIndex))
                .OrderBy(x => x.item.TimestampUtc).ThenBy(x => x.originalIndex).ToArray();
            var normalizedEvents = orderedEvents.Select((x, index) => x.item with { Sequence = index + 1L }).ToArray();
            _events.Clear();
            _events.AddRange(normalizedEvents);
            RemapInteractionInputSequencesUnsafe(orderedEvents, normalizedEvents);
            AppendUnmodeledInputInteractionsUnsafe();
            var orderedInteractions = _interactions.OrderBy(item => item.StartedUtc)
                .ThenBy(item => item.Sequence)
                .Select((item, index) => item with { Sequence = index + 1L })
                .ToArray();
            _interactions.Clear();
            _interactions.AddRange(orderedInteractions);
            var ended = DateTimeOffset.UtcNow;
            var sessionId = Path.GetFileNameWithoutExtension(_outputPath);
            if (string.IsNullOrWhiteSpace(sessionId)) sessionId = Guid.NewGuid().ToString("N");
            _writer.WriteText("raw/input-events.jsonl", ToJsonLines(_events));
            _writer.WriteText("raw/interactions.jsonl", ToJsonLines(_interactions));
            _writer.WriteText("raw/capture-health.jsonl", ToJsonLines(_health));
            _writer.WriteJson("derived/statebook.json", BuildStatebook());
            var manifest = new RecordingManifest(FormatVersions.RecordingBundle, FormatVersions.Tool, sessionId, _started, ended, outcome,
                new(_target.Hwnd, _target.RootOwnerHwnd, _target.ProcessId, _target.ProcessName, _target.ProcessStartedUtc,
                    ProductVersion: _target.ProductVersion, OriginalFilename: _target.OriginalFilename,
                    CompanyName: _target.CompanyName, ProductName: _target.ProductName),
                new(), new(retainOnCancel), true, _events.Count, _frames.Count, Files: _writer.DescribeEntries());
            _writer.WriteJson("manifest.json", manifest);
            if (outcome != RecordingOutcome.Cancelled || retainOnCancel)
                _writer.Complete(_outputPath);
        }
    }

    private DerivedStatebook BuildStatebook()
    {
        var episodes = new List<Episode>(_episodes.Count);
        foreach (var summary in _episodes.OrderBy(item => item.Sequence))
        {
            var members = _frames.Where(frame => frame.EpisodeSequence == summary.Sequence).OrderBy(frame => frame.Sequence).ToArray();
            if (members.Length == 0) continue;
            var prior = _frames.LastOrDefault(frame => frame.Sequence < members[0].Sequence &&
                !string.Equals(frame.CapturePhase, "post-trigger", StringComparison.Ordinal));
            var settled = members.LastOrDefault(frame => string.Equals(frame.CapturePhase, "materialized", StringComparison.Ordinal)) ?? members[^1];
            var input = _events.LastOrDefault(x => x.TimestampUtc >= summary.ArmedUtc &&
                (!summary.ActionObservedUtc.HasValue || x.TimestampUtc <= summary.ActionObservedUtc.Value) && x.Kind == InputEventKind.PointerUp);
            episodes.Add(new($"episode-{summary.Sequence:D6}", input?.Sequence, prior?.Sequence ?? members[0].Sequence,
                settled.Sequence, settled.Trigger, input is null ? "observed" : "input-correlated", summary.ArmedUtc,
                summary.ActionObservedUtc, summary.StreamsSettledUtc, summary.ExpectedClickCount, summary.ObservationStatus));
        }
        var representatives = _frames
            .Where(frame => frame.EpisodeSequence is null || string.Equals(frame.CapturePhase, "materialized", StringComparison.Ordinal))
            .Select(frame => frame.Sequence)
            .ToHashSet();
        foreach (var group in _frames.Where(frame => frame.EpisodeSequence.HasValue).GroupBy(frame => frame.EpisodeSequence!.Value))
        {
            var settled = group.LastOrDefault(frame => string.Equals(frame.CapturePhase, "materialized", StringComparison.Ordinal));
            var transient = group.LastOrDefault(frame => string.Equals(frame.CapturePhase, "post-trigger", StringComparison.Ordinal) &&
                !string.Equals(frame.AutomationStatus, "not-requested", StringComparison.Ordinal));
            if (transient is not null && (settled is null || !string.Equals(transient.StructuralFingerprint, settled.StructuralFingerprint, StringComparison.Ordinal)))
                representatives.Add(transient.Sequence);
        }
        foreach (var interaction in _interactions)
        {
            representatives.Add(interaction.SourceFrameSequence);
            foreach (var result in interaction.ResultFrameSequences) representatives.Add(result);
        }
        return new("statebook/1", representatives.Order().ToArray(), episodes);
    }

    private static string StructuralFingerprint(FrameObservation observation)
    {
        var builder = new StringBuilder();
        foreach (var window in (observation.ScopedWindows ?? [observation.Window])
                     .OrderBy(item => item.ClassName, StringComparer.Ordinal)
                     .ThenBy(item => item.Bounds.X).ThenBy(item => item.Bounds.Y))
            builder.Append(window.ClassName).Append('|').Append(window.OwnerHwnd == 0 ? "root" : "owned").Append('|')
                .Append(window.Bounds.X).Append(',').Append(window.Bounds.Y).Append(',')
                .Append(window.Bounds.Width).Append(',').Append(window.Bounds.Height).Append(';');
        foreach (var control in observation.Automation
                     .OrderBy(item => item.WindowHwnd).ThenBy(item => item.RuntimeId, StringComparer.Ordinal))
            builder.Append(control.WindowHwnd).Append('|').Append(control.RuntimeId).Append('|')
                .Append(control.AutomationId).Append('|').Append(control.ControlType).Append('|')
                .Append(control.ClassName).Append('|').Append(control.Bounds.X).Append(',').Append(control.Bounds.Y).Append(',')
                .Append(control.Bounds.Width).Append(',').Append(control.Bounds.Height).Append(';');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }

    private void DrainInput()
    {
        lock (_stateGate) DrainInputUnsafe();
    }

    private void DrainInputUnsafe()
    {
        var remaining = Math.Max(0, MaxEvents - _events.Count);
        _events.AddRange(_input.Drain().Take(remaining));
        if (_input.DroppedEvents > _reportedDroppedEvents)
        {
            _reportedDroppedEvents = _input.DroppedEvents;
            _health.Add(new(DateTimeOffset.UtcNow, "input", "quota-drop", "Input events exceeded the bounded queue.", true));
        }
    }

    private void RemapInteractionInputSequencesUnsafe(
        IReadOnlyList<(InputEvent item, int originalIndex)> orderedEvents,
        IReadOnlyList<InputEvent> normalizedEvents)
    {
        for (var interactionIndex = 0; interactionIndex < _interactions.Count; interactionIndex++)
        {
            var interaction = _interactions[interactionIndex];
            if (interaction.InputSequences.Count == 0) continue;
            var remapped = new List<long>(interaction.InputSequences.Count);
            var used = new HashSet<int>();
            foreach (var originalSequence in interaction.InputSequences)
            {
                var candidate = orderedEvents.Select((entry, index) => (entry, index))
                    .Where(value => value.entry.item.Sequence == originalSequence && !used.Contains(value.index))
                    .OrderBy(value => value.entry.item.TimestampUtc < interaction.StartedUtc ||
                                      value.entry.item.TimestampUtc > interaction.CompletedUtc ? 1 : 0)
                    .ThenBy(value => Math.Abs((value.entry.item.TimestampUtc - interaction.CompletedUtc).Ticks))
                    .FirstOrDefault();
                if (candidate.entry.item is null) continue;
                used.Add(candidate.index);
                remapped.Add(normalizedEvents[candidate.index].Sequence);
            }
            _interactions[interactionIndex] = interaction with { InputSequences = remapped.Distinct().Order().ToArray() };
        }
    }

    private void AppendUnmodeledInputInteractionsUnsafe()
    {
        if (_frameObservations.Count == 0 || _events.Count == 0) return;
        var referenced = _interactions.SelectMany(item => item.InputSequences).ToHashSet();
        var candidates = _events.Where(item => !referenced.Contains(item.Sequence) && item.Kind != InputEventKind.Marker)
            .OrderBy(item => item.Sequence).ToArray();

        for (var index = 0; index < candidates.Length; index++)
        {
            var current = candidates[index];
            if (current.Kind == InputEventKind.Wheel)
            {
                var group = new List<InputEvent> { current };
                while (index + 1 < candidates.Length && candidates[index + 1].Kind == InputEventKind.Wheel &&
                       candidates[index + 1].TimestampUtc - group[^1].TimestampUtc <= TimeSpan.FromMilliseconds(250))
                    group.Add(candidates[++index]);
                AppendDerivedInputInteractionUnsafe(group, InteractionGestureKind.Wheel,
                    InteractionActionKind.Scroll, "raw-wheel");
                continue;
            }

            if (current.Kind == InputEventKind.KeyDown)
            {
                var group = new List<InputEvent> { current };
                while (index + 1 < candidates.Length &&
                       candidates[index + 1].Kind is InputEventKind.KeyDown or InputEventKind.KeyUp &&
                       candidates[index + 1].TimestampUtc - group[^1].TimestampUtc <= TimeSpan.FromMilliseconds(650))
                    group.Add(candidates[++index]);
                AppendDerivedInputInteractionUnsafe(group, InteractionGestureKind.Keyboard,
                    InteractionActionKind.Unknown, "redacted-keyboard-group");
                continue;
            }

            if (current.Kind != InputEventKind.PointerDown) continue;
            var pointerUpIndex = Array.FindIndex(candidates, index + 1,
                item => item.Kind == InputEventKind.PointerUp &&
                        item.TimestampUtc - current.TimestampUtc <= TimeSpan.FromSeconds(5));
            if (pointerUpIndex < 0) continue;
            var pointerUp = candidates[pointerUpIndex];
            var distance = Math.Abs(pointerUp.X - current.X) + Math.Abs(pointerUp.Y - current.Y);
            if (distance < 8) continue;
            AppendDerivedInputInteractionUnsafe([current, pointerUp], InteractionGestureKind.Drag,
                InteractionActionKind.MoveResize, "raw-drag");
            index = pointerUpIndex;
        }
    }

    private void AppendDerivedInputInteractionUnsafe(
        IReadOnlyList<InputEvent> inputs,
        InteractionGestureKind gesture,
        InteractionActionKind action,
        string diagnosticCode)
    {
        if (inputs.Count == 0 || _interactions.Count >= RecordingContractLimits.MaxInteractions) return;
        var sourceFrame = _frameObservations.Where(frame => frame.TimestampUtc <= inputs[0].TimestampUtc)
            .OrderByDescending(frame => frame.TimestampUtc).FirstOrDefault();
        if (sourceFrame is null) return;
        var resultFrame = _frameObservations.Where(frame => frame.TimestampUtc >= inputs[^1].TimestampUtc && frame.Sequence != sourceFrame.Sequence)
            .OrderBy(frame => frame.TimestampUtc).FirstOrDefault();
        var sourceControl = gesture == InteractionGestureKind.Keyboard
            ? sourceFrame.Automation.FirstOrDefault(control => control.HasKeyboardFocus)
            : ResolveInputControl(sourceFrame.Automation, inputs[^1].X, inputs[^1].Y);
        if (gesture == InteractionGestureKind.Keyboard && sourceControl is not null)
        {
            var type = NormalizeAutomationControlType(sourceControl.ControlType);
            action = type is "Edit" or "ComboBox" ? InteractionActionKind.SetValue : InteractionActionKind.Unknown;
            diagnosticCode = action == InteractionActionKind.SetValue
                ? "redacted-text-input"
                : "keyboard-shortcut";
        }
        var outcome = resultFrame is null || sourceControl is null
            ? InteractionOutcome.Unobserved
            : StructuralFingerprint(sourceFrame) == StructuralFingerprint(resultFrame)
                ? InteractionOutcome.NoChange
                : InteractionOutcome.Succeeded;
        _interactions.Add(new(
            "interaction-" + Guid.NewGuid().ToString("N"),
            "user-input-" + inputs[0].Sequence,
            1,
            _interactions.Count + 1L,
            InteractionActor.User,
            gesture,
            action,
            sourceFrame.Sequence,
            sourceControl,
            inputs.Select(item => item.Sequence).Distinct().Order().ToArray(),
            resultFrame is null ? [] : [resultFrame.Sequence],
            inputs[0].TimestampUtc,
            inputs[^1].TimestampUtc,
            outcome,
            diagnosticCode));
    }

    private static AutomationObservation? ResolveInputControl(
        IEnumerable<AutomationObservation> controls,
        int x,
        int y) => controls
        .Where(control => !control.IsOffscreen && control.IsEnabled && control.Bounds.Width > 0 && control.Bounds.Height > 0 &&
                          x >= control.Bounds.X && x < (long)control.Bounds.X + control.Bounds.Width &&
                          y >= control.Bounds.Y && y < (long)control.Bounds.Y + control.Bounds.Height)
        .OrderBy(control => (long)control.Bounds.Width * control.Bounds.Height)
        .FirstOrDefault();

    private void RevalidateTarget()
    {
        var current = WindowCatalog.Resolve(_target.Hwnd);
        if (current.RootOwnerHwnd != _target.RootOwnerHwnd || current.ProcessId != _target.ProcessId ||
            current.ProcessStartedUtc != _target.ProcessStartedUtc)
            throw new InvalidOperationException("Selected target identity changed.");
    }

    private static string ToJsonLines<T>(IEnumerable<T> values)
    {
        var options = new JsonSerializerOptions(JsonDefaults.Options) { WriteIndented = false };
        return string.Concat(values.Select(x => JsonSerializer.Serialize(x, options) + "\n"));
    }

    public ValueTask DisposeAsync()
    {
        if (!_finalized && _startedCapture) Fail();
        _input.Dispose();
        _captureGate.Dispose();
        _automationGate.Dispose();
        _writer.Dispose();
        return ValueTask.CompletedTask;
    }

    private sealed record FrameSummary(
        long Sequence,
        DateTimeOffset TimestampUtc,
        string Trigger,
        long? EpisodeSequence,
        string CapturePhase,
        string AutomationStatus,
        string StructuralFingerprint);

    private sealed record ManualEpisodeSummary(
        long Sequence,
        DateTimeOffset ArmedUtc,
        DateTimeOffset? ActionObservedUtc,
        DateTimeOffset? StreamsSettledUtc,
        int ExpectedClickCount,
        string ObservationStatus);
}

internal sealed record PopupDeltaPreparation(PreparedPopupDelta? Delta, string Status);

public sealed record InteractionCaptureContext(
    string InteractionId,
    string OperationId,
    int Attempt,
    InteractionActor Actor,
    InteractionGestureKind Gesture,
    InteractionActionKind Action,
    long SourceFrameSequence,
    AutomationObservation? SourceControl,
    DateTimeOffset StartedUtc);

internal sealed record PreparedPopupDelta(
    WindowTarget Popup,
    WindowObservation RootWindow,
    IReadOnlyList<WindowObservation> ScopedWindows,
    RectI ScreenshotBounds,
    IReadOnlyList<AutomationObservation> Automation,
    string AutomationStatus,
    byte[] Png,
    string CaptureMethod,
    bool UsedFallback,
    bool IsPartial,
    bool ScopeWasTruncated,
    long BaseFrameSequence,
    bool AutomationTimedOut = false);

public sealed record FrameCaptureOptions(
    bool IncludeAutomation = true,
    long? EpisodeSequence = null,
    int? PostTriggerDelayMs = null,
    string CapturePhase = "materialized",
    DateTimeOffset? ActionObservedUtc = null,
    TimeSpan? AutomationTimeout = null,
    string ObservationScope = "full-root",
    IReadOnlyList<long>? ObservedWindowHwnds = null,
    IReadOnlyList<long>? ScreenshotWindowHwnds = null,
    long? BaseFrameSequence = null,
    long? AutomationWindowHwnd = null,
    int? MaxAutomationNodes = null,
    IReadOnlyList<AutomationObservation>? AutomationOverride = null,
    bool AutomationTimedOutOverride = false,
    string? AutomationStatusOverride = null,
    TimeSpan? ScreenshotTimeout = null,
    bool AutomationBeforeScreenshot = false,
    bool PopupAutomation = false,
    IReadOnlyList<long>? AdditionalScopedWindowHwnds = null,
    long? PrimaryWindowHwnd = null,
    AutomationObservation? InteractionSource = null,
    string? InteractionId = null,
    bool PreferScreenBoundsScreenshot = false,
    AdaptiveExtractionSnapshot? ExtractionOverride = null,
    PreparedFrameScreenshot? PreparedScreenshot = null,
    bool WaitForDeferredVisualContent = false,
    bool RequireRenderedAutomationContent = false);

public sealed record PreparedFrameScreenshot(
    byte[] Png,
    RectI Bounds,
    string Method,
    bool UsedFallback,
    bool IsPartial = false);

internal static class ManualRecordingHighlightResolver
{
    private const int FallbackMarkerSize = 18;
    private static readonly HashSet<string> InteractiveControlTypes =
    [
        "Button", "CheckBox", "ComboBox", "DataItem", "Edit", "Hyperlink", "ListItem",
        "MenuItem", "RadioButton", "ScrollBar", "Slider", "Spinner", "SplitButton", "TabItem",
        "Thumb", "TreeItem"
    ];
    private static readonly HashSet<string> ContainerControlTypes =
    [
        "Document", "Group", "List", "Pane", "Tree", "Window"
    ];

    public static IReadOnlyList<RectI> Resolve(FrameObservation observation, IReadOnlyList<InputEvent> pointerUps)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(pointerUps);
        if (pointerUps.Count == 0) return [];

        var results = new List<RectI>(pointerUps.Count);
        foreach (var pointerUp in pointerUps)
        {
            var highlight = ResolveSingle(observation, pointerUp);
            if (!results.Contains(highlight))
                results.Add(highlight);
        }

        return results;
    }

    private static RectI ResolveSingle(FrameObservation observation, InputEvent pointerUp)
    {
        var control = ResolveControl(observation, pointerUp);
        if (control is not null) return control.Bounds;

        var window = ResolveWindow(observation, pointerUp);
        if (window is not null) return window.Bounds;

        return new(pointerUp.X - FallbackMarkerSize / 2, pointerUp.Y - FallbackMarkerSize / 2, FallbackMarkerSize, FallbackMarkerSize);
    }

    private static AutomationObservation? ResolveControl(FrameObservation observation, InputEvent pointerUp)
    {
        var targetWindow = ResolveScopedWindow(observation, pointerUp);
        var candidates = observation.Automation
            .Where(control => !control.IsOffscreen &&
                              control.Bounds.Width > 0 &&
                              control.Bounds.Height > 0 &&
                              Contains(control.Bounds, pointerUp.X, pointerUp.Y))
            .ToArray();
        if (candidates.Length == 0) return null;

        if (targetWindow is not null)
        {
            var sameWindow = candidates
                .Where(control => control.WindowHwnd == 0 || control.WindowHwnd == targetWindow.Hwnd)
                .ToArray();
            if (sameWindow.Length > 0) candidates = sameWindow;
        }

        return candidates
            .OrderBy(ControlRank)
            .ThenBy(control => Area(control.Bounds))
            .ThenBy(control => DistanceToCenterSquared(control.Bounds, pointerUp.X, pointerUp.Y))
            .FirstOrDefault(control => !ShouldSuppressControlHighlight(control, targetWindow));
    }

    private static WindowObservation? ResolveWindow(FrameObservation observation, InputEvent pointerUp)
    {
        var targetWindow = ResolveScopedWindow(observation, pointerUp);
        if (targetWindow is null) return null;

        // If we could not correlate the click to a concrete control and only the root window matches,
        // prefer a point marker over tinting the entire app surface.
        if ((targetWindow.Hwnd == observation.Window.Hwnd && targetWindow.OwnerHwnd == 0) ||
            targetWindow.Bounds.Width > 720 || targetWindow.Bounds.Height > 520)
            return null;

        return targetWindow;
    }

    private static int ControlRank(AutomationObservation control)
    {
        var controlType = NormalizeControlType(control.ControlType);
        if (IsInteractive(controlType, control)) return 0;
        if (string.Equals(controlType, "Text", StringComparison.OrdinalIgnoreCase)) return 2;
        if (ContainerControlTypes.Contains(controlType)) return 3;
        return 1;
    }

    private static bool ShouldSuppressControlHighlight(AutomationObservation control, WindowObservation? targetWindow)
    {
        if (targetWindow is null) return false;

        var controlType = NormalizeControlType(control.ControlType);
        if (control.Bounds.Width > 900 || control.Bounds.Height > 620 || Area(control.Bounds) > 420_000)
            return true;
        if (!OccupiesMostOfWindow(control.Bounds, targetWindow.Bounds))
            return false;

        // If a control fills most of the target window, it is usually a worksheet/list/document
        // surface rather than the specific thing the user clicked. Only keep clearly actionable
        // item-sized control types; otherwise prefer a smaller control, the popup window, or a
        // point marker over tinting the whole app.
        return !InteractiveControlTypes.Contains(controlType);
    }

    private static bool IsInteractive(string controlType, AutomationObservation control) =>
        (control.SupportedPatterns?.Count ?? 0) > 0 || InteractiveControlTypes.Contains(controlType);

    private static WindowObservation? ResolveScopedWindow(FrameObservation observation, InputEvent pointerUp)
    {
        var windows = (observation.ScopedWindows ?? [observation.Window])
            .Where(window => window.Bounds.Width > 0 &&
                             window.Bounds.Height > 0 &&
                             Contains(window.Bounds, pointerUp.X, pointerUp.Y))
            .ToArray();
        if (windows.Length == 0) return null;

        if (pointerUp.WindowFromPointHwnd != 0)
        {
            var exact = windows.Where(window => window.Hwnd == pointerUp.WindowFromPointHwnd).ToArray();
            if (exact.Length > 0) windows = exact;
            else if (pointerUp.RootOwnerHwnd != 0)
            {
                var sameScope = windows.Where(window => window.RootOwnerHwnd == pointerUp.RootOwnerHwnd).ToArray();
                if (sameScope.Length > 0) windows = sameScope;
            }
        }
        else if (pointerUp.RootOwnerHwnd != 0)
        {
            var sameScope = windows.Where(window => window.RootOwnerHwnd == pointerUp.RootOwnerHwnd).ToArray();
            if (sameScope.Length > 0) windows = sameScope;
        }

        return windows
            .OrderBy(window => Area(window.Bounds))
            .ThenBy(window => DistanceToCenterSquared(window.Bounds, pointerUp.X, pointerUp.Y))
            .FirstOrDefault();
    }

    private static bool Contains(RectI bounds, int x, int y) =>
        x >= bounds.X &&
        y >= bounds.Y &&
        x < (long)bounds.X + bounds.Width &&
        y < (long)bounds.Y + bounds.Height;

    private static long Area(RectI bounds) => Math.Max(1L, (long)bounds.Width * bounds.Height);

    private static bool OccupiesMostOfWindow(RectI bounds, RectI windowBounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0 || windowBounds.Width <= 0 || windowBounds.Height <= 0)
            return false;

        var widthRatio = bounds.Width / (double)windowBounds.Width;
        var heightRatio = bounds.Height / (double)windowBounds.Height;
        var coverage = Area(bounds) / (double)Area(windowBounds);
        return widthRatio >= 0.75 && heightRatio >= 0.75 && coverage >= 0.60;
    }

    private static string NormalizeControlType(string? value)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        const string prefix = "ControlType.";
        return trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? trimmed[prefix.Length..]
            : trimmed;
    }

    private static long DistanceToCenterSquared(RectI bounds, int x, int y)
    {
        var centerX = bounds.X + bounds.Width / 2.0;
        var centerY = bounds.Y + bounds.Height / 2.0;
        var dx = centerX - x;
        var dy = centerY - y;
        return (long)Math.Round(dx * dx + dy * dy);
    }
}
