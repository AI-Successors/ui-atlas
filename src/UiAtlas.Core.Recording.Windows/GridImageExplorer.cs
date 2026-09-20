using System.Runtime.InteropServices;
using System.Text.Json;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Recording;

namespace UiAtlas.Core.Recording.Windows;

/// <summary>The inspector's bounded image operation. Explore is its only input authorization.</summary>
public sealed class GridImageExplorer
{
    private readonly UiaWorkerClient _worker;
    public GridImageExplorer() => _worker = new();
    public GridImageExplorer(string workerExecutable) => _worker = new(workerExecutable);
    public async Task<GridImageExplorationResult> ExploreAsync(long selectedWindow, long hostHandle,
        RectI selectedBounds, string displayName, string evidenceDirectory,
        IProgress<AcquisitionProgress>? progress = null, CancellationToken cancellationToken = default,
        GridCaptureScope scope = GridCaptureScope.VisibleRowBand, bool readTable = false)
    {
        if (!Enum.IsDefined(scope)) throw new ArgumentOutOfRangeException(nameof(scope));
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        using var duration = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var budgetMs = readTable ? 120_000 : 60_000;
        duration.CancelAfter(TimeSpan.FromMilliseconds(budgetMs));
        var cancellation = duration.Token;
        WindowTarget window, host;
        using (new DataGridDpiScope())
        {
            window = WindowCatalog.Resolve(selectedWindow);
            host = WindowCatalog.Resolve(hostHandle == 0 ? selectedWindow : hostHandle);
        }
        var locator = DataGridTargetBinding.DescribeLocator(window.Hwnd, host.Hwnd);
        if (!DataGridTargetBinding.Contains(host.Bounds, selectedBounds)) throw new InvalidOperationException("selected-table-outside-host");
        var table = new RectI(selectedBounds.X - host.Bounds.X, selectedBounds.Y - host.Bounds.Y,
            selectedBounds.Width, selectedBounds.Height);
        var provisional = new GridRegions(host.Bounds.Width, host.Bounds.Height, table,
            table with { Height = 1 }, table with { Y = table.Y + 1, Height = table.Height - 1 }, table);
        var id = Guid.NewGuid().ToString("N");
        var definition = new MappedGridDefinition(id, "", "", "", displayName, locator, provisional,
            GridScrollAxes.None, new("image-only", false, null, [], 0, 0), GridQualification.Candidate,
            "image-exploration", DateTimeOffset.UtcNow);
        var target = DataGridTargetBinding.ResolveTarget(definition, window.Hwnd);
        var unsafeReason = DataGridTargetBinding.CheckSafe(definition, target, true);
        if (unsafeReason is not null) throw new InvalidOperationException(unsafeReason);
        GridExplorationAutomationResult? native = null;
        using (var preparation = DataGridOperationGate.TryAcquire() ?? throw new InvalidOperationException("operation-busy"))
        {
            // A timed-out provider never causes an unbounded fallback. Native HWND grids
            // can still use their sealed client area and Win32 scrolling.
            try { native = await _worker.ExploreGridAsync(window, host.Hwnd, new(selectedBounds), cancellation).ConfigureAwait(false); }
            catch (Exception ex) when (ex is TimeoutException or InvalidOperationException) { }
            if (native?.Found == true && !native.HasScroll) throw new InvalidOperationException("table-scroll-support-unavailable");
            if (native?.Viewport is { IsValid: true } viewport)
                table = Intersect(table, viewport with { X = viewport.X - host.Bounds.X, Y = viewport.Y - host.Bounds.Y });
            if (native?.Found != true && (selectedBounds != host.Bounds ||
                (NativeGridMethods.GetWindowLongW((nint)host.Hwnd, -16) & 0x300000) == 0))
                throw new InvalidOperationException("table-scroll-support-unavailable");
            using (new DataGridDpiScope())
            {
                if (native?.Found != true && NativeGridMethods.GetClientRect((nint)host.Hwnd, out var client))
                {
                    var origin = new NativeMethods.Point(0, 0);
                    if (NativeGridMethods.ClientToScreen((nint)host.Hwnd, ref origin))
                    {
                        var clipped = Intersect(table, new(origin.X - host.Bounds.X, origin.Y - host.Bounds.Y,
                            client.Right - client.Left, client.Bottom - client.Top));
                        if (clipped.IsValid) table = clipped;
                    }
                }
            }
            WindowSnapshotCapture.CaptureResult screenshot;
            using (new DataGridDpiScope())
                screenshot = WindowSnapshotCapture.CapturePngAsync([host], cancellation, preferScreenBounds: true).GetAwaiter().GetResult();
            if (screenshot.IsPartial) throw new InvalidOperationException("partial-viewport-screenshot");
            var headers = native?.Headers.Select(h => h with { X = h.X - host.Bounds.X, Y = h.Y - host.Bounds.Y }).ToArray();
            var layout = GridImageLayout.Detect(screenshot.Png, table, headers);
            definition = definition with { Regions = provisional with { Data = table, Header = layout.Header, Body = layout.Body },
                Schema = definition.Schema with { HeaderHeight = layout.Header.Height } };
        }
        RectI? uiaBounds = native?.Found == true ? selectedBounds : null;
        using (var state = new DataGridCaptureSession(definition, target, uiaBounds, imageOnly: true, worker: _worker))
        {
            var position = state.ReadPosition();
            definition = definition with { SupportedAxes =
                (position.Horizontal.AtStart && position.Horizontal.AtEnd ? GridScrollAxes.None : GridScrollAxes.Horizontal) |
                (position.Vertical.AtStart && position.Vertical.AtEnd ? GridScrollAxes.None : GridScrollAxes.Vertical) };
        }
        var remainingMs = budgetMs - (int)elapsed.ElapsedMilliseconds;
        if (remainingMs < 5_000) throw new InvalidOperationException("duration-limit");
        var limits = readTable ? new GridCaptureLimits(MaxDurationMs: remainingMs, MaxMovements: 192, MaxTiles: 95,
            RestorationReserveMs: 30_000, RestorationReserveMovements: 64) : new GridCaptureLimits(MaxDurationMs: remainingMs, MaxTiles: 23);
        var approval = new GridOperationApproval(id, target, limits, true, scope == GridCaptureScope.FullTable, DateTimeOffset.UtcNow);
        var coordinator = new DataGridAcquisitionCoordinator(GridImageLayout.DescribeAsync,
            (d, t) => new DataGridCaptureSession(d, t, uiaBounds, imageOnly: true, worker: _worker));
        var capture = await coordinator.AcquireAsync(new(definition, GridAcquisitionMode.ImageExploration, limits,
            target, approval, evidenceDirectory, id, Scope: scope, OptimizeImageTraversal: readTable), progress, cancellation).ConfigureAwait(false);
        var result = GridImageAssembler.Assemble(capture, Path.Combine(evidenceDirectory, "table.png"));
        if (Directory.Exists(evidenceDirectory))
            File.WriteAllText(Path.Combine(evidenceDirectory, "image-result.json"), JsonSerializer.Serialize(result, JsonDefaults.Options));
        return result;
    }

    private static RectI Intersect(RectI a, RectI b)
    {
        var x = Math.Max(a.X, b.X); var y = Math.Max(a.Y, b.Y);
        return new(x, y, Math.Max(0, Math.Min(a.X + a.Width, b.X + b.Width) - x),
            Math.Max(0, Math.Min(a.Y + a.Height, b.Y + b.Height) - y));
    }
}

internal static partial class NativeGridMethods
{
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetClientRect(nint hwnd, out NativeMethods.Rect rect);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ClientToScreen(nint hwnd, ref NativeMethods.Point point);
}
