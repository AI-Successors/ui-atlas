using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Runtime.InteropServices;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Recording.Windows;

namespace UiAtlas.Core.Cli;

internal sealed partial class RecordingHighlightOverlay
{
    private IReadOnlyList<MapperHighlight> _mapperHighlights = [];
    private readonly HashSet<MapperHighlightKind> _hiddenMapperKinds = [];
    private readonly Dictionary<string, Window> _gridHitWindows = new(StringComparer.Ordinal);
    private MapperGridInspector? _gridInspector;
    private MapperLegendWindow? _mapperLegend;
    private RectI? _mapperCaptureRoot;
    private long _mapperSurfaceHwnd;
    private bool _mapperTargetVisible = true;
    private bool _mapperEnabled;
    private bool _mapperNeedsRefresh;
    private long _mapperRevision;
    private bool _gridExplorationActive;
    internal Func<Func<CancellationToken, Task<GridImageExplorationResult>>, CancellationToken, Task<GridImageExplorationResult>>? ExplorationRunner { get; set; }
    internal Func<CancellationToken, Task>? HideRecorderForExploration { get; set; }
    internal Action? RestoreRecorderAfterExploration { get; set; }
    internal Action<string, DataGridReviewContext, GridSchemaReview>? SaveReviewedDataGrid { get; set; }
    internal Action? ResumeAfterGridInspector { get; set; }
    internal IReadOnlyList<MapperHighlight> MapperHighlights => _mapperHighlights;
    internal MapperGridInspector? GridInspector => _gridInspector;
    internal MapperLegendWindow? MapperLegend => _mapperLegend;
    internal IReadOnlyCollection<Window> GridHitWindows => _gridHitWindows.Values;

    private bool IsMapperKindVisible(MapperHighlightKind kind) => !_hiddenMapperKinds.Contains(kind);

    private void SetMapperKindVisible(MapperHighlightKind kind, bool visible)
    {
        var changed = visible ? _hiddenMapperKinds.Remove(kind) : _hiddenMapperKinds.Add(kind);
        if (!changed) return;
        RenderHighlights();
        UpdateMapperWindowVisibility();
    }

    public void ReplaceMapperHighlights(RectI root, FrameObservation frame)
    {
        var surfaceHwnd = ResolveMapperSurfaceHwnd(_target.Hwnd, frame.Window);
        // Native fallback enumerates handles/geometry only; it sends no input and performs no OCR.
        IReadOnlyList<AutomationObservation> hints = [];
        try { hints = NativeGridHostDiscovery.Collect(WindowCatalog.Resolve(surfaceHwnd)); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        var current = frame.Trigger == "mapped-resume-baseline" ? hints : frame.Automation.Concat(hints).ToArray();
        if (_dispatcher is null || _dispatcher.HasShutdownStarted) return;
        Post(() =>
        {
            _mapperEnabled = true;
            if (frame.Automation.Count == 0 && frame.AutomationStatus != "ok" &&
                _mapperCaptureRoot == root && _mapperSurfaceHwnd == surfaceHwnd)
            {
                // A screenshot-only / unavailable observation is not evidence that controls vanished.
                RequestMapperRefresh(invalidateVisual: false);
                return;
            }
            ApplyMapperSnapshot(root, surfaceHwnd, current,
                TabHighlightLayerResolver.ResolveVisibleLayerKey(frame.Window, frame.Automation));
            Console.WriteLine($"Mapper overlay: surface=0x{surfaceHwnd:X}; root={root.X},{root.Y},{root.Width},{root.Height}; " +
                $"hostHints={hints.Count}; nativeGrids={_mapperHighlights.Count(g => g.Kind == MapperHighlightKind.NativeGrid)}; " +
                $"gridCandidates={_mapperHighlights.Count(g => g.Kind == MapperHighlightKind.GridCandidate)}.");
            RefreshMapperTarget();
            RenderHighlights();
        });
    }

    private void ApplyMapperSnapshot(RectI root, long surfaceHwnd, IReadOnlyList<AutomationObservation> controls, string layer)
    {
        // Explore also owns its selection while waiting for the recorder handoff.
        if (_gridExplorationActive || _gridInspector?.IsExploring == true || _gridInspector?.IsReviewingSchema == true || _gridInspector?.Result is not null) return;
        _mapperRevision++;
        _mapperCaptureRoot = root;
        _currentRootBounds = root;
        _mapperSurfaceHwnd = surfaceHwnd;
        _visibleLayerKey = layer;
        _mapperHighlights = MapperHighlightModel.Build(root, controls);
        _mapperNeedsRefresh = false;
        if (_gridInspector is not null)
        {
            var selection = _mapperHighlights.FirstOrDefault(g => g.IsGrid && g.Id == _gridInspector.Selection.Id);
            if (selection is not null) _gridInspector.Update(selection);
            // An occluded UIA table may disappear from a passive snapshot. Keep the
            // selected inspector until the app itself can provide a current view;
            // Explore still resolves and validates the live target before input.
            else if (WindowCatalog.GetTopLevelHandle(NativeMethods.GetForegroundWindow()).ToInt64() == surfaceHwnd)
                _gridInspector.Close();
        }
        SyncGridHitWindows();
    }

    private void RefreshMapperFromLiveSurface(WindowTarget root, WindowTarget surface,
        IReadOnlyList<AutomationObservation> automation, IReadOnlyList<AutomationObservation> hints,
        string layer, bool layerChanged, long expectedRevision)
    {
        // An older background probe must not replace a newer recorded observation.
        if (!_mapperEnabled || _mapperRevision != expectedRevision || !_mapperTargetVisible) return;
        if (_currentRootBounds != root.Bounds)
        { RequestMapperRefresh(invalidateVisual: false); return; }
        var visual = !layerChanged && !_mapperNeedsRefresh && surface.Hwnd == _mapperSurfaceHwnd &&
            _mapperCaptureRoot is { } captured && captured.Width == root.Bounds.Width && captured.Height == root.Bounds.Height
            ? _mapperHighlights.Where(g => MapperHighlightModel.IsVisual(g.Control))
                .Select(g => g.Control with { Bounds = CurrentBounds(g) })
            : [];
        ApplyMapperSnapshot(root.Bounds, surface.Hwnd, automation.Concat(hints).Concat(visual).ToArray(), layer);
        RenderHighlights();
    }

    private void RequestMapperRefresh(bool invalidateVisual = true)
    {
        _mapperNeedsRefresh |= invalidateVisual;
        _lastVisibleLayerRefreshUtc = DateTimeOffset.MinValue;
    }

    internal static long ResolveMapperSurfaceHwnd(long selectedHwnd, WindowObservation observed) =>
        observed.IsVisible && !observed.IsMinimized && !observed.IsCloaked &&
        observed.Bounds.Width > 0 && observed.Bounds.Height > 0 ? observed.Hwnd : selectedHwnd;

    private void ClearMapperHighlights()
    {
        _mapperRevision++;
        _mapperHighlights = [];
        _mapperCaptureRoot = null;
        foreach (var window in _gridHitWindows.Values) window.Close();
        _gridHitWindows.Clear();
        _gridClipState.Clear();
        _gridInspector?.Close();
        _gridInspector = null;
        RequestMapperRefresh();
    }

    private RectI CurrentBounds(MapperHighlight item) => _mapperCaptureRoot is not { } captured ? item.Bounds :
        item.Bounds with { X = item.Bounds.X + _currentRootBounds.X - captured.X,
            Y = item.Bounds.Y + _currentRootBounds.Y - captured.Y };

    private void SyncGridHitWindows()
    {
        var grids = _mapperHighlights.Where(g => g.IsGrid).ToArray();
        foreach (var id in _gridHitWindows.Keys.Where(id => !grids.Any(g => g.Id == id)).ToArray())
        {
            _gridHitWindows[id].Close();
            _gridClipState.Remove(_gridHitWindows[id]);
            _gridHitWindows.Remove(id);
        }
        foreach (var grid in grids)
        {
            if (!_gridHitWindows.TryGetValue(grid.Id, out var hit))
            {
                hit = new Window { Title = "UiAtlas inspect grid", WindowStyle = WindowStyle.None,
                    ResizeMode = ResizeMode.NoResize, AllowsTransparency = true,
                    Background = new SolidColorBrush(Color.FromArgb(1, 255, 255, 255)),
                    ShowInTaskbar = false, ShowActivated = false, Topmost = true, Cursor = Cursors.Hand };
                var id = grid.Id;
                hit.PreviewMouseDown += (_, e) =>
                {
                    e.Handled = true;
                    if (e.ChangedButton != MouseButton.Left) return;
                    var selected = _mapperHighlights.FirstOrDefault(g => g.Id == id);
                    if (selected is not null && _mapperTargetVisible) InspectGrid(selected);
                };
                hit.PreviewMouseUp += (_, e) => e.Handled = true;
                hit.SourceInitialized += (_, _) => ConfigureMapperWindow(hit, noActivate: true);
                _gridHitWindows.Add(id, hit);
            }
            PositionGridHitWindow(hit, CurrentBounds(grid));
        }
        UpdateMapperWindowVisibility();
    }

    private void PositionGridHitWindow(Window window, RectI bounds)
    {
        if (_window is null || !TryProjectToOverlayRect(bounds, out var projected)) return;
        // PointFromScreen uses the overlay's monitor DPI and also handles negative screen coordinates.
        window.Left = _window.Left + projected.Left;
        window.Top = _window.Top + projected.Top;
        window.Width = projected.Width;
        window.Height = projected.Height;
    }

    internal void InspectGrid(MapperHighlight selected)
    {
        if (!selected.IsGrid || !IsMapperKindVisible(selected.Kind)) return;
        if (_gridInspector is null)
        {
            var inspector = new MapperGridInspector(selected);
            inspector.SourceInitialized += (_, _) => ConfigureMapperWindow(inspector, noActivate: true);
            inspector.Explore = ExploreGridAsync;
            inspector.SaveDataGrid = (name, review) =>
            {
                var context = inspector.ReviewContext ?? throw new InvalidOperationException("grid-review-context-missing");
                var save = SaveReviewedDataGrid ?? throw new InvalidOperationException("recording-workspace-unavailable");
                save(name, context, review);
            };
            inspector.NamingStarted += () => { ConfigureMapperWindow(inspector, noActivate: false); PositionInspector(); };
            inspector.Finished += () => ResumeAfterGridInspector?.Invoke();
            inspector.ExplorationStateChanged += () => { if (!inspector.IsExploring) PositionInspector(); };
            inspector.Closed += (_, _) => { if (ReferenceEquals(_gridInspector, inspector)) _gridInspector = null; };
            _gridInspector = inspector;
        }
        else if (_gridInspector.Result is null && !_gridInspector.IsExploring && !_gridInspector.IsReviewingSchema) _gridInspector.Update(selected);
        PositionInspector();
        UpdateMapperWindowVisibility();
    }

    private void PositionInspector()
    {
        if (_gridInspector is null || _gridInspector.UserPositioned || _window is null) return;
        if (!TryProjectToOverlayRect(CurrentBounds(_gridInspector.Selection), out var grid)) return;
        var width = _gridInspector.Width;
        _gridInspector.Measure(new Size(width, double.PositiveInfinity));
        var height = _gridInspector.DesiredSize.Height;
        var left = _window.Left + grid.Right - width - 14;
        var top = _window.Top + grid.Bottom - height - 14;
        // Clamp to the selected application's visible rectangle, including monitors left of the primary.
        if (TryProjectToOverlayRect(_currentRootBounds, out var root))
        {
            left = Math.Max(_window.Left + root.Left, left);
            top = Math.Max(_window.Top + root.Top, top);
        }
        _gridInspector.Left = left;
        _gridInspector.Top = top;
    }

    private static void ConfigureMapperWindow(Window window, bool noActivate)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        var style = GetWindowLong(hwnd, GwlExStyle) | WsExToolWindow;
        if (noActivate) style |= WsExNoActivate;
        else style &= ~WsExNoActivate;
        _ = SetWindowLong(hwnd, GwlExStyle, style);
        // User screenshots include Mapper. Its capture/input scopes explicitly hide all owned windows.
    }

    private void UpdateMapperWindowVisibility()
    {
        var sessionVisible = Volatile.Read(ref _captureVisibleRequested) != 0 &&
            Volatile.Read(ref _screenshotVisibilityHolds) == 0;
        var visible = _mapperTargetVisible && sessionVisible && !_gridExplorationActive;
        var inputEnabled = Volatile.Read(ref _automationTransparencyHolds) == 0;
        foreach (var (id, window) in _gridHitWindows)
        {
            var grid = _mapperHighlights.FirstOrDefault(g => g.Id == id);
            if (visible && grid is not null && IsMapperKindVisible(grid.Kind))
            {
                if (!window.IsVisible) window.Show();
                PositionPhysicalGridWindow(window, CurrentBounds(grid));
                ApplyGridInputClip(window, CurrentBounds(grid));
                SetMapperInputEnabled(window, inputEnabled);
            }
            else window.Hide();
        }
        if (_gridInspector is not null)
        {
            if (visible && IsMapperKindVisible(_gridInspector.Selection.Kind) || _gridExplorationActive && _mapperTargetVisible)
            { if (!_gridInspector.IsVisible) _gridInspector.Show(); }
            else _gridInspector.Hide();
            SetMapperInputEnabled(_gridInspector, inputEnabled);
        }
        if (sessionVisible && _mapperEnabled && _window is not null && !_gridExplorationActive)
        {
            if (_mapperLegend is null)
            {
                var legend = new MapperLegendWindow(IsMapperKindVisible, SetMapperKindVisible);
                legend.SourceInitialized += (_, _) => ConfigureMapperWindow(legend, noActivate: false);
                _window.Closed += (_, _) => { legend.Close(); _mapperLegend = null; };
                _mapperLegend = legend;
            }
            if (!_mapperLegend.IsVisible) _mapperLegend.Show();
            _mapperLegend.Place(_currentRootBounds);
            SetMapperInputEnabled(_mapperLegend, inputEnabled);
        }
        else _mapperLegend?.Hide();
    }

    private Task<GridImageExplorationResult> ExploreGridAsync(MapperHighlight selection,
        IProgress<AcquisitionProgress> progress, CancellationToken cancellation) => ExplorationRunner is { } runner
        ? runner(token => _dispatcher!.InvokeAsync(() => ExploreGridCoreAsync(selection, progress, token)).Task.Unwrap(), cancellation)
        : ExploreGridCoreAsync(selection, progress, cancellation);

    private async Task<GridImageExplorationResult> ExploreGridCoreAsync(MapperHighlight selection,
        IProgress<AcquisitionProgress> progress, CancellationToken cancellation)
    {
        if (_gridExplorationActive) throw new InvalidOperationException("operation-busy");
        var inspector = _gridInspector ?? throw new InvalidOperationException("inspector-closed");
        var bounds = CurrentBounds(selection);
        // Leave Stop outside the captured table. This window never reclaims foreground.
        inspector.UpdateLayout();
        var size = ReadPhysicalBounds(inspector);
        RectI desktop;
        var previousDpi = SetThreadDpiAwarenessContext((nint)(-4));
        try { desktop = new(NativeMethods.GetSystemMetrics(76), NativeMethods.GetSystemMetrics(77),
            NativeMethods.GetSystemMetrics(78), NativeMethods.GetSystemMetrics(79)); }
        finally { if (previousDpi != 0) SetThreadDpiAwarenessContext(previousDpi); }
        var places = new[]
        {
            new RectI(bounds.X + bounds.Width + 12, bounds.Y, size.Width, size.Height),
            new RectI(bounds.X - size.Width - 12, bounds.Y, size.Width, size.Height),
            new RectI(bounds.X, bounds.Y - size.Height - 12, size.Width, size.Height),
            new RectI(bounds.X, bounds.Y + bounds.Height + 12, size.Width, size.Height)
        };
        var place = places.FirstOrDefault(p => MapperHighlightModel.Contains(desktop, p));
        if (place is null || !place.IsValid) throw new InvalidOperationException("no-room-for-explorer");
        PositionPhysicalGridWindow(inspector, place);
        var window = _mapperSurfaceHwnd == 0 ? _target.Hwnd : _mapperSurfaceHwnd;
        var host = selection.Control.WindowHwnd == 0 ? window : selection.Control.WindowHwnd;
        var surface = WindowCatalog.Resolve(window);
        inspector.ReviewContext = new(DataGridTargetBinding.DescribeLocator(window, host), _visibleLayerKey,
            new(selection.Control.ClassName, selection.Control.AutomationId, selection.Control.ControlType, selection.Control.Name),
            bounds with { X = bounds.X - surface.Bounds.X, Y = bounds.Y - surface.Bounds.Y },
            IsUiaNative: selection.Kind == MapperHighlightKind.NativeGrid);
        _gridExplorationActive = true;
        _mapperRevision++;
        var directory = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "UiAtlas", "GridExplorations", Guid.NewGuid().ToString("N"));
        var recorderHidden = false;
        var overlayHidden = false;
        try
        {
            await HideForScreenshotAsync(cancellation).ConfigureAwait(false);
            overlayHidden = true;
            if (HideRecorderForExploration is { } hideRecorder)
            { await hideRecorder(cancellation).ConfigureAwait(false); recorderHidden = true; }
            // Allow the Explore button's release to finish before arming the takeover guard.
            await Task.Delay(200, cancellation).ConfigureAwait(false);
            using var targetWindow = await GridExplorationWindowScope.EnterAsync(_target, window, cancellation).ConfigureAwait(false);
            await inspector.Dispatcher.InvokeAsync(() =>
            {
                // Activation raises the app above other topmost windows; keep Stop reachable without taking focus.
                _ = NativeMethods.SetWindowPos(new WindowInteropHelper(inspector).Handle, NativeMethods.HwndTopMost,
                    0, 0, 0, 0, 0x0213);
            });
            _ = DwmFlush();
            await Task.Delay(20, cancellation).ConfigureAwait(false);
            var worker = System.IO.Path.ChangeExtension(typeof(RecordingHighlightOverlay).Assembly.Location, ".exe");
            return await Task.Run(() => new GridImageExplorer(worker).ExploreAsync(window, host, bounds,
                selection.Name, directory, progress, cancellation), cancellation).ConfigureAwait(false);
        }
        finally
        {
            _gridExplorationActive = false;
            if (recorderHidden) RestoreRecorderAfterExploration?.Invoke();
            if (overlayHidden) RestoreAfterScreenshot();
            if (_dispatcher is { HasShutdownStarted: false } dispatcher)
                await dispatcher.InvokeAsync(() => { if (!_windowClosed) { RequestMapperRefresh(); PositionInspector(); } });
        }
    }

    private static void SetMapperInputEnabled(Window window, bool enabled)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == 0) return;
        var style = GetWindowLong(handle, GwlExStyle);
        var updated = enabled ? style & ~WsExTransparent : style | WsExTransparent;
        if (style != updated) _ = SetWindowLong(handle, GwlExStyle, updated);
        if (NativeMethods.IsWindowEnabled(handle) != enabled) _ = EnableWindow(handle, enabled);
    }

    private static void PositionPhysicalGridWindow(Window window, RectI bounds)
    {
        // A hit window can be on a different-DPI monitor than the desktop-wide drawing window.
        // Its input rectangle must match physical evidence, not another HWND's DIP scale.
        var previous = SetThreadDpiAwarenessContext((nint)(-4));
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (ReadPhysicalBounds(window) == bounds) return;
            _ = NativeMethods.SetWindowPos(handle, 0, bounds.X, bounds.Y, bounds.Width, bounds.Height, 0x0014);
        }
        finally { if (previous != 0) SetThreadDpiAwarenessContext(previous); }
    }

    internal static RectI ReadPhysicalBounds(Window window)
    {
        var previous = SetThreadDpiAwarenessContext((nint)(-4));
        try
        {
            _ = NativeMethods.GetWindowRect(new WindowInteropHelper(window).Handle, out var r);
            return new(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
        }
        finally { if (previous != 0) SetThreadDpiAwarenessContext(previous); }
    }

    [DllImport("user32.dll")]
    private static extern nint SetThreadDpiAwarenessContext(nint context);

    private void RefreshMapperTarget()
    {
        if (_window is null) return;
        var visible = false;
        try
        {
            var target = WindowCatalog.Resolve(_target.Hwnd);
            var observed = WindowSnapshotCapture.Observe(target);
            visible = target.ProcessId == _target.ProcessId && target.ProcessStartedUtc == _target.ProcessStartedUtc &&
                observed.IsVisible && !observed.IsMinimized && !observed.IsCloaked;
            if (!visible)
            {
                _mapperTargetVisible = false;
                RenderHighlights();
                UpdateMapperWindowVisibility();
                return;
            }
            if (_mapperCaptureRoot is { } captured &&
                (captured.Width != target.Bounds.Width || captured.Height != target.Bounds.Height))
            {
                Console.WriteLine($"Mapper overlay invalidated: target size changed from {captured.Width}x{captured.Height} to {target.Bounds.Width}x{target.Bounds.Height}.");
                ClearMapperHighlights();
                RenderHighlights();
            }
            var moved = _currentRootBounds != target.Bounds;
            _currentRootBounds = target.Bounds;
            // A destroyed/hidden/replaced native host invalidates its old hit region immediately.
            foreach (var item in _mapperHighlights.Where(g => g.IsGrid).ToArray())
            {
                var host = item.Control.WindowHwnd;
                if (host == 0 || host == _mapperSurfaceHwnd || host == _target.Hwnd) continue;
                _ = NativeMethods.GetWindowThreadProcessId((nint)host, out var process);
                if (process != _target.ProcessId || !NativeMethods.IsWindowVisible((nint)host) ||
                    !NativeMethods.GetWindowRect((nint)host, out var hostRect) ||
                    !MapperHighlightModel.Equivalent(CurrentBounds(item), new RectI(hostRect.Left, hostRect.Top,
                        hostRect.Right - hostRect.Left, hostRect.Bottom - hostRect.Top)))
                {
                    // Replacing one grid must not erase all the other controls or stop refreshes.
                    _mapperHighlights = _mapperHighlights.Where(g => g.Id != item.Id).ToArray();
                    _mapperRevision++;
                    RequestMapperRefresh();
                    if (_gridInspector?.Selection.Id == item.Id) _gridInspector.Close();
                    SyncGridHitWindows();
                    RenderHighlights();
                }
            }
            if (moved)
            {
                foreach (var grid in _mapperHighlights.Where(g => g.IsGrid))
                    if (_gridHitWindows.TryGetValue(grid.Id, out var hit)) PositionGridHitWindow(hit, CurrentBounds(grid));
                PositionInspector();
                RenderHighlights();
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { ClearMapperHighlights(); }
        if (_mapperTargetVisible != visible)
        {
            _mapperTargetVisible = visible;
            RenderHighlights();
        }
        RefreshMapperOcclusion();
        UpdateMapperWindowVisibility();
    }

    private void RenderMapperHighlights()
    {
        if (_canvas is null || !_mapperTargetVisible) return;
        foreach (var item in _mapperHighlights)
        {
            if (!IsMapperKindVisible(item.Kind)) continue;
            if (!TryProjectToOverlayRect(CurrentBounds(item), out var rect)) continue;
            var brush = MapperGridInspector.Brush(item.Color);
            if (item.Kind == MapperHighlightKind.ComputerVision)
            {
                var length = Math.Min(9, Math.Min(rect.Width, rect.Height) / 3);
                foreach (var (x, y, dx, dy) in new[] { (rect.Left, rect.Top, 1, 1), (rect.Right, rect.Top, -1, 1),
                    (rect.Left, rect.Bottom, 1, -1), (rect.Right, rect.Bottom, -1, -1) })
                    _canvas.Children.Add(new Polyline { Points = [new(x + dx * length, y), new(x, y), new(x, y + dy * length)],
                        Stroke = brush, StrokeThickness = 1.5, IsHitTestVisible = false });
            }
            else
            {
                var border = new Rectangle { Width = rect.Width, Height = rect.Height, Fill = Brushes.Transparent,
                    Stroke = brush, StrokeThickness = item.IsGrid ? 2.5 : 1.25, IsHitTestVisible = false };
                if (item.Kind == MapperHighlightKind.GridCandidate) border.StrokeDashArray = [4, 3];
                Canvas.SetLeft(border, rect.Left); Canvas.SetTop(border, rect.Top);
                _canvas.Children.Add(border);
            }
            if (item.IsGrid)
            {
                var label = new Border { Background = brush, CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(7, 3, 7, 3), MaxWidth = Math.Max(60, rect.Width),
                    Child = new TextBlock { Text = "▦  " + item.Label + " · " + item.Name,
                        Foreground = Brushes.White, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis } };
                Canvas.SetLeft(label, rect.Left + 3); Canvas.SetTop(label, rect.Top + 3);
                _canvas.Children.Add(label);
            }
        }
    }
}
