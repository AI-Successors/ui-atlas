using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using UiAtlas.Core.Contracts;

namespace UiAtlas.Core.Recording.Windows;

public sealed class AdaptiveCaptureCoordinator : IAsyncDisposable
{
    private static readonly TimeSpan PopupPollInterval = TimeSpan.FromMilliseconds(40);
    private static readonly TimeSpan PopupCaptureCompletionWindow = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan PopupMaterializationWindow = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan PopupBoundsStableWindow = TimeSpan.FromMilliseconds(60);
    private static readonly TimeSpan PopupContentRetryWindow = TimeSpan.FromMilliseconds(5_000);
    private static readonly TimeSpan TabSettle = TimeSpan.FromMilliseconds(180);
    private static readonly TimeSpan ControlAutomationTimeout = TimeSpan.FromMilliseconds(650);
    // This is a worker deadline, not a mandatory delay. Successful Office reads
    // return immediately; the larger ceiling permits the A/B readiness transaction
    // to finish under provider contention instead of killing a valid second read.
    private static readonly TimeSpan PopupAutomationTimeout = TimeSpan.FromMilliseconds(3_500);
    private static readonly TimeSpan TabAutomationTimeout = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan RootLegacyRecoveryTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RootNativeBandRecoveryTimeout = TimeSpan.FromSeconds(3);
    internal static readonly TimeSpan ManualRootRefreshTimeout = TimeSpan.FromSeconds(8);
    internal static readonly TimeSpan CancelledRootDrainTimeout = TimeSpan.FromMilliseconds(750);
    private const int PopupMaxNodes = 600;
    private const int TabMaxNodes = RecordingContractLimits.MaxControlsPerFrame;

    private readonly ManualRecordingSession _session;
    private readonly WindowTarget _target;
    private readonly Action<string>? _status;
    private readonly Func<long, CancellationToken,
        Task<(IReadOnlyList<AutomationObservation> Items, bool TimedOut, string Status)>>? _popupAutomationOverride;
    private readonly Func<long, CancellationToken,
        Task<(IReadOnlyList<AutomationObservation> Items, bool TimedOut, string Status)>>? _dialogAutomationOverride;
    private readonly Func<CancellationToken, Task<FrameObservation?>>? _rootCaptureAttemptOverride;
    private readonly Channel<CaptureRequest> _requests = Channel.CreateBounded<CaptureRequest>(new BoundedChannelOptions(128)
    {
        SingleReader = true,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.Wait
    });
    private readonly Channel<PopupRequest> _popupRequests = Channel.CreateBounded<PopupRequest>(new BoundedChannelOptions(64)
    {
        SingleReader = true,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.Wait
    });
    private readonly ConcurrentDictionary<long, byte> _pendingPopups = new();
    private readonly ConcurrentDictionary<long, byte> _capturedPopupsForCurrentClick = new();
    private readonly ConcurrentDictionary<long, byte> _seenPopupsForCurrentClick = new();
    private readonly ConcurrentDictionary<string, byte> _pendingTabs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _recordedCommands = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<long, AutomationObservation[]> _recentPopupAutomation = new();
    private readonly ConcurrentDictionary<long, long> _recentPopupFrames = new();
    private readonly ConcurrentDictionary<long, byte> _capturedDialogs = new();
    private readonly HashSet<string> _popupFingerprints = new(StringComparer.Ordinal);
    private readonly HashSet<string> _recordedTabs = new(StringComparer.Ordinal);
    private readonly HashSet<string> _controlFingerprints = new(StringComparer.Ordinal);
    private readonly object _tabGate = new();
    private readonly CancellationTokenSource _shutdown = new();
    private PopupWindowEventMonitor? _popupMonitor;
    private Task? _worker;
    private Task? _popupWorker;
    private AutoTabCandidate[] _knownTabs = [];
    private long _baselineSequence;
    private FrameObservation? _latestFullFrame;
    private int _pendingRoot;
    private long _rootRequestSequence;
    private int _draining;
    private long _popupCaptures;
    private long _popupFailures;
    private RectI? _lastManualHighlightBounds;
    private AutomationObservation? _lastManualHighlightControl;
    private readonly ConcurrentDictionary<string, AutomationObservation> _popupSources = new(StringComparer.Ordinal);
    private string _activeInteractionId = "";

    public AdaptiveCaptureCoordinator(ManualRecordingSession session, WindowTarget target, Action<string>? status = null)
        : this(session, target, status, popupAutomationOverride: null, dialogAutomationOverride: null)
    {
    }

    internal AdaptiveCaptureCoordinator(
        ManualRecordingSession session,
        WindowTarget target,
        Action<string>? status,
        Func<long, CancellationToken,
            Task<(IReadOnlyList<AutomationObservation> Items, bool TimedOut, string Status)>>? popupAutomationOverride,
        Func<long, CancellationToken,
            Task<(IReadOnlyList<AutomationObservation> Items, bool TimedOut, string Status)>>? dialogAutomationOverride = null,
        Func<CancellationToken, Task<FrameObservation?>>? rootCaptureAttemptOverride = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _target = target ?? throw new ArgumentNullException(nameof(target));
        _status = status;
        _popupAutomationOverride = popupAutomationOverride;
        _dialogAutomationOverride = dialogAutomationOverride;
        _rootCaptureAttemptOverride = rootCaptureAttemptOverride;
    }

    public long BaseFrameSequence => Interlocked.Read(ref _baselineSequence);
    public FrameObservation LatestFullFrame => _latestFullFrame ?? throw new InvalidOperationException("Adaptive capture has not started.");
    public event Action<FrameObservation>? FullFrameRegistered;
    public IReadOnlySet<string> RecordedTabKeys
    {
        get { lock (_tabGate) return _recordedTabs.ToHashSet(StringComparer.Ordinal); }
    }
    public bool IsCommandRecorded(string tabKey, string commandKey) =>
        _recordedCommands.ContainsKey(tabKey + "\n" + commandKey);

    public void MarkCommandRecorded(string tabKey, string commandKey) =>
        _recordedCommands.TryAdd(tabKey + "\n" + commandKey, 0);

    public void Start(FrameObservation baseline)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        if (_worker is not null) throw new InvalidOperationException("Adaptive capture is already active.");
        _baselineSequence = baseline.Sequence;
        _latestFullFrame = baseline;
        UpdateTabs(baseline, recordSelected: true);
        _worker = Task.Run(ProcessAsync);
        _popupWorker = Task.Run(ProcessPopupsAsync);
        _popupMonitor = new PopupWindowEventMonitor(_target, QueuePopup);
        _popupMonitor.Start();
    }

    public AdaptiveCaptureCheckpoint CreateClickCheckpoint(string? interactionId = null)
    {
        _capturedPopupsForCurrentClick.Clear();
        _seenPopupsForCurrentClick.Clear();
        _lastManualHighlightBounds = null;
        _lastManualHighlightControl = null;
        _activeInteractionId = string.IsNullOrWhiteSpace(interactionId)
            ? "capture-" + Guid.NewGuid().ToString("N")
            : interactionId;
        return new(Interlocked.Read(ref _popupCaptures), Interlocked.Read(ref _popupFailures), _activeInteractionId);
    }

    public void ArmPopupSource(AutomationObservation source, string? interactionId = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        var id = string.IsNullOrWhiteSpace(interactionId) ? _activeInteractionId : interactionId;
        if (!string.IsNullOrWhiteSpace(id))
            _popupSources[id] = source;
    }

    public AdaptiveDialogCaptureCheckpoint CreateDialogCheckpoint()
    {
        IReadOnlySet<long> existing;
        try
        {
            existing = WindowCatalog.ListProcessWindows(_target)
                .Where(window => window.Hwnd != _target.RootOwnerHwnd)
                .Select(window => window.Hwnd)
                .ToHashSet();
        }
        catch
        {
            existing = new HashSet<long>();
        }
        return new(existing);
    }

    public async Task<AdaptiveDialogCaptureResult> WaitForDialogCaptureAsync(
        AdaptiveDialogCaptureCheckpoint checkpoint,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WindowTarget? dialog = null;
            try
            {
                dialog = WindowCatalog.ListProcessWindows(_target)
                    .Where(window => window.Hwnd != _target.RootOwnerHwnd &&
                                     ShouldCaptureDialogWindow(
                                         window.Hwnd,
                                         checkpoint,
                                         _capturedDialogs.ContainsKey(window.Hwnd)) &&
                                     IsExactWindowCaptureCandidate(window))
                    .OrderBy(window => window.ZOrder)
                    .FirstOrDefault();
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }

            if (dialog is not null)
            {
                var rootAutomation = IsPeerRootCaptureCandidate(dialog)
                    ? RootAutomationForDialog()
                    : await ResolveRootAutomationForDialogAsync(cancellationToken).ConfigureAwait(false);
                var frame = await _session.CaptureOwnedDialogAsync(
                    dialog.Hwnd, rootAutomation, cancellationToken,
                    _dialogAutomationOverride).ConfigureAwait(false);
                if (frame is not null)
                {
                    _capturedDialogs.TryAdd(dialog.Hwnd, 0);
                    RegisterFullFrame(frame);
                    var finalFrame = await CaptureDialogTabVariantsAsync(
                        dialog.Hwnd, frame, cancellationToken).ConfigureAwait(false);
                    return new(AdaptiveDialogCaptureOutcome.Captured, dialog.Hwnd, dialog.Title, finalFrame);
                }
                return new(AdaptiveDialogCaptureOutcome.Failed, dialog.Hwnd, dialog.Title, null);
            }
            await Task.Delay(PopupPollInterval, cancellationToken).ConfigureAwait(false);
        }
        return new(AdaptiveDialogCaptureOutcome.NotObserved, 0, string.Empty, null);
    }

    internal static bool ShouldCaptureDialogWindow(
        long hwnd,
        AdaptiveDialogCaptureCheckpoint checkpoint,
        bool alreadyCaptured) =>
        hwnd != 0 && !checkpoint.ExistingWindowHwnds.Contains(hwnd) && !alreadyCaptured;

    private async Task<FrameObservation> CaptureDialogTabVariantsAsync(
        long dialogHwnd,
        FrameObservation initialFrame,
        CancellationToken cancellationToken)
    {
        var tabs = initialFrame.Automation
            .Where(control => control.WindowHwnd == dialogHwnd &&
                              control.IsEnabled && !control.IsOffscreen &&
                              control.Bounds.Width > 0 && control.Bounds.Height > 0 &&
                              control.ControlType.EndsWith(".TabItem", StringComparison.OrdinalIgnoreCase))
            .GroupBy(control => string.IsNullOrWhiteSpace(control.AutomationId)
                ? $"{control.Name}|{control.Bounds.X},{control.Bounds.Y},{control.Bounds.Width},{control.Bounds.Height}"
                : control.AutomationId, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(control => control.Bounds.X)
            .ThenBy(control => control.Bounds.Y)
            .ToArray();
        if (tabs.Length <= 1) return initialFrame;

        var current = initialFrame;
        // bosa_sdm exposes every page's controls through MSAA but omits the actual
        // tab objects. Its synthetic tab observations use Ctrl+Tab, so the initial
        // frame plus five advances cover all six pages without returning to and
        // recording the initial page twice.
        var tabsToActivate = tabs.All(candidate => candidate.ClassName == "OfficeDialogTab")
            ? tabs.Take(tabs.Length - 1)
            : tabs.Where(candidate => !candidate.IsSelected);
        foreach (var tab in tabsToActivate)
        {
            var tabIdentity = string.IsNullOrWhiteSpace(tab.AutomationId) ? tab.Name : tab.AutomationId;
            var tabToken = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(tabIdentity ?? string.Empty)))
                .ToLowerInvariant()[..16];
            var interaction = _session.CreateInteractionContext(
                $"auto-dialog-tab:{dialogHwnd:x}:{tabToken}",
                1,
                InteractionActor.AutoExplorer,
                InteractionGestureKind.ProgrammaticSelect,
                InteractionActionKind.Select,
                current.Sequence,
                tab);
            if (!NativeMethods.IsWindow((nint)dialogHwnd) ||
                !await _session.TrySelectDialogTabAsync(dialogHwnd, tab, cancellationToken).ConfigureAwait(false))
            {
                _session.CompleteInteraction(interaction, InteractionOutcome.Failed,
                    diagnosticCode: "dialog-tab-activation-failed");
                _session.AddCaptureHealth("adaptive", "dialog-tab-activation-failed",
                    $"Dialog tab {tab.Name} could not be activated; the already captured dialog controls were retained.");
                continue;
            }

            var variant = await _session.CaptureOwnedDialogAsync(
                dialogHwnd, RootAutomationForDialog(), cancellationToken,
                _dialogAutomationOverride, interaction).ConfigureAwait(false);
            if (variant is null)
            {
                _session.CompleteInteraction(interaction, InteractionOutcome.TimedOut,
                    diagnosticCode: "dialog-tab-controls-missed");
                _session.AddCaptureHealth("adaptive", "dialog-tab-controls-missed",
                    $"Dialog tab {tab.Name} opened but did not expose a stable control tree.");
                continue;
            }
            current = variant;
            _session.CompleteInteraction(interaction, InteractionOutcome.Succeeded,
                [variant.Sequence], "dialog-tab-captured");
            RegisterFullFrame(variant);
        }
        _status?.Invoke($"Dialog captured with {tabs.Length} tabs.");
        return current;
    }

    internal static bool IsOwnedDialogCandidate(WindowTarget window)
    {
        if (window.Bounds.Width < 120 || window.Bounds.Height < 80)
            return false;
        const string monitorSuffix = "Monitor";
        if (window.Title.EndsWith(monitorSuffix, StringComparison.OrdinalIgnoreCase) &&
            Guid.TryParse(window.Title[..^monitorSuffix.Length], out _))
            return false;
        if (window.ClassName.Equals("#32770", StringComparison.OrdinalIgnoreCase) ||
            window.ClassName.StartsWith("bosa_sdm_", StringComparison.OrdinalIgnoreCase))
            return true;
        if (window.OwnerHwnd == 0)
            return false;
        var token = $"{window.ClassName} {window.Title}";
        return !token.Contains("popup", StringComparison.OrdinalIgnoreCase) &&
               !token.Contains("dropdown", StringComparison.OrdinalIgnoreCase) &&
               !token.Contains("NetUI", StringComparison.OrdinalIgnoreCase) &&
               !string.IsNullOrWhiteSpace(window.Title);
    }

    internal static bool IsPeerRootCaptureCandidate(WindowTarget window) =>
        window.Hwnd == window.RootOwnerHwnd &&
        window.OwnerHwnd == 0 &&
        window.Bounds.Width >= 120 &&
        window.Bounds.Height >= 80 &&
        (window.ExStyle & NativeMethods.WsExToolWindow) == 0 &&
        !string.IsNullOrWhiteSpace(window.Title);

    private static bool IsExactWindowCaptureCandidate(WindowTarget window) =>
        IsOwnedDialogCandidate(window) || IsPeerRootCaptureCandidate(window);

    public RectI? ResolveManualHighlightBounds(RectI clickPoint) =>
        _lastManualHighlightBounds is { Width: > 0, Height: > 0 } bounds && Contains(bounds, clickPoint.X, clickPoint.Y)
            ? bounds
            : null;

    public AutomationObservation? LastManualHighlightControl => _lastManualHighlightControl;

    public long ResolveInteractionSourceFrameSequence(long clickWindowHwnd) =>
        clickWindowHwnd != 0 && _recentPopupFrames.TryGetValue(clickWindowHwnd, out var sequence)
            ? sequence
            : LatestFullFrame.Sequence;

    internal static RectI? ResolveManualHighlightBounds(
        IReadOnlyList<AutomationObservation> automation,
        RectI clickPoint) =>
        ResolveManualHighlightControl(automation, clickPoint)?.Bounds;

    internal static AutomationObservation? ResolveManualHighlightControl(
        IReadOnlyList<AutomationObservation> automation,
        RectI clickPoint) =>
        automation
            .Where(item =>
                item.IsEnabled &&
                !item.IsOffscreen &&
                item.Bounds.Width > 0 &&
                item.Bounds.Height > 0 &&
                Contains(item.Bounds, clickPoint.X, clickPoint.Y) &&
                IsReasonableManualHighlight(item))
            .OrderBy(ManualHighlightPriority)
            .ThenBy(item => (long)item.Bounds.Width * item.Bounds.Height)
            .FirstOrDefault();

    public async Task<AdaptiveClickCaptureOutcome> CaptureClickAsync(
        RectI point,
        AdaptiveCaptureCheckpoint checkpoint,
        CancellationToken cancellationToken,
        long clickWindowHwnd = 0,
        AdaptiveDialogCaptureCheckpoint? dialogCheckpoint = null)
    {
        if (Volatile.Read(ref _draining) != 0)
            return AdaptiveClickCaptureOutcome.Failed;

        // Resolve the source control while the root surface is still known. A
        // popup-producing click returns through the popup path below, so waiting
        // until after popup capture would lose the Ribbon button highlight.
        var clickScopeHwnd = ResolvePointScopeHwnd(clickWindowHwnd);
        if (clickScopeHwnd != 0 && _recentPopupAutomation.TryGetValue(clickScopeHwnd, out var popupControls))
            SetLastManualHighlight(ResolveManualHighlightTarget(_target, popupControls, point));
        else if ((clickScopeHwnd == 0 || clickScopeHwnd == _target.RootOwnerHwnd) && _latestFullFrame is not null)
            SetLastManualHighlight(ResolveManualHighlightTarget(_target, _latestFullFrame.Automation, point));
        if (_lastManualHighlightControl is not null)
            ArmPopupSource(_lastManualHighlightControl, checkpoint.InteractionId);

        var preservePopupSource = _lastManualHighlightControl is not null;
        var controlCaptured = await CaptureClickedControlAsync(
            point, preservePopupSource, clickScopeHwnd, cancellationToken).ConfigureAwait(false);
        if (_lastManualHighlightControl is null && _latestFullFrame is not null)
            SetLastManualHighlight(ResolveManualHighlightTarget(_target, _latestFullFrame.Automation, point));

        // A dialog may already have been captured before the user changes one of
        // its tabs or controls. That is not a new HWND, so a creation checkpoint
        // cannot detect the new state. Re-read the exact dialog after every manual
        // click inside it and persist the resulting variant with the full control
        // tree instead of leaving only a two-node point delta.
        if (TryResolveDialogScope(clickScopeHwnd, out var clickedDialog))
        {
            var rootAutomation = await ResolveRootAutomationForDialogAsync(cancellationToken).ConfigureAwait(false);
            var dialogFrame = await _session.CaptureOwnedDialogAsync(
                clickedDialog.Hwnd, rootAutomation, cancellationToken,
                _dialogAutomationOverride).ConfigureAwait(false);
            if (dialogFrame is not null)
            {
                _capturedDialogs.TryAdd(clickedDialog.Hwnd, 0);
                RegisterFullFrame(dialogFrame);
                return AdaptiveClickCaptureOutcome.DialogCaptured;
            }
            return AdaptiveClickCaptureOutcome.DialogFailed;
        }

        AutoTabCandidate? tab;
        lock (_tabGate)
            tab = _knownTabs.FirstOrDefault(candidate => Contains(candidate.Observation.Bounds, point.X, point.Y));
        if (tab is not null && !IsTabRecorded(tab.StableKey) && _pendingTabs.TryAdd(tab.StableKey, 0))
        {
            if (!_requests.Writer.TryWrite(new TabRequest(tab.StableKey)))
            {
                _pendingTabs.TryRemove(tab.StableKey, out _);
                _session.AddCaptureHealth("adaptive", "queue-full", "A tab delta was dropped because the adaptive queue was full.");
            }
        }

        if (dialogCheckpoint is { } armedDialogCheckpoint)
        {
            var dialogResult = await WaitForDialogCaptureAsync(
                armedDialogCheckpoint, TimeSpan.FromMilliseconds(180), cancellationToken).ConfigureAwait(false);
            if (dialogResult.Outcome == AdaptiveDialogCaptureOutcome.Captured)
            {
                if (dialogResult.Frame is not null)
                    RegisterFullFrame(dialogResult.Frame);
                return AdaptiveClickCaptureOutcome.DialogCaptured;
            }
            if (dialogResult.Outcome == AdaptiveDialogCaptureOutcome.Failed)
                return AdaptiveClickCaptureOutcome.DialogFailed;
        }

        // A popup may be created well after the input call returns. Keep scanning
        // throughout the observation window instead of treating an initially
        // empty queue as proof that no popup will appear.
        var popupOutcome = await ObservePopupCapturesAsync(
            checkpoint, TimeSpan.FromMilliseconds(1_600), cancellationToken).ConfigureAwait(false);
        if (popupOutcome == AdaptivePopupCaptureOutcome.Captured)
            return AdaptiveClickCaptureOutcome.PopupCaptured;

        if (popupOutcome == AdaptivePopupCaptureOutcome.Failed)
            return AdaptiveClickCaptureOutcome.PopupFailed;

        // Classic owner-drawn Win32/Delphi providers can spend longer walking
        // the accessibility tree than the whole manual interaction budget. A
        // screenshot is the authoritative evidence for these surfaces, and the
        // offline parallel enricher can reconstruct their controls without
        // keeping the user blocked or losing the newly opened page.
        if (PreferFastVisualRootCapture(_target, _latestFullFrame?.Automation ?? []))
        {
            _status?.Invoke("Saving the changed screen. No automatic click is being sent.");
            var visualFrame = await CaptureFastVisualRootAsync(
                checkpoint.InteractionId, cancellationToken).ConfigureAwait(false);
            if (visualFrame is not null)
                return AdaptiveClickCaptureOutcome.RootCaptured;
        }

        // A control delta proves which control was clicked, but it is not a
        // complete representation of the page that appeared afterwards. Wait
        // for the full root transaction before arming the next manual click so
        // tables, page buttons, and persistent application chrome are attached
        // to this interaction instead of being captured by a later click.
        _status?.Invoke("Reading the changed screen for up to 8 seconds. No automatic click is being sent.");
        var rootFrame = await RefreshRootSurfaceAsync(
            ManualRootRefreshTimeout, cancellationToken).ConfigureAwait(false);
        if (rootFrame is not null)
            return AdaptiveClickCaptureOutcome.RootCaptured;

        // Never let a slow or broken provider erase a screen the user actually
        // opened. Persist the pixels after the native deadline and let map build
        // complete the control tree from that immutable evidence.
        var fallbackFrame = await CaptureFastVisualRootAsync(
            checkpoint.InteractionId, cancellationToken).ConfigureAwait(false);
        if (fallbackFrame is not null)
            return AdaptiveClickCaptureOutcome.RootCaptured;

        return controlCaptured
            ? AdaptiveClickCaptureOutcome.ControlCaptured
            : AdaptiveClickCaptureOutcome.Failed;
    }

    private async Task<bool> CaptureClickedControlAsync(
        RectI point,
        bool preserveExistingHighlight,
        long clickScopeHwnd,
        CancellationToken cancellationToken)
    {
        var probe = await _session.CollectPointAutomationAsync(
            point, ControlAutomationTimeout, 64, cancellationToken,
            clickScopeHwnd == 0 ? null : clickScopeHwnd).ConfigureAwait(false);
        var capturedItems = probe.Items;
        var resolvedControl = ResolveManualHighlightControl(capturedItems, point) ??
                              ResolveOpaqueRevitRibbonPanel(_target, capturedItems, point);
        if (resolvedControl is null || IsOpaqueRevitRibbonPanel(_target, resolvedControl))
        {
            // The managed WPF bridge frequently returns only Revit's Ribbon panel.
            // Ask the native UIA3 client at the exact click point before falling
            // back to a synthetic marker. This commonly returns the real WPF
            // Button/Tab bounds even when a full-tree walk was truncated.
            var nativeScope = clickScopeHwnd == 0 ? _target.RootOwnerHwnd : clickScopeHwnd;
            var native = await _session.CollectNativePointAutomationAsync(
                nativeScope,
                point,
                TimeSpan.FromMilliseconds(900),
                48,
                cancellationToken).ConfigureAwait(false);
            if (native.Items.Count > 0)
            {
                capturedItems = capturedItems.Concat(native.Items)
                    .GroupBy(item => string.IsNullOrWhiteSpace(item.RuntimeId)
                        ? $"{item.ControlType}:{item.Bounds}"
                        : item.RuntimeId, StringComparer.Ordinal)
                    .Select(group => group.First())
                    .ToArray();
                resolvedControl = ResolveManualHighlightControl(native.Items, point) ??
                                  ResolveOpaqueRevitRibbonPanel(_target, native.Items, point) ??
                                  resolvedControl;
            }
        }
        var opaqueRibbonPanel = IsOpaqueRevitRibbonPanel(_target, resolvedControl);
        var highlightControl = NormalizeManualHighlightTarget(_target, resolvedControl, point);
        if (opaqueRibbonPanel && highlightControl is not null)
        {
            capturedItems = capturedItems.Concat([highlightControl]).ToArray();
            _session.AddCaptureHealth("adaptive", "ribbon-command-observed",
                "Revit exposed only the Ribbon panel container; the user's click was retained as an individual command.");
        }
        if (highlightControl is null && preserveExistingHighlight)
        {
            if (_lastManualHighlightControl is not null &&
                (_lastManualHighlightControl.ClassName == "UiAtlas.ObservedRibbonCommand" ||
                 _lastManualHighlightControl.ClassName == "UiAtlas.HoverRegion" ||
                 _lastManualHighlightControl.ClassName == "UiAtlas.VisualControlRegion"))
            {
                highlightControl = PromotePointerConfirmedControl(_lastManualHighlightControl);
                capturedItems = capturedItems.Concat([highlightControl]).ToArray();
            }
            else
            {
                return false;
            }
        }
        if (highlightControl is null)
        {
            // Revit views, Miro boards, and other accelerated canvases expose only
            // one large document host through UIA. The user's click is still direct
            // evidence of an interactive target, so retain a small point-backed
            // region instead of silently dropping it from the map.
            highlightControl = CreateObservedCanvasTarget(_target, point);
            capturedItems = capturedItems.Concat([highlightControl]).ToArray();
            _session.AddCaptureHealth("adaptive", "canvas-target-observed",
                "The clicked canvas item was not exposed by UI Automation; a pointer-observed target was retained.");
        }

        // A current point probe is stronger evidence than the previous full frame.
        // This matters after Outlook changes modules without changing its HWND or
        // dimensions: the previous frame can contain an unrelated control at the
        // same coordinates. If an owned popup covered the point, the probe is
        // empty and the pre-click source above remains available instead.
        SetLastManualHighlight(highlightControl);

        if (clickScopeHwnd == _target.RootOwnerHwnd && UsesFullControlFrames(_target))
        {
            var frame = await CaptureFullOutlookSurfaceAsync(cancellationToken).ConfigureAwait(false);
            RegisterFullFrame(frame);
            _status?.Invoke("Complete Outlook state captured.");
            return true;
        }

        var fingerprint = FingerprintControlDelta(capturedItems);
        lock (_controlFingerprints)
            if (_controlFingerprints.Contains(fingerprint)) return true;

        await _session.CaptureAutomationDeltaAsync(
            "adaptive-control", capturedItems, cancellationToken, _baselineSequence,
            clickScopeHwnd == 0 ? null : clickScopeHwnd).ConfigureAwait(false);
        lock (_controlFingerprints) _controlFingerprints.Add(fingerprint);
        _status?.Invoke("Clicked control state captured.");
        return true;
    }

    internal static AutomationObservation CreateObservedCanvasTarget(WindowTarget target, RectI point)
    {
        const int markerSize = 18;
        var width = Math.Min(markerSize, Math.Max(1, target.Bounds.Width));
        var height = Math.Min(markerSize, Math.Max(1, target.Bounds.Height));
        var maxX = (long)target.Bounds.X + target.Bounds.Width - width;
        var maxY = (long)target.Bounds.Y + target.Bounds.Height - height;
        var x = (int)Math.Clamp((long)point.X - width / 2, target.Bounds.X, maxX);
        var y = (int)Math.Clamp((long)point.Y - height / 2, target.Bounds.Y, maxY);
        var relativeX = point.X - target.Bounds.X;
        var relativeY = point.Y - target.Bounds.Y;

        return new AutomationObservation(
            $"ui-atlas:pointer:{relativeX}:{relativeY}",
            string.Empty,
            string.Empty,
            "Observed canvas target",
            "CanvasItem",
            "UiAtlas.ObservedCanvasTarget",
            new RectI(x, y, width, height),
            IsEnabled: true,
            IsOffscreen: false,
            FrameworkId: "UiAtlas.Pointer",
            WindowHwnd: target.RootOwnerHwnd,
            SupportedPatterns: ["SelectionItem"]);
    }

    internal static AutomationObservation? NormalizeManualHighlightTarget(
        WindowTarget target,
        AutomationObservation? control,
        RectI point) =>
        IsOpaqueRevitRibbonPanel(target, control)
            ? CreateObservedRevitRibbonCommand(target, point, control!)
            : control;

    internal static AutomationObservation? ResolveManualHighlightTarget(
        WindowTarget target,
        IReadOnlyList<AutomationObservation> automation,
        RectI point)
    {
        ArgumentNullException.ThrowIfNull(automation);
        var control = ResolveManualHighlightControl(automation, point);
        if (control is not null && !IsOpaqueRevitRibbonPanel(target, control))
            return control;
        var shadow = automation
            .Where(candidate => candidate.ClassName is "UiAtlas.HoverRegion" or "UiAtlas.VisualControlRegion" &&
                                Contains(candidate.Bounds, point.X, point.Y))
            .OrderBy(candidate => (long)candidate.Bounds.Width * candidate.Bounds.Height)
            .FirstOrDefault();
        if (shadow is not null) return PromotePointerConfirmedControl(shadow);
        control ??= ResolveOpaqueRevitRibbonPanel(target, automation, point);
        return NormalizeManualHighlightTarget(target, control, point);
    }

    private static AutomationObservation PromotePointerConfirmedControl(AutomationObservation control) => control with
    {
        ControlType = "ControlType.Button",
        IsEnabled = true,
        IsOffscreen = false,
        FrameworkId = "UiAtlas.Pointer",
        SupportedPatterns = ["InvokePatternIdentifiers.Pattern"]
    };

    private static AutomationObservation? ResolveOpaqueRevitRibbonPanel(
        WindowTarget target,
        IReadOnlyList<AutomationObservation> automation,
        RectI point) =>
        automation
            .Where(control =>
                IsOpaqueRevitRibbonPanel(target, control) &&
                Contains(control.Bounds, point.X, point.Y))
            .OrderBy(control => (long)control.Bounds.Width * control.Bounds.Height)
            .FirstOrDefault();

    internal static bool IsOpaqueRevitRibbonPanel(
        WindowTarget target,
        AutomationObservation? control)
    {
        if (control is null) return false;
        var application = $"{target.ProcessName} {target.OriginalFilename} {target.ProductName}";
        if (!application.Contains("Revit", StringComparison.OrdinalIgnoreCase) ||
            control.Bounds.Width <= 0 || control.Bounds.Height is < 60 or > 220)
            return false;

        var nativePanel =
            control.ControlType.EndsWith(".DataItem", StringComparison.OrdinalIgnoreCase) &&
            control.ClassName.Equals("ItemsControlItem", StringComparison.OrdinalIgnoreCase) &&
            control.Name.Equals("UIFramework.RvtRibbonPanel", StringComparison.OrdinalIgnoreCase);
        var cachedRibbonSurface =
            control.ControlType.EndsWith(".Custom", StringComparison.OrdinalIgnoreCase) &&
            control.AutomationId.EndsWith("_PanelBarScrollViewer", StringComparison.OrdinalIgnoreCase);
        return nativePanel || cachedRibbonSurface;
    }

    internal static AutomationObservation CreateObservedRevitRibbonCommand(
        WindowTarget target,
        RectI point,
        AutomationObservation panel)
    {
        // Revit's compact Ribbon commands use an approximately 24 px hit cell at
        // 125% DPI. Keeping the pointer-backed fallback inside that cell prevents
        // adjacent commands from visually overlapping when the provider exposes
        // only the common panel surface.
        const int markerSize = 24;
        var width = Math.Min(markerSize, panel.Bounds.Width);
        var height = Math.Min(markerSize, panel.Bounds.Height);
        var maxX = (long)panel.Bounds.X + panel.Bounds.Width - width;
        var maxY = (long)panel.Bounds.Y + panel.Bounds.Height - height;
        var x = (int)Math.Clamp((long)point.X - width / 2, panel.Bounds.X, maxX);
        var y = (int)Math.Clamp((long)point.Y - height / 2, panel.Bounds.Y, maxY);
        var cellX = Math.Max(0, (point.X - panel.Bounds.X) / 8);
        var cellY = Math.Max(0, (point.Y - panel.Bounds.Y) / 8);
        var identity = $"revit-ribbon-command-{cellX}-{cellY}";
        return new AutomationObservation(
            $"{panel.RuntimeId}.{identity}",
            panel.RuntimeId,
            identity,
            "Observed Ribbon command",
            "ControlType.Button",
            "UiAtlas.ObservedRibbonCommand",
            new RectI(x, y, width, height),
            IsEnabled: true,
            IsOffscreen: false,
            FrameworkId: "UiAtlas.Pointer",
            WindowHwnd: target.RootOwnerHwnd,
            SupportedPatterns: ["InvokePatternIdentifiers.Pattern"]);
    }

    internal static bool UsesFullControlFrames(WindowTarget target) =>
        RibbonSurfaceCapturePolicy.NeedsVisibleApplicationBody(target);

    private async Task<FrameObservation> CaptureFullOutlookSurfaceAsync(CancellationToken cancellationToken)
    {
        var profile = RibbonSurfaceCapturePolicy.DenseRetry;
        var body = await _session.CollectWindowAutomationAsync(
            _target.RootOwnerHwnd,
            TimeSpan.FromSeconds(20),
            RecordingContractLimits.MaxControlsPerFrame,
            cancellationToken).ConfigureAwait(false);
        var navigation = await _session.CollectNavigationAutomationAsync(
            _target.RootOwnerHwnd,
            profile.NavigationTimeout,
            profile.NavigationMaxNodes,
            cancellationToken).ConfigureAwait(false);
        var ribbon = await _session.CollectRibbonAutomationAsync(
            _target.RootOwnerHwnd,
            profile.RibbonTimeout,
            profile.RibbonMaxNodes,
            cancellationToken).ConfigureAwait(false);
        var controls = body.Items.Concat(navigation.Items).Concat(ribbon.Items)
            .GroupBy(control => string.IsNullOrWhiteSpace(control.RuntimeId)
                ? $"bounds:{control.Bounds.X},{control.Bounds.Y},{control.Bounds.Width},{control.Bounds.Height}:{control.AutomationId}"
                : control.RuntimeId, StringComparer.Ordinal)
            .Select(group => group.Last())
            .ToArray();
        var incomplete = body.TimedOut || navigation.TimedOut || ribbon.TimedOut ||
                         body.Status is not ("ok" or "node-limit") ||
                         navigation.Status is not ("ok" or "node-limit") ||
                         ribbon.Status is not ("ok" or "node-limit");
        if (incomplete)
            _session.AddCaptureHealth("outlook-full-click", "partial",
                "One or more Outlook UI Automation passes were incomplete; every returned control was retained.");

        return await _session.CaptureAsync(
            "adaptive-control-full",
            cancellationToken,
            new FrameCaptureOptions(
                IncludeAutomation: false,
                CapturePhase: "materialized",
                ObservationScope: "full-root",
                ObservedWindowHwnds: [_target.RootOwnerHwnd],
                ScreenshotWindowHwnds: [_target.RootOwnerHwnd],
                BaseFrameSequence: _baselineSequence,
                AutomationOverride: controls,
                AutomationTimedOutOverride: incomplete,
                AutomationStatusOverride: incomplete ? "partial" : "ok")).ConfigureAwait(false);
    }

    private long ResolvePointScopeHwnd(long clickWindowHwnd)
    {
        if (clickWindowHwnd == 0)
            return _target.RootOwnerHwnd;
        try
        {
            var root = WindowCatalog.GetTopLevelHandle((nint)clickWindowHwnd).ToInt64();
            return root != 0 && WindowCatalog.IsSameProcessWindow(_target, root)
                ? root
                : _target.RootOwnerHwnd;
        }
        catch
        {
            return _target.RootOwnerHwnd;
        }
    }

    private bool TryResolveDialogScope(long hwnd, out WindowTarget dialog)
    {
        dialog = null!;
        // Some legacy applications use a hidden owner window and expose their
        // real main UI as another top-level HWND. The explicitly selected HWND
        // is still the main recording surface, never an owned dialog.
        if (hwnd == 0 || hwnd == _target.Hwnd || hwnd == _target.RootOwnerHwnd) return false;
        try
        {
            var candidate = WindowCatalog.Resolve(hwnd);
            if (candidate.ProcessId != _target.ProcessId ||
                candidate.ProcessStartedUtc != _target.ProcessStartedUtc ||
                !IsExactWindowCaptureCandidate(candidate))
                return false;
            dialog = candidate;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private IReadOnlyList<AutomationObservation> RootAutomationForDialog() =>
        (_latestFullFrame?.Automation ?? [])
        .Where(control => control.WindowHwnd == 0 || control.WindowHwnd == _target.RootOwnerHwnd)
        .ToArray();

    private async Task<IReadOnlyList<AutomationObservation>> ResolveRootAutomationForDialogAsync(
        CancellationToken cancellationToken)
    {
        var cached = RootAutomationForDialog();
        if (!RibbonSurfaceCapturePolicy.NeedsVisibleApplicationBody(_target))
            return cached;

        var current = await _session.CollectWindowAutomationAsync(
            _target.RootOwnerHwnd,
            TimeSpan.FromSeconds(20),
            RecordingContractLimits.MaxControlsPerFrame,
            cancellationToken).ConfigureAwait(false);
        if (!current.TimedOut && current.Status is ("ok" or "node-limit") && current.Items.Count > 0)
        {
            return current.Items
                .Where(control => control.WindowHwnd == 0 || control.WindowHwnd == _target.RootOwnerHwnd)
                .ToArray();
        }

        _session.AddCaptureHealth("outlook-dialog-root", current.Status,
            "The current Outlook root controls were unavailable while capturing an owned dialog; cached controls were retained.");
        return cached;
    }

    private void SetLastManualHighlight(AutomationObservation? control)
    {
        _lastManualHighlightControl = control;
        _lastManualHighlightBounds = control?.Bounds;
    }

    private static bool IsReasonableManualHighlight(AutomationObservation item)
    {
        var type = item.ControlType.StartsWith("ControlType.", StringComparison.OrdinalIgnoreCase)
            ? item.ControlType[12..]
            : item.ControlType;
        if (type.Equals("Window", StringComparison.OrdinalIgnoreCase)) return false;

        var area = (long)item.Bounds.Width * item.Bounds.Height;
        if (item.Bounds.Width > 900 || item.Bounds.Height > 620 || area > 420_000)
            return false;
        if (type is ("Document" or "Group" or "List" or "Pane" or "Table" or "Tree") &&
            (item.Bounds.Width > 480 || item.Bounds.Height > 320))
            return false;
        return true;
    }

    private static int ManualHighlightPriority(AutomationObservation item)
    {
        var type = item.ControlType;
        if (type.EndsWith(".SplitButton", StringComparison.OrdinalIgnoreCase)) return 0;
        if (type.EndsWith(".Button", StringComparison.OrdinalIgnoreCase) ||
            type.EndsWith(".MenuItem", StringComparison.OrdinalIgnoreCase)) return 1;
        if (type.EndsWith(".TabItem", StringComparison.OrdinalIgnoreCase) ||
            type.EndsWith(".ComboBox", StringComparison.OrdinalIgnoreCase) ||
            type.EndsWith(".ListItem", StringComparison.OrdinalIgnoreCase) ||
            type.EndsWith(".TreeItem", StringComparison.OrdinalIgnoreCase) ||
            type.EndsWith(".Hyperlink", StringComparison.OrdinalIgnoreCase)) return 2;
        return item.SupportedPatterns is { Count: > 0 } ? 3 : 10;
    }

    public void RegisterFullFrame(FrameObservation frame)
    {
        _latestFullFrame = frame;
        UpdateTabs(frame, recordSelected: true);
        try
        {
            FullFrameRegistered?.Invoke(frame);
        }
        catch
        {
            // Overlay/UI observers must never be able to fail evidence capture.
        }
    }

    internal static bool PreferFastVisualRootCapture(
        WindowTarget target,
        IReadOnlyList<AutomationObservation> controls)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(controls);
        return target.ClassName.StartsWith("Tfrm", StringComparison.OrdinalIgnoreCase) &&
               controls.Any(control =>
                   control.ClassName.StartsWith("TAbacre", StringComparison.OrdinalIgnoreCase) ||
                   control.ClassName.Contains("DBGrid", StringComparison.OrdinalIgnoreCase));
    }

    internal static IReadOnlyList<AutomationObservation> SelectFastRootSnapshotHints(
        IReadOnlyList<AutomationObservation> controls,
        RectI rootBounds)
    {
        ArgumentNullException.ThrowIfNull(controls);
        var topBoundary = rootBounds.Y + Math.Max(90, rootBounds.Height * 22 / 100);
        var selectedIds = controls
            .Where(control => !control.IsOffscreen &&
                              control.Bounds.Width > 0 && control.Bounds.Height > 0 &&
                              !control.FrameworkId.StartsWith("UiAtlas.", StringComparison.OrdinalIgnoreCase))
            .Where(control =>
            {
                var type = NormalizeControlType(control.ControlType);
                return type is "Window" or "TitleBar" or "MenuBar" or "MenuItem" or "StatusBar" ||
                       control.ClassName.EndsWith("Button", StringComparison.OrdinalIgnoreCase) &&
                       control.Bounds.Y + control.Bounds.Height / 2 <= topBoundary;
            })
            .Select(control => control.RuntimeId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);
        var byId = controls
            .Where(control => !string.IsNullOrWhiteSpace(control.RuntimeId))
            .GroupBy(control => control.RuntimeId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var pending = new Queue<string>(selectedIds);
        while (pending.Count > 0)
        {
            var id = pending.Dequeue();
            if (!byId.TryGetValue(id, out var control) ||
                string.IsNullOrWhiteSpace(control.ParentRuntimeId) ||
                !selectedIds.Add(control.ParentRuntimeId))
                continue;
            pending.Enqueue(control.ParentRuntimeId);
        }
        return controls.Where(control => selectedIds.Contains(control.RuntimeId)).ToArray();
    }

    public async Task<FrameObservation?> RefreshRootSurfaceAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        var request = CreateRootRequest(isBackground: false);
        if (!_requests.Writer.TryWrite(request))
        {
            request.Cancellation.Dispose();
            _session.AddCaptureHealth("adaptive", "queue-full",
                "The requested main-surface refresh could not be queued.");
            return null;
        }
        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(timeout);
        try
        {
            return await request.Completion.Task.WaitAsync(timeoutCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            request.Cancellation.Cancel();
            try
            {
                await request.Completion.Task.WaitAsync(CancelledRootDrainTimeout, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
            {
                // The UIA worker owns a killable child process. The request
                // cancellation above prevents its result from being reused by
                // a later navigation request even if cleanup needs more time.
            }
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }
    }

    public async Task<AdaptivePopupCaptureOutcome> WaitForPopupCapturesAsync(
        AdaptiveCaptureCheckpoint checkpoint,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        return await ObservePopupCapturesAsync(checkpoint, timeout, cancellationToken).ConfigureAwait(false);
    }

    private async Task<AdaptivePopupCaptureOutcome> ObservePopupCapturesAsync(
        AdaptiveCaptureCheckpoint checkpoint,
        TimeSpan observationWindow,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.Add(observationWindow > TimeSpan.Zero
            ? observationWindow
            : TimeSpan.Zero);

        do
        {
            var captures = Interlocked.Read(ref _popupCaptures);
            if (captures > checkpoint.PopupCaptures && _pendingPopups.IsEmpty)
                return AdaptivePopupCaptureOutcome.Captured;
            QueueVisiblePopups();
            captures = Interlocked.Read(ref _popupCaptures);
            if (captures > checkpoint.PopupCaptures && _pendingPopups.IsEmpty)
                return AdaptivePopupCaptureOutcome.Captured;

            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
                break;
            await Task.Delay(remaining < PopupPollInterval ? remaining : PopupPollInterval, cancellationToken)
                .ConfigureAwait(false);
        }
        while (true);

        // If the popup appeared at the end of the observation window, finish
        // recording that already-discovered window before allowing another click.
        var drained = await WaitForPendingPopupsAsync(PopupCaptureCompletionWindow, cancellationToken)
            .ConfigureAwait(false);
        return ResolvePopupCaptureOutcome(
            checkpoint,
            Interlocked.Read(ref _popupCaptures),
            Interlocked.Read(ref _popupFailures),
            drained);
    }

    internal static AdaptivePopupCaptureOutcome ResolvePopupCaptureOutcome(
        AdaptiveCaptureCheckpoint checkpoint,
        long popupCaptures,
        long popupFailures,
        bool queueDrained)
    {
        if (!queueDrained)
            return AdaptivePopupCaptureOutcome.Failed;
        if (popupCaptures > checkpoint.PopupCaptures)
            return AdaptivePopupCaptureOutcome.Captured;
        if (popupFailures > checkpoint.PopupFailures)
            return AdaptivePopupCaptureOutcome.Failed;
        return AdaptivePopupCaptureOutcome.NotObserved;
    }

    public async Task DrainAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _draining, 1) == 0)
        {
            _popupMonitor?.Dispose();
            _popupMonitor = null;
            _requests.Writer.TryComplete();
            _popupRequests.Writer.TryComplete();
        }
        var workers = new[] { _worker, _popupWorker }.Where(task => task is not null).Cast<Task>().ToArray();
        if (workers.Length == 0) return;
        try
        {
            await Task.WhenAll(workers).WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _session.AddCaptureHealth("adaptive", "drain-timeout", "Pending adaptive captures were abandoned after the finish boundary.");
            _shutdown.Cancel();
        }
    }

    private void QueuePopup(long hwnd)
    {
        if (Volatile.Read(ref _draining) != 0 || hwnd == _target.RootOwnerHwnd ||
            !IsPopupCaptureCandidateClass(WindowCatalog.GetClass((nint)hwnd))) return;
        try
        {
            var popup = WindowCatalog.Resolve(hwnd);
            if (popup.ProcessId != _target.ProcessId ||
                popup.ProcessStartedUtc != _target.ProcessStartedUtc ||
                popup.RootOwnerHwnd != _target.RootOwnerHwnd ||
                IsEmbeddedChildWindow(popup) ||
                IsOwnedDialogCandidate(popup) ||
                popup.Hwnd == popup.RootOwnerHwnd && !LooksLikeStandalonePopup(popup))
                return;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return;
        }
        if (!_seenPopupsForCurrentClick.TryAdd(hwnd, 0) || !_pendingPopups.TryAdd(hwnd, 0)) return;
        if (!_popupRequests.Writer.TryWrite(new(hwnd, _activeInteractionId)))
        {
            _pendingPopups.TryRemove(hwnd, out _);
            _seenPopupsForCurrentClick.TryRemove(hwnd, out _);
            Interlocked.Increment(ref _popupFailures);
            _session.AddCaptureHealth("adaptive", "queue-full", "A popup delta was dropped because the adaptive queue was full.");
        }
    }

    private void QueueVisiblePopups()
    {
        try
        {
            foreach (var popup in WindowCatalog.ListProcessWindows(_target).Where(window => window.Hwnd != _target.RootOwnerHwnd))
                QueuePopup(popup.Hwnd);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _session.AddCaptureHealth("adaptive", "popup-scan-failed", "Visible popup windows could not be enumerated.");
        }
    }

    internal static bool IsPopupCaptureCandidateClass(string className) =>
        !className.StartsWith("MSO_BORDEREFFECT_WINDOW_CLASS", StringComparison.OrdinalIgnoreCase) &&
        !className.Equals("SysShadow", StringComparison.OrdinalIgnoreCase) &&
        !className.Equals("#32770", StringComparison.OrdinalIgnoreCase) &&
        !className.StartsWith("bosa_sdm_", StringComparison.OrdinalIgnoreCase);

    internal static bool IsEmbeddedChildWindow(WindowTarget window) =>
        (window.Style & NativeMethods.WsChild) != 0;

    internal static bool LooksLikeStandalonePopup(WindowTarget window)
    {
        var token = $"{window.ClassName} {window.Title}";
        return window.ClassName.Equals("#32768", StringComparison.OrdinalIgnoreCase) ||
               token.Contains("net ui", StringComparison.OrdinalIgnoreCase) ||
               token.Contains("netui", StringComparison.OrdinalIgnoreCase) ||
               token.Contains("popup", StringComparison.OrdinalIgnoreCase) ||
               token.Contains("dropdown", StringComparison.OrdinalIgnoreCase) ||
               token.Contains("flyout", StringComparison.OrdinalIgnoreCase) ||
               token.Contains("menu", StringComparison.OrdinalIgnoreCase) ||
               (window.ExStyle & NativeMethods.WsExToolWindow) != 0;
    }

    internal static IReadOnlyList<WindowTarget> SelectVisiblePopupTargets(
        long rootOwnerHwnd,
        IReadOnlyList<WindowTarget> windows,
        int maxCount = 8)
    {
        ArgumentNullException.ThrowIfNull(windows);
        if (maxCount < 1) throw new ArgumentOutOfRangeException(nameof(maxCount));
        return windows
            .Where(window => window.Hwnd != rootOwnerHwnd &&
                             window.RootOwnerHwnd == rootOwnerHwnd &&
                             window.Bounds.Width > 0 && window.Bounds.Height > 0 &&
                             IsPopupCaptureCandidateClass(window.ClassName) &&
                             LooksLikeStandalonePopup(window))
            .OrderBy(window => window.ZOrder)
            .ThenBy(window => window.Hwnd)
            .Take(maxCount)
            .ToArray();
    }

    private async Task<bool> WaitForPendingPopupsAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (!_pendingPopups.IsEmpty && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken).ConfigureAwait(false);
        return _pendingPopups.IsEmpty;
    }

    private async Task ProcessAsync()
    {
        try
        {
            await foreach (var request in _requests.Reader.ReadAllAsync(_shutdown.Token).ConfigureAwait(false))
            {
                try
                {
                    switch (request)
                    {
                        case TabRequest tab:
                            await ProcessTabAsync(tab.StableKey, _shutdown.Token).ConfigureAwait(false);
                            break;
                        case RootRequest root:
                            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(
                                       _shutdown.Token, root.Cancellation.Token))
                            {
                                var frame = await ProcessRootAsync(linked.Token).ConfigureAwait(false);
                                root.Completion.TrySetResult(frame);
                            }
                            break;
                    }
                }
                catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
                catch (OperationCanceledException) when (request is RootRequest cancelledRoot &&
                                                         cancelledRoot.Cancellation.IsCancellationRequested)
                {
                    cancelledRoot.Completion.TrySetCanceled(cancelledRoot.Cancellation.Token);
                }
                catch (Exception ex)
                {
                    if (request is RootRequest failedRoot)
                        failedRoot.Completion.TrySetException(ex);
                    _session.AddCaptureHealth("adaptive", "delta-failed", $"A background delta was skipped: {ex.GetType().Name}.");
                    _status?.Invoke($"Background screen refresh skipped: {ex.GetType().Name}.");
                }
                finally
                {
                    if (request is TabRequest tab) _pendingTabs.TryRemove(tab.StableKey, out _);
                    if (request is RootRequest root)
                    {
                        root.Cancellation.Dispose();
                        if (root.IsBackground)
                        {
                            var rootState = Interlocked.Exchange(ref _pendingRoot, 0);
                            if (rootState == 2 && !_shutdown.IsCancellationRequested)
                                QueueChangedRootSurface();
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _session.AddCaptureHealth("adaptive", "worker-failed", $"Adaptive capture stopped: {ex.GetType().Name}.");
        }
    }

    private async Task ProcessPopupsAsync()
    {
        try
        {
            await foreach (var request in _popupRequests.Reader.ReadAllAsync(_shutdown.Token).ConfigureAwait(false))
            {
                try
                {
                    await ProcessPopupAsync(request, _shutdown.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref _popupFailures);
                    _session.AddCaptureHealth("adaptive", "popup-failed", $"A popup capture failed: {ex.GetType().Name}.");
                }
                finally
                {
                    _pendingPopups.TryRemove(request.Hwnd, out _);
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _session.AddCaptureHealth("adaptive", "popup-worker-failed", $"Popup capture stopped: {ex.GetType().Name}.");
        }
    }

    private async Task ProcessPopupAsync(PopupRequest request, CancellationToken cancellationToken)
    {
        var hwnd = request.Hwnd;
        var popup = await WaitForMaterializedPopupAsync(hwnd, cancellationToken).ConfigureAwait(false);
        if (popup is null)
        {
            Interlocked.Increment(ref _popupFailures);
            _session.AddCaptureHealth("adaptive", "popup-missed",
                "A transient popup did not become visible with stable non-zero bounds before capture.");
            return;
        }

        // Reserve visible evidence before asking the accessibility provider for
        // two coherent trees. Office can paint a valid flyout while its UIA
        // provider times out; previously that made a successfully opened
        // chevron disappear from the recording altogether.
        await Task.Delay(TimeSpan.FromMilliseconds(160), cancellationToken).ConfigureAwait(false);
        var visualFallback = await _session.TryPrepareVisualPopupDeltaAsync(
            hwnd,
            _baselineSequence,
            TimeSpan.FromMilliseconds(700),
            cancellationToken).ConfigureAwait(false);

        var deadline = DateTimeOffset.UtcNow.Add(PopupContentRetryWindow);
        var lastStatus = "content-incomplete";
        var failedPreparations = 0;
        while (DateTimeOffset.UtcNow < deadline && NativeMethods.IsWindowVisible((nint)hwnd))
        {
            var preparation = await _session.TryPreparePopupDeltaAsync(
                hwnd,
                _baselineSequence,
                TimeSpan.FromMilliseconds(700),
                async (popupHwnd, token) => _popupAutomationOverride is not null
                    ? await _popupAutomationOverride(popupHwnd, token).ConfigureAwait(false)
                    : await _session.CollectPopupAutomationAsync(
                        popupHwnd, PopupAutomationTimeout, PopupMaxNodes, token).ConfigureAwait(false),
                NormalizePopupAutomation,
                PopupSnapshotsMatch,
                cancellationToken,
                waitForDeferredVisualContent: IsExcelTarget(_target)).ConfigureAwait(false);
            lastStatus = preparation.Status;
            if (preparation.Delta is null)
            {
                failedPreparations++;
                if (visualFallback.Delta is not null && failedPreparations >= 2 &&
                    lastStatus.StartsWith("uia-", StringComparison.Ordinal))
                    break;
                var remaining = deadline - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero) break;
                await Task.Delay(remaining < PopupPollInterval ? remaining : PopupPollInterval, cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            var prepared = preparation.Delta;
            _recentPopupAutomation[hwnd] = prepared.Automation.ToArray();
            var fingerprint = FingerprintPopup(prepared.Popup, prepared.Automation);
            var isNew = false;
            lock (_popupFingerprints)
                isNew = _popupFingerprints.Add(fingerprint);
            if (isNew)
            {
                try
                {
                    _popupSources.TryGetValue(request.InteractionId, out var source);
                    var frame = await _session.PersistPreparedPopupDeltaAsync(
                        prepared, cancellationToken, source, request.InteractionId).ConfigureAwait(false);
                    _recentPopupFrames[hwnd] = frame.Sequence;
                }
                catch
                {
                    lock (_popupFingerprints) _popupFingerprints.Remove(fingerprint);
                    throw;
                }
                _status?.Invoke("Popup captured.");
            }
            else
                _status?.Invoke("Popup already captured.");

            _capturedPopupsForCurrentClick.TryAdd(hwnd, 0);
            Interlocked.Increment(ref _popupCaptures);
            return;
        }

        if (visualFallback.Delta is { } fallback)
        {
            var fingerprint = FingerprintPopup(fallback.Popup, fallback.Automation);
            var isNew = false;
            lock (_popupFingerprints)
                isNew = _popupFingerprints.Add(fingerprint);
            if (isNew)
            {
                try
                {
                    _popupSources.TryGetValue(request.InteractionId, out var source);
                    var frame = await _session.PersistPreparedPopupDeltaAsync(
                        fallback, cancellationToken, source, request.InteractionId).ConfigureAwait(false);
                    _recentPopupFrames[hwnd] = frame.Sequence;
                }
                catch
                {
                    lock (_popupFingerprints) _popupFingerprints.Remove(fingerprint);
                    throw;
                }
            }

            _capturedPopupsForCurrentClick.TryAdd(hwnd, 0);
            Interlocked.Increment(ref _popupCaptures);
            _session.AddCaptureHealth("adaptive", "popup-visual-fallback",
                $"The popup accessibility tree was incomplete ({lastStatus}); its visible screen and visual controls were retained instead.");
            _status?.Invoke(isNew
                ? "Popup screen saved; detailed controls will be completed while the map is built."
                : "Popup screen already captured.");
            return;
        }

        Interlocked.Increment(ref _popupFailures);
        _session.AddCaptureHealth("adaptive", "popup-controls-missed",
            $"A coherent popup snapshot was not available before the window closed ({lastStatus}); no popup frame was retained.");
    }

    internal static bool HasPopupContent(IReadOnlyList<AutomationObservation> automation)
    {
        var rootIds = automation
            .Where(control => string.IsNullOrWhiteSpace(control.ParentRuntimeId) && IsPopupSurfaceRoot(control))
            .Select(control => control.RuntimeId)
            .ToHashSet(StringComparer.Ordinal);
        if (rootIds.Count == 0) return false;
        var connected = new HashSet<string>(rootIds, StringComparer.Ordinal);
        var remaining = automation.Where(control => !rootIds.Contains(control.RuntimeId)).ToList();
        var changed = true;
        while (changed)
        {
            changed = false;
            for (var index = remaining.Count - 1; index >= 0; index--)
            {
                if (!connected.Contains(remaining[index].ParentRuntimeId)) continue;
                connected.Add(remaining[index].RuntimeId);
                remaining.RemoveAt(index);
                changed = true;
            }
        }
        return automation.Any(control => connected.Contains(control.RuntimeId) &&
            !rootIds.Contains(control.RuntimeId) && IsMeaningfulPopupControl(control));
    }

    internal static IReadOnlyList<AutomationObservation> NormalizePopupAutomation(
        WindowTarget popup,
        IReadOnlyList<AutomationObservation> automation)
    {
        var candidates = automation
            .Where(control => (control.WindowHwnd == 0 || control.WindowHwnd == popup.Hwnd) &&
                              IsInsidePopup(control.Bounds, popup.Bounds) &&
                              !IsWorksheetContamination(control))
            .ToArray();
        var root = candidates.FirstOrDefault(control =>
            string.IsNullOrWhiteSpace(control.ParentRuntimeId) &&
            IsPopupSurfaceRoot(control) && PopupSurfaceCoverage(control.Bounds, popup.Bounds) >= 0.72);
        if (root is null) return [];

        var accepted = new HashSet<string>(StringComparer.Ordinal) { root.RuntimeId };
        var remaining = candidates.Where(control => control.RuntimeId != root.RuntimeId).ToList();
        var changed = true;
        while (changed)
        {
            changed = false;
            for (var index = remaining.Count - 1; index >= 0; index--)
            {
                var control = remaining[index];
                if (!accepted.Contains(control.ParentRuntimeId)) continue;
                accepted.Add(control.RuntimeId);
                remaining.RemoveAt(index);
                changed = true;
            }
        }

        var result = DeduplicatePopupControls(
            candidates.Where(control => accepted.Contains(control.RuntimeId)).ToArray());
        return HasPopupContent(result) ? result : [];
    }

    internal static IReadOnlyList<AutomationObservation> DeduplicatePopupControls(
        IReadOnlyList<AutomationObservation> controls)
    {
        var result = new List<AutomationObservation>(controls.Count);
        foreach (var control in controls)
        {
            var normalizedType = control.ControlType.StartsWith("ControlType.", StringComparison.OrdinalIgnoreCase)
                ? control.ControlType[12..]
                : control.ControlType;
            var isLeafAction = normalizedType is "Button" or "CheckBox" or "ComboBox" or "DataItem" or
                "Edit" or "Hyperlink" or "ListItem" or "MenuItem" or "RadioButton" or "SplitButton" or
                "TabItem" or "TreeItem";
            var duplicate = isLeafAction && !string.IsNullOrWhiteSpace(control.Name) && result.Any(existing =>
            {
                var existingType = existing.ControlType.StartsWith("ControlType.", StringComparison.OrdinalIgnoreCase)
                    ? existing.ControlType[12..]
                    : existing.ControlType;
                return existingType.Equals(normalizedType, StringComparison.OrdinalIgnoreCase) &&
                       existing.Name.Trim().Equals(control.Name.Trim(), StringComparison.OrdinalIgnoreCase) &&
                       Math.Abs(existing.Bounds.X - control.Bounds.X) <= 2 &&
                       Math.Abs(existing.Bounds.Y - control.Bounds.Y) <= 2 &&
                       Math.Abs(existing.Bounds.Width - control.Bounds.Width) <= 2 &&
                       Math.Abs(existing.Bounds.Height - control.Bounds.Height) <= 2;
            });
            if (!duplicate) result.Add(control);
        }
        return result;
    }

    internal static bool PopupSnapshotsMatch(
        WindowTarget popup,
        IReadOnlyList<AutomationObservation> first,
        IReadOnlyList<AutomationObservation> second) =>
        HasPopupContent(first) && HasPopupContent(second) &&
        string.Equals(FingerprintPopupContent(popup, first), FingerprintPopupContent(popup, second), StringComparison.Ordinal);

    private static bool IsPopupSurfaceRoot(AutomationObservation control)
    {
        var type = control.ControlType.StartsWith("ControlType.", StringComparison.OrdinalIgnoreCase)
            ? control.ControlType[12..]
            : control.ControlType;
        return type is "Menu" or "List" or "Tree" or "Window" or "Pane" or "Custom" or "ToolBar";
    }

    private static bool IsMeaningfulPopupControl(AutomationObservation control)
    {
        if (IsWorksheetContamination(control) || control.Bounds.Width <= 0 || control.Bounds.Height <= 0 || control.IsOffscreen)
            return false;
        var type = control.ControlType.StartsWith("ControlType.", StringComparison.OrdinalIgnoreCase)
            ? control.ControlType[12..]
            : control.ControlType;
        if (type is "Menu" or "Window" or "Pane" or "Group" or "DataGrid" or "Image" or
            "Separator" or "ToolBar") return false;
        if (type == "Text") return !string.IsNullOrWhiteSpace(control.Name);
        return type is "Button" or "CheckBox" or "ComboBox" or "DataItem" or "Edit" or "Hyperlink" or
            "List" or "ListItem" or "MenuItem" or "RadioButton" or "ScrollBar" or "Slider" or "Spinner" or
            "SplitButton" or "TabItem" or "Thumb" or "Tree" or "TreeItem" or "Custom" ||
            control.SupportedPatterns is { Count: > 0 };
    }

    private static bool IsWorksheetContamination(AutomationObservation control) =>
        control.ControlType.EndsWith(".Document", StringComparison.OrdinalIgnoreCase) ||
        control.ControlType is "Document" ||
        control.ClassName.StartsWith("XLSpreadsheet", StringComparison.OrdinalIgnoreCase) ||
        control.ClassName.StartsWith("XLGrid", StringComparison.OrdinalIgnoreCase);

    private static double PopupSurfaceCoverage(RectI candidate, RectI popup)
    {
        var left = Math.Max(candidate.X, popup.X);
        var top = Math.Max(candidate.Y, popup.Y);
        var right = Math.Min(candidate.X + candidate.Width, popup.X + popup.Width);
        var bottom = Math.Min(candidate.Y + candidate.Height, popup.Y + popup.Height);
        if (right <= left || bottom <= top || popup.Width <= 0 || popup.Height <= 0) return 0;
        return (right - left) * (double)(bottom - top) / (popup.Width * (double)popup.Height);
    }

    private static bool IsInsidePopup(RectI bounds, RectI popupBounds) =>
        bounds.Width > 0 && bounds.Height > 0 &&
        bounds.X >= popupBounds.X - 8 && bounds.Y >= popupBounds.Y - 8 &&
        bounds.X + bounds.Width <= popupBounds.X + popupBounds.Width + 8 &&
        bounds.Y + bounds.Height <= popupBounds.Y + popupBounds.Height + 8;

    private async Task<WindowTarget?> WaitForMaterializedPopupAsync(long hwnd, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.Add(PopupMaterializationWindow);
        RectI? previousBounds = null;
        var stableSince = DateTimeOffset.MinValue;
        do
        {
            try
            {
                var popup = WindowCatalog.Resolve(hwnd);
                var belongsToTarget = popup.ProcessId == _target.ProcessId &&
                    popup.ProcessStartedUtc == _target.ProcessStartedUtc &&
                    (popup.Hwnd != popup.RootOwnerHwnd || LooksLikeStandalonePopup(popup));
                var hasUsableBounds = popup.Bounds.Width > 0 && popup.Bounds.Height > 0;
                if (belongsToTarget && NativeMethods.IsWindowVisible((nint)hwnd) && hasUsableBounds)
                {
                    if (previousBounds != popup.Bounds)
                    {
                        previousBounds = popup.Bounds;
                        stableSince = DateTimeOffset.UtcNow;
                    }
                    else if (DateTimeOffset.UtcNow - stableSince >= PopupBoundsStableWindow)
                        return popup;
                }
                else
                {
                    previousBounds = null;
                    stableSince = DateTimeOffset.MinValue;
                }
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                previousBounds = null;
                stableSince = DateTimeOffset.MinValue;
            }

            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
                break;
            await Task.Delay(remaining < PopupPollInterval ? remaining : PopupPollInterval, cancellationToken)
                .ConfigureAwait(false);
        }
        while (true);

        return null;
    }

    private async Task ProcessTabAsync(string stableKey, CancellationToken cancellationToken)
    {
        if (IsTabRecorded(stableKey)) return;
        await Task.Delay(TabSettle, cancellationToken).ConfigureAwait(false);
        var profile = RibbonSurfaceCapturePolicy.ForTarget(_target, RibbonSurfaceCapturePolicy.Fast);
        var navigation = await _session.CollectNavigationAutomationAsync(
            _target.RootOwnerHwnd, profile.NavigationTimeout, profile.NavigationMaxNodes, cancellationToken).ConfigureAwait(false);
        var ribbon = await _session.CollectRibbonAutomationAsync(
            _target.RootOwnerHwnd, profile.RibbonTimeout, profile.RibbonMaxNodes, cancellationToken).ConfigureAwait(false);
        if (!RibbonSurfaceCapturePolicy.HasMaterializedRibbonContent(ribbon.Items))
        {
            profile = RibbonSurfaceCapturePolicy.ForTarget(_target, RibbonSurfaceCapturePolicy.DenseRetry);
            navigation = await _session.CollectNavigationAutomationAsync(
                _target.RootOwnerHwnd, profile.NavigationTimeout, profile.NavigationMaxNodes, cancellationToken).ConfigureAwait(false);
            ribbon = await _session.CollectRibbonAutomationAsync(
                _target.RootOwnerHwnd, profile.RibbonTimeout, profile.RibbonMaxNodes, cancellationToken).ConfigureAwait(false);
        }
        (IReadOnlyList<AutomationObservation> Items, bool TimedOut, string Status) worksheet =
            ([], false, "ok");
        if (IsExcelTarget(_target) && !IsExcelBackstageVisible(_target))
        {
            // Excel's worksheet and bottom chrome are not descendants of the
            // Ribbon provider. Keep them in every full-window tab frame so a tab
            // switch cannot make visible cells and status controls disappear.
            worksheet = await _session.CollectWorksheetAutomationAsync(
                _target.RootOwnerHwnd,
                TimeSpan.FromMilliseconds(3_000),
                2_000,
                cancellationToken).ConfigureAwait(false);
        }
        var controls = MergeNativeControls(navigation.Items, ribbon.Items, worksheet.Items);
        if (!RibbonSurfaceCapturePolicy.HasMaterializedRibbonContent(ribbon.Items))
        {
            _session.AddCaptureHealth("adaptive", "tab-empty", "The selected tab returned no Ribbon controls and remains eligible for another capture.");
            return;
        }
        var frame = await _session.CaptureAsync(
            "adaptive-tab:" + stableKey,
            cancellationToken,
            new FrameCaptureOptions(
                IncludeAutomation: false,
                CapturePhase: "materialized",
                ObservationScope: "full-root",
                BaseFrameSequence: _baselineSequence,
                AutomationOverride: controls,
                AutomationTimedOutOverride: navigation.TimedOut || ribbon.TimedOut || worksheet.TimedOut,
                AutomationStatusOverride: navigation.TimedOut || ribbon.TimedOut || worksheet.TimedOut
                    ? "partial"
                    : "ok")).ConfigureAwait(false);
        lock (_tabGate) _recordedTabs.Add(stableKey);
        UpdateTabs(frame, recordSelected: true);
        _status?.Invoke("New tab captured in the background.");
    }

    private async Task<FrameObservation?> ProcessRootAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await Task.Delay(attempt == 0 ? TabSettle : TimeSpan.FromMilliseconds(420), cancellationToken)
                .ConfigureAwait(false);
            var frame = await (_rootCaptureAttemptOverride is null
                ? ProcessRootAttemptAsync(cancellationToken)
                : _rootCaptureAttemptOverride(cancellationToken)).ConfigureAwait(false);
            if (frame is not null) return frame;
        }

        _session.AddCaptureHealth("adaptive", "root-state-changing",
            "The main surface changed during both capture attempts and was not persisted.");
        return null;
    }

    private async Task<FrameObservation?> ProcessRootAttemptAsync(CancellationToken cancellationToken)
    {
        var currentTarget = WindowCatalog.Resolve(_target.Hwnd);
        var isExcel = IsExcelTarget(currentTarget);
        var isExcelBackstage = isExcel && IsExcelBackstageVisible(currentTarget);
        (IReadOnlyList<AutomationObservation> Items, bool TimedOut, string Status) automation;
        (PreparedFrameScreenshot Screenshot, WindowTarget AnalysisTarget, IReadOnlyList<long> Hwnds) prepared;

        if (isExcel && !isExcelBackstage)
        {
            // Excel has its own native worksheet transaction. Keep it ordered
            // because the accessibility provider can materialize cells while it
            // is read.
            automation = await _session.CollectWorksheetAutomationAsync(
                currentTarget.Hwnd,
                TimeSpan.FromMilliseconds(3_000),
                2_000,
                cancellationToken).ConfigureAwait(false);
            prepared = await CapturePreparedRootScreenshotAsync(cancellationToken).ConfigureAwait(false);
        }
        else if (isExcelBackstage)
        {
            // Excel keeps the worksheet provider alive behind File/Backstage.
            // Reading that provider here pairs Account/New/Open pixels with grid
            // cells from the hidden sheet. Read the visible root instead and do
            // not take the screenshot until that visible tree has materialized.
            automation = await _session.CollectWindowAutomationAsync(
                currentTarget.Hwnd,
                TabAutomationTimeout,
                TabMaxNodes,
                cancellationToken).ConfigureAwait(false);
            prepared = await CapturePreparedRootScreenshotAsync(
                cancellationToken,
                waitForDeferredVisualContent: true).ConfigureAwait(false);
        }
        else
        {
            // Pixel capture and provider traversal are independent I/O lanes.
            // Start both against the same settled screen and validate the page
            // anchor once they finish before persisting either result.
            var automationTask = CollectRootNativeAutomationAsync(
                currentTarget,
                token => _session.CollectWindowAutomationAsync(
                    currentTarget.Hwnd, TabAutomationTimeout, TabMaxNodes, token),
                token => _session.CollectLegacyAutomationAsync(
                    currentTarget.Hwnd, RootLegacyRecoveryTimeout, 4_000, token),
                (band, token) => _session.CollectNativeBandAutomationAsync(
                    currentTarget.Hwnd, band, 48, 28,
                    RootNativeBandRecoveryTimeout, 4_000, token),
                cancellationToken);
            var screenshotTask = CapturePreparedRootScreenshotAsync(cancellationToken);
            await Task.WhenAll(automationTask, screenshotTask).ConfigureAwait(false);
            automation = await automationTask.ConfigureAwait(false);
            prepared = await screenshotTask.ConfigureAwait(false);
        }

        var controls = automation.Items.ToList();
        var pointVerificationIncomplete = false;
        var (preparedScreenshot, analysisTarget, screenshotHwnds) = prepared;

        if (_latestFullFrame is { } previous &&
            _session.TryGetFrameScreenshot(previous.Sequence, out var previousPng) &&
            RootSurfaceIdentity(previous.Automation) is { Length: > 0 } previousIdentity &&
            string.Equals(previousIdentity, RootSurfaceIdentity(controls), StringComparison.Ordinal) &&
            WindowSnapshotCapture.AreVisuallyEquivalentPng(previousPng, preparedScreenshot.Png))
        {
            _status?.Invoke("The visible screen did not change; a duplicate frame was skipped.");
            return previous;
        }

        var opaqueRegions = VisualFallbackPolicy.FindOpaqueRegions(controls, analysisTarget.Bounds);
        var visualRegions = opaqueRegions.Count > 0 ? opaqueRegions : [analysisTarget.Bounds];
        var allowOcrFallback = VisualFallbackPolicy.ShouldUseOcrFallback(controls) || opaqueRegions.Count > 0;
        var recorderBounds = RecorderWindowExclusion.Find(analysisTarget);
        using var visualCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task<IReadOnlyList<AutomationObservation>>? visualTask = null;
        try
        {
            var pixels = OpaqueSurfaceScanner.PixelFrame.Decode(preparedScreenshot.Png);
            visualTask = DiscoverRootVisualControlsAsync(
                analysisTarget,
                pixels,
                visualRegions,
                controls.ToArray(),
                allowOcrFallback,
                recorderBounds,
                visualCancellation.Token);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or InvalidOperationException or
                                         System.ComponentModel.Win32Exception)
        {
            _session.AddCaptureHealth("adaptive", "visual-refresh-failed",
                "The changed screen was captured without visual control enrichment.");
        }

        if (!isExcel || isExcelBackstage)
        {
            var beforeAnchor = CreateRootPageAnchor(controls, analysisTarget.Bounds);
            var verification = isExcelBackstage
                ? await _session.CollectWindowAutomationAsync(
                    currentTarget.Hwnd,
                    TabAutomationTimeout,
                    TabMaxNodes,
                    cancellationToken).ConfigureAwait(false)
                : await _session.CollectNativeBandAutomationAsync(
                    currentTarget.Hwnd,
                    RootRecoveryBand(analysisTarget.Bounds),
                    48,
                    28,
                    RootNativeBandRecoveryTimeout,
                    4_000,
                    cancellationToken).ConfigureAwait(false);
            var afterAnchor = CreateRootPageAnchor(verification.Items, analysisTarget.Bounds);
            if (!RootPageAnchorsMatch(beforeAnchor, afterAnchor))
            {
                visualCancellation.Cancel();
                if (visualTask is not null)
                    _ = visualTask.ContinueWith(
                        task => _ = task.Exception,
                        CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted,
                        TaskScheduler.Default);
                _session.AddCaptureHealth("adaptive", "root-state-mismatch",
                    $"The page changed while it was being captured ({beforeAnchor.Display} -> {afterAnchor.Display}); retrying.");
                return null;
            }
            controls = MergeNativeControls(controls, verification.Items).ToList();
            if (verification.TimedOut || verification.Status is not ("ok" or "node-limit"))
                pointVerificationIncomplete = true;
        }

        try
        {
            var visual = visualTask is null
                ? Array.Empty<AutomationObservation>()
                : await visualTask.ConfigureAwait(false);
            var inspectionPoints = VisualNativeVerification.PlanAll(visual, controls);
            if (inspectionPoints.Count > 0)
            {
                var inspectedNative = new List<AutomationObservation>();
                foreach (var batch in inspectionPoints.Chunk(VisualNativeVerification.MaximumProbePoints))
                {
                    var inspection = await _session.CollectInspectionPointsAutomationAsync(
                        analysisTarget.Hwnd,
                        batch,
                        TimeSpan.FromMilliseconds(3_000),
                        Math.Min(RecordingContractLimits.MaxControlsPerFrame, Math.Max(1_200, batch.Length * 16)),
                        cancellationToken).ConfigureAwait(false);
                    pointVerificationIncomplete |= inspection.TimedOut ||
                                                   inspection.Status is not ("ok" or "node-limit");
                    inspectedNative.AddRange(inspection.Items);
                    controls = MergeNativeControls(controls, inspection.Items).ToList();
                }
                if (allowOcrFallback)
                    controls.AddRange(VisualNativeVerification.RetainUnconfirmedVisuals(
                        visual, inspectedNative));
                else
                    controls.AddRange(VisualNativeVerification.RetainUnconfirmedStructures(
                        visual, inspectedNative));
                if (pointVerificationIncomplete)
                    _session.AddCaptureHealth("adaptive", "native-point-verification-partial",
                        "Point verification of visual candidates was incomplete; unconfirmed candidates were retained.");
            }
            else
            {
                if (allowOcrFallback)
                    controls.AddRange(visual);
                else
                    controls.AddRange(VisualNativeVerification.RetainUnconfirmedStructures(visual, []));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or InvalidOperationException or
                                         System.ComponentModel.Win32Exception)
        {
            _session.AddCaptureHealth("adaptive", "visual-refresh-failed",
                "The changed screen was captured without visual control enrichment.");
        }

        var frame = await _session.CaptureAsync(
            "adaptive-root-change",
            cancellationToken,
            new FrameCaptureOptions(
                IncludeAutomation: false,
                CapturePhase: "materialized",
                ObservationScope: "full-root",
                ScreenshotWindowHwnds: screenshotHwnds,
                BaseFrameSequence: _baselineSequence,
                // The native collector may consume its entire 1,200-node budget
                // on a dense grid. Keep the visual controls appended after that
                // budget as well; otherwise buttons found from pixels disappear
                // precisely on table-heavy screens such as Room Calendar.
                AutomationOverride: controls.ToArray(),
                AutomationTimedOutOverride: automation.TimedOut || pointVerificationIncomplete,
                AutomationStatusOverride: pointVerificationIncomplete ? "partial" : automation.Status,
                PreparedScreenshot: preparedScreenshot)).ConfigureAwait(false);
        RegisterFullFrame(frame);
        _status?.Invoke($"Changed main window captured in the background: {controls.Count} controls.");
        return frame;
    }

    private async Task<IReadOnlyList<AutomationObservation>> DiscoverRootVisualControlsAsync(
        WindowTarget analysisTarget,
        OpaqueSurfaceScanner.PixelFrame pixels,
        IReadOnlyList<RectI> visualRegions,
        IReadOnlyList<AutomationObservation> knownControls,
        bool allowOcrFallback,
        IReadOnlyList<RectI> recorderBounds,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<AutomationObservation> visual;
        try
        {
            if (!allowOcrFallback)
            {
                visual = await Task.Run(
                    () => VisualSurfaceScanner.DiscoverGeometry(
                        analysisTarget, pixels, visualRegions, knownControls, recorderBounds),
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                using var visualTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                visualTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                var visualWithOcr = VisualSurfaceScanner.DiscoverAsync(
                    analysisTarget,
                    pixels,
                    visualRegions,
                    knownControls,
                    visualTimeout.Token,
                    recorderBounds);
                var completed = await Task.WhenAny(
                    visualWithOcr,
                    Task.Delay(TimeSpan.FromSeconds(5), cancellationToken)).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (completed == visualWithOcr)
                {
                    visual = await visualWithOcr.ConfigureAwait(false);
                }
                else
                {
                    visualTimeout.Cancel();
                    visual = await Task.Run(
                        () => VisualSurfaceScanner.DiscoverGeometry(
                            analysisTarget, pixels, visualRegions, knownControls, recorderBounds),
                        cancellationToken).ConfigureAwait(false);
                    _session.AddCaptureHealth("adaptive", "visual-ocr-refresh-timeout",
                        "The changed screen was mapped visually while OCR continued to be unavailable.");
                    _ = visualWithOcr.ContinueWith(
                        task => _ = task.Exception,
                        CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted,
                        TaskScheduler.Default);
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            visual = await Task.Run(
                () => VisualSurfaceScanner.DiscoverGeometry(
                    analysisTarget, pixels, visualRegions, knownControls, recorderBounds),
                CancellationToken.None).ConfigureAwait(false);
            _session.AddCaptureHealth("adaptive", "visual-ocr-refresh-timeout",
                "The changed screen was mapped visually while OCR continued to be unavailable.");
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or InvalidOperationException or
                                         System.ComponentModel.Win32Exception)
        {
            // Geometry recognition does not depend on OCR. Preserve the
            // current screen's controls even if the Windows OCR runtime is
            // temporarily unavailable on this worker thread.
            visual = await Task.Run(
                () => VisualSurfaceScanner.DiscoverGeometry(
                    analysisTarget, pixels, visualRegions, knownControls, recorderBounds),
                cancellationToken).ConfigureAwait(false);
            _session.AddCaptureHealth("adaptive", "visual-ocr-refresh-failed",
                "The changed screen was mapped visually without refreshed OCR labels.");
        }
        return RecorderWindowExclusion.FilterControls(visual, recorderBounds);
    }

    private async Task<FrameObservation?> CaptureFastVisualRootAsync(
        string interactionId,
        CancellationToken cancellationToken)
    {
        try
        {
            var (screenshot, analysisTarget, screenshotHwnds) =
                await CapturePreparedRootScreenshotAsync(cancellationToken).ConfigureAwait(false);
            var hints = SelectFastRootSnapshotHints(
                _latestFullFrame?.Automation ?? [], analysisTarget.Bounds);
            var frame = await _session.CaptureAsync(
                "adaptive-root-change",
                cancellationToken,
                new FrameCaptureOptions(
                    IncludeAutomation: false,
                    CapturePhase: "materialized",
                    ObservationScope: "full-root",
                    ScreenshotWindowHwnds: screenshotHwnds,
                    BaseFrameSequence: _baselineSequence,
                    AutomationOverride: hints,
                    AutomationTimedOutOverride: true,
                    AutomationStatusOverride: "partial",
                    InteractionSource: _lastManualHighlightControl,
                    InteractionId: interactionId,
                    PreparedScreenshot: screenshot)).ConfigureAwait(false);
            RegisterFullFrame(frame);
            _status?.Invoke("Changed screen saved; detailed controls will be completed while the map is built.");
            return frame;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or InvalidOperationException or
                                         IOException or System.ComponentModel.Win32Exception)
        {
            _session.AddCaptureHealth("adaptive", "visual-root-snapshot-failed",
                $"The changed screen snapshot could not be saved: {ex.GetType().Name}.");
            return null;
        }
    }

    private async Task<(PreparedFrameScreenshot Screenshot, WindowTarget AnalysisTarget, IReadOnlyList<long> Hwnds)>
        CapturePreparedRootScreenshotAsync(
            CancellationToken cancellationToken,
            bool waitForDeferredVisualContent = false)
    {
        var discovered = WindowCatalog.ListScopedWindows(_target);
        var targets = discovered
            .Where(WindowSnapshotCapture.IsCapturable)
            .Take(WindowSnapshotCapture.MaxScopedWindows)
            .ToArray();
        if (targets.Length == 0)
            throw new InvalidOperationException("No visible root surface is available for capture.");

        var capture = waitForDeferredVisualContent
            ? await _session.CaptureStableRootScreenshotAsync(targets, cancellationToken).ConfigureAwait(false)
            : await _session.CaptureScreenshotAsync(
                token => WindowSnapshotCapture.CapturePngAsync(targets, token, preferScreenBounds: true),
                cancellationToken).ConfigureAwait(false);
        var bounds = ManualRecordingSession.CompositeBounds(targets);
        var analysisTarget = targets
            .OrderByDescending(target => (long)target.Bounds.Width * target.Bounds.Height)
            .ThenBy(target => target.ZOrder)
            .First();
        var screenshot = new PreparedFrameScreenshot(
            capture.Png,
            bounds,
            capture.Method,
            capture.UsedFallback,
            capture.IsPartial || discovered.Count > targets.Length);
        return (screenshot, analysisTarget, targets.Select(target => target.Hwnd).ToArray());
    }

    internal static async Task<(IReadOnlyList<AutomationObservation> Items, bool TimedOut, string Status)>
        CollectRootNativeAutomationAsync(
            WindowTarget target,
            Func<CancellationToken, Task<(IReadOnlyList<AutomationObservation> Items, bool TimedOut, string Status)>>
                collectFull,
            Func<CancellationToken, Task<(IReadOnlyList<AutomationObservation> Items, bool TimedOut, string Status)>>
                collectLegacy,
            Func<RectI, CancellationToken,
                Task<(IReadOnlyList<AutomationObservation> Items, bool TimedOut, string Status)>> collectNativeBand,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(collectFull);
        ArgumentNullException.ThrowIfNull(collectLegacy);
        ArgumentNullException.ThrowIfNull(collectNativeBand);

        // A timeout belongs to one provider request, not to the application for
        // the rest of the recording. Every root change starts with a fresh full
        // read and falls back to bounded legacy/top-band probes only when needed.
        var full = await collectFull(cancellationToken).ConfigureAwait(false);
        if (!full.TimedOut && full.Status != "node-limit" && full.Items.Count > 0 &&
            !NeedsTopBandRecovery(full.Items, target.Bounds))
            return full;

        var legacy = await collectLegacy(cancellationToken).ConfigureAwait(false);
        var bands = full.Status == "node-limit"
            ? RootRecoveryBands(target.Bounds)
            : [RootRecoveryBand(target.Bounds)];
        var bandItems = new List<AutomationObservation>();
        var bandTimedOut = false;
        var bandIncomplete = false;
        foreach (var initialBand in bands)
        {
            var pending = new Queue<RectI>();
            pending.Enqueue(initialBand);
            while (pending.Count > 0)
            {
                var band = pending.Dequeue();
                var batch = await collectNativeBand(band, cancellationToken).ConfigureAwait(false);
                bandItems.AddRange(batch.Items);
                bandTimedOut |= batch.TimedOut;
                bandIncomplete |= batch.Status is not ("ok" or "node-limit");

                // A provider that fills a regional node budget is queried again
                // through two non-overlapping regions. This is spatial paging:
                // no region silently disappears merely because its parent batch
                // reached the Windows worker safety ceiling.
                if (batch.Status == "node-limit" && band.Height >= 56)
                {
                    var firstHeight = band.Height / 2;
                    pending.Enqueue(band with { Height = firstHeight });
                    pending.Enqueue(new RectI(
                        band.X, band.Y + firstHeight, band.Width, band.Height - firstHeight));
                }
            }
        }

        var merged = MergeNativeControls(full.Items, legacy.Items, bandItems);
        var timedOut = full.TimedOut || legacy.TimedOut || bandTimedOut;
        var incomplete = timedOut || bandIncomplete || full.Status == "node-limit";
        if (!incomplete && full.Items.Count > 0)
            return (merged, false, full.Status);
        return merged.Count > 0
            ? (merged, timedOut, incomplete ? "partial" : "ok")
            : timedOut
                ? (merged, true, "partial")
                : (merged, false, "visual-only");
    }

    internal static RectI RootRecoveryBand(RectI rootBounds)
    {
        var height = Math.Max(1, (int)Math.Ceiling(rootBounds.Height * .35));
        return new RectI(rootBounds.X, rootBounds.Y, Math.Max(1, rootBounds.Width), height);
    }

    internal static IReadOnlyList<RectI> RootRecoveryBands(RectI rootBounds)
    {
        const int batchCount = 4;
        var result = new List<RectI>(batchCount);
        for (var index = 0; index < batchCount; index++)
        {
            var top = rootBounds.Y + rootBounds.Height * index / batchCount;
            var bottom = rootBounds.Y + rootBounds.Height * (index + 1) / batchCount;
            if (bottom > top)
                result.Add(new RectI(rootBounds.X, top, Math.Max(1, rootBounds.Width), bottom - top));
        }
        return result.Count > 0 ? result : [new RectI(rootBounds.X, rootBounds.Y, Math.Max(1, rootBounds.Width), 1)];
    }

    internal static bool NeedsTopBandRecovery(
        IReadOnlyList<AutomationObservation> controls,
        RectI rootBounds)
    {
        var band = RootRecoveryBand(rootBounds);
        var menuItems = controls.Where(control =>
                control.ControlType.EndsWith(".MenuItem", StringComparison.OrdinalIgnoreCase) &&
                IntersectionOverUnion(control.Bounds, band) > 0)
            .ToArray();
        return menuItems.Length > 0 && menuItems.Any(control =>
            string.IsNullOrWhiteSpace(control.Name) ||
            control.Name.Equals(control.AutomationId, StringComparison.OrdinalIgnoreCase) ||
            control.AutomationId.StartsWith("Item ", StringComparison.OrdinalIgnoreCase) &&
            control.Name.StartsWith("Item ", StringComparison.OrdinalIgnoreCase));
    }

    internal static RootPageAnchor CreateRootPageAnchor(
        IReadOnlyList<AutomationObservation> controls,
        RectI rootBounds)
    {
        ArgumentNullException.ThrowIfNull(controls);
        var minimumWideWidth = Math.Max(240, rootBounds.Width * 55 / 100);
        var pageName = controls
            .Where(control => !control.IsOffscreen && !string.IsNullOrWhiteSpace(control.Name) &&
                              control.Bounds.Width >= minimumWideWidth &&
                              control.Bounds.Y >= rootBounds.Y + Math.Max(50, rootBounds.Height / 12) &&
                              control.Bounds.Y < rootBounds.Y + rootBounds.Height * 3 / 5 &&
                              (control.ClassName.Contains("Panel", StringComparison.OrdinalIgnoreCase) ||
                               NormalizeControlType(control.ControlType) is "Pane" or "Group"))
            .OrderBy(control => control.Bounds.Y)
            .ThenByDescending(control => control.Bounds.Width)
            .Select(control => NormalizeAnchorText(control.Name))
            .FirstOrDefault(value => value.Length > 0) ?? string.Empty;
        var activeNavigation = controls
            .Where(control => !control.IsOffscreen && control.Bounds.Width > 0 && control.Bounds.Height > 0 &&
                              control.Bounds.Y < rootBounds.Y + rootBounds.Height * 2 / 5 &&
                              (control.HasKeyboardFocus || control.IsSelected) &&
                              (control.ClassName.EndsWith("Button", StringComparison.OrdinalIgnoreCase) ||
                               NormalizeControlType(control.ControlType) is "Button" or "TabItem" or "MenuItem"))
            .OrderBy(control => control.Bounds.Y)
            .ThenBy(control => control.Bounds.X)
            .Select(control => NormalizeAnchorText(control.Name))
            .FirstOrDefault(value => value.Length > 0) ?? string.Empty;
        var selectedTab = controls
            .Where(control => !control.IsOffscreen && control.IsSelected &&
                              NormalizeControlType(control.ControlType) == "TabItem")
            .OrderBy(control => control.Bounds.Y)
            .ThenBy(control => control.Bounds.X)
            .Select(control => NormalizeAnchorText(control.Name))
            .FirstOrDefault(value => value.Length > 0) ?? string.Empty;
        return new(pageName, activeNavigation, selectedTab);
    }

    internal static bool RootPageAnchorsMatch(RootPageAnchor before, RootPageAnchor after)
    {
        if (before.PageName.Length > 0 && after.PageName.Length > 0)
            return before.PageName.Equals(after.PageName, StringComparison.Ordinal);
        if (before.ActiveNavigation.Length > 0 && after.ActiveNavigation.Length > 0)
            return before.ActiveNavigation.Equals(after.ActiveNavigation, StringComparison.Ordinal);
        if (before.SelectedTab.Length > 0 && after.SelectedTab.Length > 0)
            return before.SelectedTab.Equals(after.SelectedTab, StringComparison.Ordinal);
        // A missing anchor is an inconclusive provider result, not evidence that
        // the page changed. Reject only when both reads identify the same kind of
        // anchor and those identities conflict.
        return true;
    }

    private static string NormalizeAnchorText(string value) => string.Join(' ', value
        .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        .TrimEnd('.')
        .ToLowerInvariant();

    private static string NormalizeControlType(string value) =>
        value.Contains('.') ? value[(value.LastIndexOf('.') + 1)..] : value;

    internal static IReadOnlyList<AutomationObservation> MergeNativeControls(
        params IReadOnlyList<AutomationObservation>[] sources)
    {
        var result = new List<AutomationObservation>();
        foreach (var control in sources.SelectMany(source => source))
        {
            var duplicate = result.FindIndex(existing =>
                (!string.IsNullOrWhiteSpace(control.RuntimeId) &&
                 existing.RuntimeId.Equals(control.RuntimeId, StringComparison.Ordinal)) ||
                (IntersectionOverUnion(existing.Bounds, control.Bounds) >= .82 &&
                 (existing.ControlType.Equals(control.ControlType, StringComparison.OrdinalIgnoreCase) ||
                  existing.ClassName.Equals(control.ClassName, StringComparison.OrdinalIgnoreCase) ||
                  (!string.IsNullOrWhiteSpace(control.Name) &&
                   existing.Name.Equals(control.Name, StringComparison.OrdinalIgnoreCase)))));
            if (duplicate < 0)
                result.Add(control);
            else
                result[duplicate] = MergeNativeControl(result[duplicate], control);
        }
        return result;
    }

    private static AutomationObservation MergeNativeControl(
        AutomationObservation existing,
        AutomationObservation incoming)
    {
        static string Prefer(string current, string candidate) =>
            string.IsNullOrWhiteSpace(current) && !string.IsNullOrWhiteSpace(candidate) ? candidate : current;
        var name = IsUsefulNativeName(incoming.Name, incoming.AutomationId) &&
                   !IsUsefulNativeName(existing.Name, existing.AutomationId)
            ? incoming.Name
            : existing.Name;
        return existing with
        {
            RuntimeId = Prefer(existing.RuntimeId, incoming.RuntimeId),
            ParentRuntimeId = Prefer(existing.ParentRuntimeId, incoming.ParentRuntimeId),
            AutomationId = Prefer(existing.AutomationId, incoming.AutomationId),
            Name = name,
            ControlType = Prefer(existing.ControlType, incoming.ControlType),
            ClassName = Prefer(existing.ClassName, incoming.ClassName),
            FrameworkId = Prefer(existing.FrameworkId, incoming.FrameworkId),
            WindowHwnd = existing.WindowHwnd != 0 ? existing.WindowHwnd : incoming.WindowHwnd,
            SupportedPatterns = (existing.SupportedPatterns ?? [])
                .Concat(incoming.SupportedPatterns ?? [])
                .Distinct(StringComparer.Ordinal)
                .ToArray()
        };
    }

    private static bool IsUsefulNativeName(string name, string automationId) =>
        !string.IsNullOrWhiteSpace(name) &&
        !name.Equals(automationId, StringComparison.OrdinalIgnoreCase) &&
        !(automationId.StartsWith("Item ", StringComparison.OrdinalIgnoreCase) &&
          name.StartsWith("Item ", StringComparison.OrdinalIgnoreCase));

    private static double IntersectionOverUnion(RectI first, RectI second)
    {
        var width = Math.Max(0,
            Math.Min(first.X + first.Width, second.X + second.Width) - Math.Max(first.X, second.X));
        var height = Math.Max(0,
            Math.Min(first.Y + first.Height, second.Y + second.Height) - Math.Max(first.Y, second.Y));
        var intersection = (long)width * height;
        var union = Math.Max(1L,
            (long)first.Width * first.Height + (long)second.Width * second.Height - intersection);
        return intersection / (double)union;
    }

    internal static bool IsExcelTarget(WindowTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var identity = $"{target.ProcessName} {target.OriginalFilename} {target.ProductName} {target.ClassName}";
        return identity.Contains("EXCEL", StringComparison.OrdinalIgnoreCase) ||
               target.ClassName.Equals("XLMAIN", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsBackstageSurface(IReadOnlyList<AutomationObservation> controls) =>
        controls.Any(control =>
            control.ClassName.Equals("FullpageUIHost", StringComparison.OrdinalIgnoreCase) ||
            control.ClassName.Equals("NetUIFullpageUIWindow", StringComparison.OrdinalIgnoreCase) ||
            control.Name.Equals("Backstage view", StringComparison.OrdinalIgnoreCase));

    internal static string RootSurfaceIdentity(IReadOnlyList<AutomationObservation> controls)
    {
        ArgumentNullException.ThrowIfNull(controls);
        if (IsBackstageSurface(controls))
        {
            var selected = controls.FirstOrDefault(control =>
                control.IsSelected &&
                control.ClassName.Equals("NetUIRibbonTab", StringComparison.OrdinalIgnoreCase));
            return "excel-backstage:" + NormalizeAnchorText(selected?.Name ?? string.Empty);
        }

        if (controls.Any(control =>
                control.ClassName.StartsWith("XLSpreadsheet", StringComparison.OrdinalIgnoreCase) ||
                control.ClassName.StartsWith("XLGrid", StringComparison.OrdinalIgnoreCase)))
            return "excel-worksheet";

        var bounds = controls
            .Where(control => control.Bounds.Width > 0 && control.Bounds.Height > 0)
            .Select(control => control.Bounds)
            .FirstOrDefault();
        if (bounds is null || !bounds.IsValid) return string.Empty;
        var anchor = CreateRootPageAnchor(controls, bounds);
        return anchor.Display == "unknown" ? string.Empty : "root:" + anchor.Display;
    }

    private static bool IsExcelBackstageVisible(WindowTarget target)
    {
        try
        {
            return WindowCatalog.ListDescendantHandles(target.RootOwnerHwnd, 4_096).Any(hwnd =>
                NativeMethods.IsWindowVisible((nint)hwnd) &&
                IsBackstageWindowClass(WindowCatalog.GetClass((nint)hwnd)));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or
                                         System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    internal static bool IsBackstageWindowClass(string className) =>
        className.Equals("FullpageUIHost", StringComparison.OrdinalIgnoreCase) ||
        className.Equals("NetUIFullpageUIWindow", StringComparison.OrdinalIgnoreCase);

    internal static string FingerprintControlDelta(IReadOnlyList<AutomationObservation> controls)
    {
        var value = string.Join(';', controls.Select(control => string.Join('|',
            control.RuntimeId, control.AutomationId, control.ControlType, control.IsEnabled,
            control.HasKeyboardFocus, control.IsSelected, control.ToggleState, control.ExpandCollapseState)));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

    private void QueueChangedRootSurface()
    {
        try
        {
            if (!NativeMethods.GetWindowRect((nint)_target.Hwnd, out _))
                throw new InvalidOperationException("The selected root window is no longer available.");
            // Navigation in classic Win32 applications commonly replaces the
            // entire page without changing the root HWND or its dimensions.
            // Every user click can therefore materialize a new surface. Coalesce
            // concurrent requests, but never use window geometry as proof that
            // the visible state is unchanged.
            if (Interlocked.CompareExchange(ref _pendingRoot, 1, 0) != 0)
            {
                // A capture is already queued or running. Remember that the
                // visible page changed again and schedule one final refresh as
                // soon as the current pass completes.
                Interlocked.Exchange(ref _pendingRoot, 2);
                return;
            }
            var request = CreateRootRequest(isBackground: true);
            if (!_requests.Writer.TryWrite(request))
            {
                request.Cancellation.Dispose();
                Interlocked.Exchange(ref _pendingRoot, 0);
                _session.AddCaptureHealth("adaptive", "queue-full", "A changed main surface was dropped because the adaptive queue was full.");
            }
            else
            {
                _status?.Invoke("Changed main window queued for background capture.");
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _session.AddCaptureHealth("adaptive", "root-change-missed", "The main window changed before it could be inspected.");
        }
    }

    private void UpdateTabs(FrameObservation frame, bool recordSelected)
    {
        var tabs = AutoTabDiscovery.Discover(frame).ToArray();
        lock (_tabGate)
        {
            _knownTabs = tabs;
            if (!recordSelected) return;
            var selected = tabs.FirstOrDefault(tab => tab.IsSelected) ?? tabs.FirstOrDefault();
            if (selected is not null) _recordedTabs.Add(selected.StableKey);
        }
    }

    private bool IsTabRecorded(string stableKey)
    {
        lock (_tabGate) return _recordedTabs.Contains(stableKey);
    }

    internal static string FingerprintPopup(WindowTarget popup, IReadOnlyList<AutomationObservation> controls)
    {
        var builder = new StringBuilder();
        builder.Append(StablePopupClassName(popup.ClassName)).Append('|')
            .Append(Round(popup.Bounds.Width)).Append('x').Append(Round(popup.Bounds.Height)).Append(';');
        foreach (var control in controls.OrderBy(item => item.Bounds.Y).ThenBy(item => item.Bounds.X).ThenBy(item => item.ControlType, StringComparer.Ordinal))
        {
            builder.Append(control.AutomationId).Append('|').Append(control.Name).Append('|')
                .Append(control.ControlType).Append('|').Append(control.ClassName).Append('@')
                .Append(Round(control.Bounds.X - popup.Bounds.X)).Append(',').Append(Round(control.Bounds.Y - popup.Bounds.Y)).Append(',')
                .Append(Round(control.Bounds.Width)).Append(',').Append(Round(control.Bounds.Height)).Append(';');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }

    private static string FingerprintPopupContent(WindowTarget popup, IReadOnlyList<AutomationObservation> controls) =>
        FingerprintPopup(popup, controls.Where(IsMeaningfulPopupControl).ToArray());

    private static string StablePopupClassName(string value)
    {
        var bracket = value.IndexOf('[', StringComparison.Ordinal);
        return bracket > 0 ? value[..bracket] : value;
    }

    private static int Round(int value) => (int)Math.Round(value / 8d) * 8;
    private static bool Contains(RectI bounds, int x, int y) => x >= bounds.X && x < bounds.X + bounds.Width && y >= bounds.Y && y < bounds.Y + bounds.Height;

    public async ValueTask DisposeAsync()
    {
        await DrainAsync(TimeSpan.FromSeconds(2), CancellationToken.None).ConfigureAwait(false);
        _shutdown.Cancel();
        _shutdown.Dispose();
    }

    private abstract record CaptureRequest;
    private sealed record TabRequest(string StableKey) : CaptureRequest;
    private sealed record RootRequest(
        long RequestId,
        bool IsBackground,
        CancellationTokenSource Cancellation,
        TaskCompletionSource<FrameObservation?> Completion) : CaptureRequest;
    private sealed record PopupRequest(long Hwnd, string InteractionId);

    private RootRequest CreateRootRequest(bool isBackground) => new(
        Interlocked.Increment(ref _rootRequestSequence),
        isBackground,
        new CancellationTokenSource(),
        new TaskCompletionSource<FrameObservation?>(TaskCreationOptions.RunContinuationsAsynchronously));
}

public enum AdaptiveClickCaptureOutcome
{
    RootCaptured,
    ControlCaptured,
    PopupCaptured,
    PopupFailed,
    DialogCaptured,
    DialogFailed,
    Failed
}

public enum AdaptivePopupCaptureOutcome
{
    Captured,
    NotObserved,
    Failed
}

public readonly record struct AdaptiveCaptureCheckpoint(
    long PopupCaptures,
    long PopupFailures,
    string InteractionId = "");

public readonly record struct RootPageAnchor(
    string PageName,
    string ActiveNavigation,
    string SelectedTab)
{
    public string Display => PageName.Length > 0
        ? PageName
        : ActiveNavigation.Length > 0
            ? ActiveNavigation
            : SelectedTab.Length > 0 ? SelectedTab : "unknown";
}

public readonly record struct AdaptiveDialogCaptureCheckpoint(IReadOnlySet<long> ExistingWindowHwnds);

public sealed record AdaptiveDialogCaptureResult(
    AdaptiveDialogCaptureOutcome Outcome,
    long Hwnd,
    string Title,
    FrameObservation? Frame);

public enum AdaptiveDialogCaptureOutcome
{
    Captured,
    NotObserved,
    Failed
}

internal sealed class PopupWindowEventMonitor : IDisposable
{
    private readonly WindowTarget _target;
    private readonly Action<long> _onPopup;
    private readonly NativeMethods.WinEventProc _callback;
    private nint _hook;

    public PopupWindowEventMonitor(WindowTarget target, Action<long> onPopup)
    {
        _target = target;
        _onPopup = onPopup;
        _callback = OnWinEvent;
    }

    public void Start()
    {
        _hook = NativeMethods.SetWinEventHook(
            NativeMethods.EventObjectShow,
            NativeMethods.EventObjectShow,
            0,
            _callback,
            (uint)_target.ProcessId,
            0,
            NativeMethods.WineventOutofcontext | NativeMethods.WineventSkipownprocess);
        if (_hook == 0) throw new InvalidOperationException("Popup event monitoring could not start.");
    }

    private void OnWinEvent(nint hook, uint eventType, nint hwnd, int objectId, int childId, uint eventThread, uint eventTime)
    {
        if (hwnd == 0 || objectId != NativeMethods.ObjidWindow || childId != 0 ||
            eventType != NativeMethods.EventObjectShow || !NativeMethods.IsWindowVisible(hwnd)) return;
        try
        {
            _onPopup(hwnd.ToInt64());
        }
        catch { }
    }

    public void Dispose()
    {
        if (_hook == 0) return;
        NativeMethods.UnhookWinEvent(_hook);
        _hook = 0;
    }
}
