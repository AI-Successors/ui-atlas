using System.Runtime.InteropServices;
using UiAtlas.Core.Contracts;

namespace UiAtlas.Core.Recording.Windows;

internal interface IDataGridCaptureSession : IDisposable
{
    bool HumanTakeover { get; }
    string? HumanTakeoverEvent => null;
    string? CheckSafety();
    GridScrollPosition ReadPosition();
    Task<GridViewportRequest> ObserveAsync(CancellationToken cancellationToken);
    bool Move(GridScrollAxis axis, int direction, int? exactPosition = null, CancellationToken cancellationToken = default);
}

/// <summary>One native-host session, not a driver registry. Each movement is one line or a restoration thumb.</summary>
internal sealed class DataGridCaptureSession : IDataGridCaptureSession
{
    private readonly MappedGridDefinition _definition;
    private readonly GridTargetIdentity _target;
    private readonly UserInputCancellationMonitor _monitor = new();
    private GridNativeProbeResult? _probe;
    private readonly RectI? _uiaBounds;
    private readonly bool _imageOnly;
    private readonly UiaWorkerClient _worker;
    public bool HumanTakeover => _monitor.WasCancelledByUser;
    public string? HumanTakeoverEvent => _monitor.EventKind;

    public DataGridCaptureSession(MappedGridDefinition definition, GridTargetIdentity target,
        RectI? uiaBounds = null, bool imageOnly = false, UiaWorkerClient? worker = null)
    {
        _definition = definition;
        _target = target;
        _uiaBounds = uiaBounds;
        _imageOnly = imageOnly;
        _worker = worker ?? new UiaWorkerClient();
        _monitor.Start();
    }

    public string? CheckSafety() => HumanTakeover ? "human-takeover" : _monitor.StartupFailed
        ? "input-monitor-unavailable" : DataGridTargetBinding.CheckSafe(_definition, _target, requireForeground: true);

    public GridScrollPosition ReadPosition() => _uiaBounds is { } bounds
        ? Uia(new(bounds, "read"), CancellationToken.None).Position ?? throw new InvalidOperationException("grid-scroll-state-unavailable")
        : new(ReadAxis(GridScrollAxis.Horizontal), ReadAxis(GridScrollAxis.Vertical));

    public async Task<GridViewportRequest> ObserveAsync(CancellationToken cancellationToken)
    {
        var unsafeReason = CheckSafety();
        if (unsafeReason is not null) throw new GridAcquisitionStoppedException(unsafeReason, GridAcquisitionStage.Capture, true);
        WindowTarget host;
        using (new DataGridDpiScope()) host = WindowCatalog.Resolve(_target.HostHwnd);
        if (_probe is null && !_imageOnly)
        {
            WindowTarget window;
            using (new DataGridDpiScope()) window = WindowCatalog.Resolve(_target.SelectedHwnd);
            _probe = await new UiaWorkerClient().ProbeGridAsync(window, host.Hwnd, TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        }
        WindowSnapshotCapture.CaptureResult capture;
        // The explicit screen-bounds branch completes synchronously. Scope thread DPI only
        // around that branch; never hold a thread-local DPI context across an async yield.
        using (new DataGridDpiScope())
            capture = WindowSnapshotCapture.CapturePngAsync([host], cancellationToken, preferScreenBounds: true).GetAwaiter().GetResult();
        unsafeReason = CheckSafety();
        if (unsafeReason is not null) throw new GridAcquisitionStoppedException(unsafeReason, GridAcquisitionStage.Capture, true);
        if (capture.IsPartial) throw new GridAcquisitionStoppedException("partial-viewport-screenshot", GridAcquisitionStage.Capture);
        return new(capture.Png, host.Bounds, _definition.Regions.Header, _definition.Regions.Body,
            _definition.Schema, _probe);
    }

    public bool Move(GridScrollAxis axis, int direction, int? exactPosition = null, CancellationToken cancellationToken = default)
    {
        var unsafeReason = CheckSafety();
        if (unsafeReason is not null) throw new GridAcquisitionStoppedException(unsafeReason, GridAcquisitionStage.Capture, true);
        var current = ReadPosition();
        var position = axis == GridScrollAxis.Horizontal ? current.Horizontal : current.Vertical;
        if (!position.Supported) throw new GridAcquisitionStoppedException("scroll-axis-unsupported", GridAcquisitionStage.Capture);
        if (direction is not (-1 or 1)) throw new ArgumentOutOfRangeException(nameof(direction));
        uint command = direction < 0 ? 0u : 1u; // SB_LINELEFT/UP or SB_LINERIGHT/DOWN: bounded and overlapping when supported.
        if (exactPosition is int desired)
        {
            if (desired is < 0 or > ushort.MaxValue) return false;
            command = 4u | ((uint)desired << 16); // SB_THUMBPOSITION, reserved for restoring a measured native position.
        }
        // These are synchronous messages to the sealed host; no untagged global mouse input is generated.
        // Recheck immediately before delivery. Never move the pointer or reclaim foreground.
        unsafeReason = CheckSafety();
        if (unsafeReason is not null) throw new GridAcquisitionStoppedException(unsafeReason, GridAcquisitionStage.Capture, true);
        cancellationToken.ThrowIfCancellationRequested();
        if (_uiaBounds is { } bounds)
        {
            var result = Uia(new(bounds, "move", axis, direction, exactPosition, _definition, _target), cancellationToken);
            return result.Found && result.HasScroll;
        }
        return NativeGridMethods.SendMessageTimeoutW((nint)_target.HostHwnd,
            axis == GridScrollAxis.Horizontal ? 0x0114u : 0x0115u, command, 0, 0x0003, 250, out _) != 0;
    }

    private GridAxisPosition ReadAxis(GridScrollAxis axis)
    {
        var state = new NativeGridMethods.ScrollInfo
        { Size = (uint)Marshal.SizeOf<NativeGridMethods.ScrollInfo>(), Mask = 0x17 };
        var bar = axis == GridScrollAxis.Horizontal ? 0 : 1;
        if (!NativeGridMethods.GetScrollInfo((nint)_target.HostHwnd, bar, ref state))
        {
            var style = NativeGridMethods.GetWindowLongW((nint)_target.HostHwnd, -16);
            var absent = (style & (bar == 0 ? 0x100000 : 0x200000)) == 0;
            return absent ? new(true, 0, 0, 1, 0, true, true) : new(false, 0, 0, 0, 0, false, false);
        }
        var last = Math.Max(state.Min, (int)Math.Max(int.MinValue, (long)state.Max - Math.Max(0L, (long)state.Page - 1)));
        var info = new NativeGridMethods.ScrollBarInfo
        { Size = (uint)Marshal.SizeOf<NativeGridMethods.ScrollBarInfo>(), State = new uint[6] };
        var barKnown = NativeGridMethods.GetScrollBarInfo((nint)_target.HostHwnd, bar == 0 ? -6 : -5, ref info);
        var startDisabled = barKnown && (info.State[1] & 1) != 0;
        var endDisabled = barKnown && (info.State[5] & 1) != 0;
        // Positions corroborate an edge only after an unchanged capture. A range is never displacement.
        return new(true, state.Min, last, state.Page, state.Position,
            startDisabled || state.Position <= state.Min,
            endDisabled || (state.Page > 0 || state.Max > state.Min) && state.Position >= last);
    }

    public void Dispose() => _monitor.Dispose();

    private GridExplorationAutomationResult Uia(GridExplorationAutomationRequest request, CancellationToken cancellation)
    {
        var reason = CheckSafety();
        if (reason is not null) throw new GridAcquisitionStoppedException(reason, GridAcquisitionStage.Capture, true);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, _monitor.Token);
        WindowTarget window;
        using (new DataGridDpiScope()) window = WindowCatalog.Resolve(_target.SelectedHwnd);
        return _worker.ExploreGridAsync(window, _target.HostHwnd, request, linked.Token).GetAwaiter().GetResult();
    }
}

internal sealed class GridAcquisitionStoppedException(string reason, GridAcquisitionStage stage, bool unsafeToRestore = false) : Exception(reason)
{
    public GridAcquisitionStage Stage { get; } = stage;
    public bool UnsafeToRestore { get; } = unsafeToRestore;
}

internal static partial class NativeGridMethods
{
    [DllImport("user32.dll")] internal static extern int GetWindowLongW(nint hwnd, int index);
    [StructLayout(LayoutKind.Sequential)] internal struct ScrollInfo
    { public uint Size, Mask; public int Min, Max; public uint Page; public int Position, Track; }
    [StructLayout(LayoutKind.Sequential)] internal struct ScrollBarInfo
    {
        public uint Size;
        public NativeMethods.Rect Bounds;
        public int LineButton, ThumbTop, ThumbBottom, Reserved;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)] public uint[] State;
    }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetScrollInfo(nint hwnd, int bar, ref ScrollInfo info);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetScrollBarInfo(nint hwnd, int objectId, ref ScrollBarInfo info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern nint SendMessageTimeoutW(
        nint hwnd, uint message, nuint wParam, nint lParam, uint flags, uint timeout, out nuint result);
}
