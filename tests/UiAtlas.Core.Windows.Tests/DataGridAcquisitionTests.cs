using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Recording.Windows;

namespace UiAtlas.Core.Windows.Tests;

public sealed class DataGridAcquisitionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ui-atlas-grid-tests-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(0)]
    [InlineData(25)]
    [InlineData(56)]
    public async Task ColumnExplorationCompletesAtTheCurrentRowBandWithoutVerticalInput(int startingY)
    {
        var session = new FixtureSession { X = 32, Y = startingY, Qualified = false };
        var request = Request() with { Mode = GridAcquisitionMode.ImageExploration, Scope = GridCaptureScope.VisibleRowBand };
        request = request with { Approval = request.Approval! with { AllowVertical = false } };
        var capture = await Coordinator(session).AcquireAsync(request);
        Assert.True(capture.Status == GridCaptureStatus.Complete, string.Join(';', capture.Reasons));
        Assert.Equal(GridCaptureScope.VisibleRowBand, capture.Scope);
        Assert.True(capture.Coverage.ColumnCoverageComplete);
        Assert.False(capture.Coverage.RowCoverageComplete);
        Assert.Equal(startingY == 0, capture.Coverage.TopBoundary);
        Assert.Equal(startingY == 56, capture.Coverage.BottomBoundary);
        Assert.False(capture.Coverage.VerticalMovementObserved);
        Assert.NotEmpty(session.Input);
        Assert.All(session.Input, input => Assert.Equal(GridScrollAxis.Horizontal, input.Axis));
        Assert.All(capture.Tiles.Where(t => t.Accepted), tile => Assert.Equal(0, tile.OffsetY));
        Assert.Equal(GridRestorationStatus.Succeeded, capture.Restoration.Status);
        Assert.Equal(32, session.X);
        Assert.Equal(startingY, session.Y);
        var result = GridImageAssembler.Assemble(capture, Path.Combine(_directory, "row-band.png"));
        Assert.Equal(150, result.TableBounds.Width);
        Assert.Equal(72, result.TableBounds.Height);
        var frame = OpaqueSurfaceScanner.PixelFrame.Decode(File.ReadAllBytes(result.ImagePath!));
        Assert.Equal(DataGridRegistrationTests.Pixel(7, startingY + 9), frame.Pixels[((9 + 8) * frame.Width + 7) * 4]);
        var saved = System.Text.Json.JsonSerializer.Deserialize<CapturedGrid>(File.ReadAllText(capture.ManifestPath),
            UiAtlas.Core.Recording.JsonDefaults.Options)!;
        Assert.Equal(GridCaptureScope.VisibleRowBand, saved.Scope);
        Assert.False(saved.Coverage.RowCoverageComplete);
    }

    [Theory]
    [InlineData(GridAcquisitionMode.FreshRead)]
    [InlineData(GridAcquisitionMode.MappingSurvey)]
    public async Task DataAcquisitionCannotUseTheExplorationRowBandShortcut(GridAcquisitionMode mode)
    {
        var session = new FixtureSession();
        var result = await Coordinator(session).AcquireAsync(Request() with { Mode = mode, Scope = GridCaptureScope.VisibleRowBand });
        Assert.Equal(GridCaptureStatus.Failed, result.Status);
        Assert.Contains("invalid-capture-scope", result.Reasons);
        Assert.Empty(session.Input);
        Assert.Empty(result.Tiles);
    }

    [Fact]
    public async Task UnprovenRightEdgeKeepsColumnExplorationPartial()
    {
        var session = new FixtureSession { Y = 24, RefuseForwardHorizontal = true };
        var result = await Coordinator(session).AcquireAsync(Request() with
            { Mode = GridAcquisitionMode.ImageExploration, Scope = GridCaptureScope.VisibleRowBand });
        Assert.Equal(GridCaptureStatus.Partial, result.Status);
        Assert.False(result.Coverage.ColumnCoverageComplete);
        Assert.False(result.Coverage.RowCoverageComplete);
        Assert.Contains("no-progress-without-boundary", result.Reasons);
        Assert.All(session.Input, input => Assert.Equal(GridScrollAxis.Horizontal, input.Axis));
    }

    [Theory]
    [InlineData(24, 1, true)]
    [InlineData(48, 2, false)]
    public async Task IgnoredRestoreUsesOnlyADirectlyObservedInverseLine(int initial, int cancelAfter, bool restored)
    {
        using var cancellation = new CancellationTokenSource();
        var session = new FixtureSession { Y = initial, IgnoreVerticalPosition = true };
        session.AfterInput = count => { if (count == cancelAfter) cancellation.Cancel(); };
        var coordinator = new DataGridAcquisitionCoordinator(GridImageLayout.DescribeAsync, (_, _) => session);
        var result = await coordinator.AcquireAsync(Request() with { Mode = GridAcquisitionMode.ImageExploration }, cancellationToken: cancellation.Token);
        Assert.Contains("cancelled", result.Reasons);
        Assert.Equal(restored ? GridRestorationStatus.Succeeded : GridRestorationStatus.Failed, result.Restoration.Status);
        Assert.Equal(restored ? 2 : 1, result.Movements.Count(m => m.IsRestoration));
        Assert.All(result.Movements.Where(m => m.IsRestoration), m => Assert.Equal(GridScrollAxis.Vertical, m.Axis));
        if (restored) { Assert.Equal(initial, session.Y); Assert.True(result.Restoration.AnchorVerified); }
    }

    [Fact]
    public async Task RestorationSetsVerticalBeforeHorizontalForControlsThatResetTheOtherAxis()
    {
        using var cancellation = new CancellationTokenSource();
        var session = new FixtureSession { X = 32, Y = 24, VerticalResetsHorizontal = true };
        session.AfterInput = count => { if (count == 2) cancellation.Cancel(); };
        var coordinator = new DataGridAcquisitionCoordinator(GridImageLayout.DescribeAsync, (_, _) => session);
        var result = await coordinator.AcquireAsync(Request() with { Mode = GridAcquisitionMode.ImageExploration }, cancellationToken: cancellation.Token);
        Assert.Equal(GridRestorationStatus.Succeeded, result.Restoration.Status);
        Assert.Equal(new[] { GridScrollAxis.Vertical, GridScrollAxis.Horizontal }, result.Movements.Where(m => m.IsRestoration).Select(m => m.Axis));
        Assert.Equal(32, session.X); Assert.Equal(24, session.Y);
    }

    [Fact]
    public async Task ImageExplorationDoesNotQualifyOcrAndAssemblesOnlyConnectedHashedSources()
    {
        var session = new FixtureSession { Qualified = false };
        var request = Request() with { Mode = GridAcquisitionMode.ImageExploration, LocalOcrQualified = false,
            Definition = Definition() with { Schema = Schema() with { IsComplete = false, Columns = [] } } };
        var coordinator = new DataGridAcquisitionCoordinator(GridImageLayout.DescribeAsync, (_, _) => session);
        var capture = await coordinator.AcquireAsync(request);
        Assert.True(capture.Status == GridCaptureStatus.Complete, string.Join(';', capture.Reasons));
        Assert.False(capture.Schema.IsComplete);
        Assert.Empty(capture.Schema.Columns);
        var result = GridImageAssembler.Assemble(capture, Path.Combine(_directory, "image.png"));
        Assert.NotNull(result.ImagePath);
        Assert.True(result.TableBounds.Width > 96); Assert.True(result.TableBounds.Height > 72);
        Assert.Equal(new RectI(0, 0, result.TableBounds.Width, 8), result.HeaderBounds);
        var frame = OpaqueSurfaceScanner.PixelFrame.Decode(File.ReadAllBytes(result.ImagePath));
        Assert.Equal(DataGridRegistrationTests.Pixel(7, 9), frame.Pixels[((9 + 8) * frame.Width + 7) * 4]);
        var disconnected = capture with { Joins = [] };
        Assert.Throws<InvalidOperationException>(() => GridImageAssembler.Assemble(disconnected, Path.Combine(_directory, "disconnected.png")));
        var first = capture.Tiles.First(t => t.Accepted);
        File.AppendAllText(first.PngPath, "changed");
        Assert.Throws<InvalidOperationException>(() => GridImageAssembler.Assemble(capture, Path.Combine(_directory, "tampered.png")));
        Assert.False(File.Exists(Path.Combine(_directory, "tampered.png")));
    }

    [Fact]
    public async Task ImageExplorationLimitRemainsPartialInTheSavedImageResult()
    {
        var session = new FixtureSession();
        var request = Request(new(MaxMovements: 5)) with { Mode = GridAcquisitionMode.ImageExploration };
        var coordinator = new DataGridAcquisitionCoordinator(GridImageLayout.DescribeAsync, (_, _) => session);
        var capture = await coordinator.AcquireAsync(request);
        var result = GridImageAssembler.Assemble(capture, Path.Combine(_directory, "partial.png"));
        Assert.Equal(GridCaptureStatus.Partial, result.Capture.Status);
        Assert.Contains("movement-limit", result.Reasons);
        Assert.NotNull(result.ImagePath);
    }

    [Fact]
    public async Task BothAxesProduceContinuousPlacedRowsAndVerifiedRestoration()
    {
        var session = new FixtureSession { DuplicateRows = true };
        var result = await Coordinator(session).AcquireAsync(Request());
        Assert.Equal(GridCaptureStatus.Complete, result.Status);
        Assert.True(result.Coverage.HorizontalMovementObserved);
        Assert.True(result.Coverage.VerticalMovementObserved);
        Assert.True(result.Coverage.Continuous);
        Assert.Equal(GridCaptureScope.FullTable, result.Scope);
        Assert.True(result.Coverage.ColumnCoverageComplete);
        Assert.True(result.Coverage.RowCoverageComplete);
        Assert.Equal(GridRestorationStatus.Succeeded, result.Restoration.Status);
        Assert.True(result.Restoration.AnchorVerified);
        Assert.Equal(15, result.Tiles.Where(tile => tile.Accepted).SelectMany(tile => tile.Rows)
            .Where(row => row.IsComplete).Select(row => row.RowOccurrenceId).Distinct().Count());
        Assert.Contains(result.Joins, join => join.Accepted && join.DisplacementY == 8); // short terminal step
        Assert.Contains(result.Joins, join => join.Accepted && join.DisplacementX == 22);
        Assert.All(result.Tiles, tile => Assert.True(File.Exists(tile.PngPath)));
        Assert.True(File.Exists(result.ManifestPath));
        Assert.Equal(0, session.X);
        Assert.Equal(0, session.Y);
        Assert.All(result.Joins.Where(join => join.Accepted), join =>
        {
            var from = result.Tiles.Single(tile => tile.TileId == join.FromTileId);
            var to = result.Tiles.Single(tile => tile.TileId == join.ToTileId);
            Assert.Equal(join.DisplacementX, to.OffsetX - from.OffsetX);
            Assert.Equal(join.DisplacementY, to.OffsetY - from.OffsetY);
        });
    }

    [Fact]
    public async Task SurveyPreservesDuplicateLabelsWithSeparatePhysicalKeys()
    {
        var session = new FixtureSession();
        var request = Request() with { Mode = GridAcquisitionMode.MappingSurvey,
            Definition = Definition() with { Schema = Schema() with { Columns = [], IsComplete = false, KnownColumnCount = null } } };
        var result = await Coordinator(session).AcquireAsync(request);
        Assert.Equal(GridCaptureStatus.Complete, result.Status);
        Assert.Equal(10, result.Schema.Columns.Count);
        Assert.Equal(10, result.Schema.Columns.Select(column => column.ColumnKey).Distinct().Count());
        Assert.All(result.Schema.Columns, column => Assert.Equal("Repeated label", column.Label));
    }

    [Fact]
    public async Task UnapprovedRequestNeverCreatesInputSession()
    {
        var created = false;
        var coordinator = new DataGridAcquisitionCoordinator((_, _) => throw new Exception(), (_, _) => { created = true; throw new Exception(); });
        var result = await coordinator.AcquireAsync(Request() with { Approval = null });
        Assert.False(created);
        Assert.Equal(GridCaptureStatus.Failed, result.Status);
        Assert.Contains("approval-required", result.Reasons);
    }

    [Fact]
    public async Task ChangedApprovalTargetIsRejected()
    {
        var session = new FixtureSession();
        var request = Request();
        request = request with { Target = request.Target with { ProcessId = 999 } };
        var result = await Coordinator(session).AcquireAsync(request);
        Assert.Empty(session.Input);
        Assert.Contains("approval-does-not-match-operation", result.Reasons);
    }

    [Fact]
    public async Task CommonGateRejectsConcurrentAcquisitionWithoutQueueing()
    {
        using var lease = DataGridOperationGate.TryAcquire();
        Assert.NotNull(lease);
        var session = new FixtureSession();
        var result = await Coordinator(session).AcquireAsync(Request());
        Assert.Contains("operation-busy", result.Reasons);
        Assert.Empty(session.Input);
        Assert.Null(DataGridOperationGate.TryAcquire());
    }

    [Fact]
    public async Task CommonGateIsObservedByAnotherProcess()
    {
        using var lease = DataGridOperationGate.TryAcquire();
        Assert.NotNull(lease);
        var name = DataGridOperationGate.SharedName();
        using var child = System.Diagnostics.Process.Start(DataGridNativeBoundaryTests.Child(
            "$s=[Threading.Semaphore]::OpenExisting('" + name + "'); try { [Console]::Out.Write($s.WaitOne(0)) } finally { $s.Dispose() }"))!;
        var output = await child.StandardOutput.ReadToEndAsync();
        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, child.ExitCode);
        Assert.Equal("False", output);
    }

    [Fact]
    public async Task OneViewportGateFailureNeverScrolls()
    {
        var session = new FixtureSession { Qualified = false };
        var result = await Coordinator(session).AcquireAsync(Request());
        Assert.Empty(session.Input);
        Assert.Contains("one-viewport-reading-unqualified", result.Reasons);
        Assert.Equal(GridAcquisitionStage.Probe, result.Stage);
    }

    [Fact]
    public async Task ShortBudgetStopsTruthfullyWithoutSpendingRestorationReserve()
    {
        var session = new FixtureSession();
        var result = await Coordinator(session).AcquireAsync(Request(new(MaxDurationMs: 1_000, MaxMovements: 1, MaxTiles: 1)));
        Assert.Equal(GridCaptureStatus.Failed, result.Status);
        Assert.Contains("duration-limit", result.Reasons);
        Assert.Empty(session.Input);
    }

    [Fact]
    public async Task MovementBudgetReturnsSubsetAndCountsRestoration()
    {
        var session = new FixtureSession();
        var result = await Coordinator(session).AcquireAsync(Request(new(MaxMovements: 5)));
        Assert.Equal(GridCaptureStatus.Partial, result.Status);
        Assert.Contains("movement-limit", result.Reasons);
        Assert.True(result.Movements.Count <= 5);
        Assert.Equal(GridRestorationStatus.Succeeded, result.Restoration.Status);
        Assert.False(result.Coverage.BottomBoundary);
    }

    [Fact]
    public async Task CancellationAllowsOnlyBoundedSafeRestoration()
    {
        using var cancellation = new CancellationTokenSource();
        var session = new FixtureSession();
        session.AfterInput = count => { if (count == 3) cancellation.Cancel(); };
        var result = await Coordinator(session).AcquireAsync(Request(), cancellationToken: cancellation.Token);
        Assert.Contains("cancelled", result.Reasons);
        Assert.Equal(GridRestorationStatus.Succeeded, result.Restoration.Status);
        Assert.All(session.Input.Skip(3), input => Assert.True(input.Restoration));
    }

    [Theory]
    [InlineData("human-takeover")]
    [InlineData("target-lost")]
    [InlineData("foreground-lost")]
    public async Task UnsafeStopProhibitsEverySubsequentInputIncludingRestoration(string reason)
    {
        var session = new FixtureSession();
        session.AfterInput = count => { if (count == 3) session.UnsafeReason = reason; };
        var result = await Coordinator(session).AcquireAsync(Request());
        Assert.Equal(3, session.Input.Count);
        Assert.Contains(reason, result.Reasons);
        Assert.Equal(GridRestorationStatus.SafelySkipped, result.Restoration.Status);
    }

    [Fact]
    public async Task NoChangeWithoutNativeBoundaryRemainsPartial()
    {
        var session = new FixtureSession { RefuseForwardHorizontal = true };
        var result = await Coordinator(session).AcquireAsync(Request());
        Assert.Equal(GridCaptureStatus.Partial, result.Status);
        Assert.Contains("no-progress-without-boundary", result.Reasons);
        Assert.False(result.Coverage.RightBoundary);
    }

    [Fact]
    public async Task RestorationOutcomeDoesNotRewriteCompleteCapture()
    {
        var session = new FixtureSession { CorruptRestoredAnchor = true };
        var result = await Coordinator(session).AcquireAsync(Request());
        Assert.Equal(GridCaptureStatus.Complete, result.Status);
        Assert.Equal(GridRestorationStatus.Failed, result.Restoration.Status);
        Assert.False(result.Restoration.AnchorVerified);
    }

    [Fact]
    public async Task OptimizedFullImageReadCoalescesSmallStepsAndReversesIgnoredThumbRestoration()
    {
        var session = new FixtureSession { VerticalStep = 4, IgnoreVerticalPosition = true };
        var limits = new GridCaptureLimits(120_000, 192, 96, RestorationReserveMs: 30_000, RestorationReserveMovements: 64);
        var result = await Coordinator(session).AcquireAsync(Request(limits) with
        { Mode = GridAcquisitionMode.ImageExploration, OptimizeImageTraversal = true });
        Assert.Equal(GridCaptureStatus.Complete, result.Status);
        Assert.True(result.Coverage.RowCoverageComplete); Assert.True(result.Coverage.ColumnCoverageComplete);
        Assert.Equal(GridRestorationStatus.Succeeded, result.Restoration.Status);
        Assert.Equal(0, session.Y); Assert.Equal(0, session.X);
        Assert.True(result.Tiles.Count < 30);
    }

    [Fact]
    public async Task OptimizedReadStillProbesPixelsWhenNativeBottomFlagIsPremature()
    {
        var session = new FixtureSession { PrematureBottom = true };
        var limits = new GridCaptureLimits(120_000, 192, 96, RestorationReserveMs: 30_000, RestorationReserveMovements: 64);
        var result = await Coordinator(session).AcquireAsync(Request(limits) with
        { Mode = GridAcquisitionMode.ImageExploration, OptimizeImageTraversal = true });
        Assert.Equal(GridCaptureStatus.Complete, result.Status);
        Assert.Equal(56, result.Tiles.Where(t => t.Accepted).Max(t => t.OffsetY));
        Assert.Equal(GridRestorationStatus.Succeeded, result.Restoration.Status);
    }

    private DataGridAcquisitionCoordinator Coordinator(FixtureSession session) => new(session.DescribeAsync, (_, _) => session);
    private AcquisitionRequest Request(GridCaptureLimits? limits = null)
    {
        limits ??= new();
        var target = new GridTargetIdentity(1, 1, 1, DateTimeOffset.UnixEpoch, 2, new(0, 0, 96, 72), new(0, 0, 96, 72));
        return new(Definition(), GridAcquisitionMode.FreshRead, limits, target,
            new("grid", target, limits, true, true, DateTimeOffset.UtcNow), _directory, "test-acquisition", true);
    }
    private static MappedGridDefinition Definition() => new("grid", "map", "surface", "host", "Synthetic table",
        new("fixture", "Window", "", [], new("Grid")), new(96, 72, new(0, 0, 96, 72), new(0, 0, 96, 8), new(0, 8, 96, 64), new(0, 0, 96, 72)),
        GridScrollAxes.Horizontal | GridScrollAxes.Vertical, Schema(), GridQualification.ReadingVerified, "test", DateTimeOffset.UnixEpoch);
    private static GridSchema Schema() => new("v1", true, 10,
        Enumerable.Range(0, 10).Select(index => new GridColumn($"column-{index:D4}", index, "Repeated label", index * 15, (index + 1) * 15)).ToArray(), 8, 8);

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private sealed class FixtureSession : IDataGridCaptureSession
    {
        public int X { get; set; }
        public int Y { get; set; }
        public bool Qualified { get; init; } = true;
        public bool DuplicateRows { get; init; }
        public bool RefuseForwardHorizontal { get; init; }
        public bool CorruptRestoredAnchor { get; init; }
        public bool IgnoreVerticalPosition { get; init; }
        public bool VerticalResetsHorizontal { get; init; }
        public int VerticalStep { get; init; } = 24;
        public bool PrematureBottom { get; init; }
        public string? UnsafeReason { get; set; }
        public Action<int>? AfterInput { get; set; }
        public List<(GridScrollAxis Axis, bool Restoration)> Input { get; } = [];
        public bool HumanTakeover => UnsafeReason == "human-takeover";
        public string? CheckSafety() => UnsafeReason;
        public GridScrollPosition ReadPosition() => new(new(true, 0, 54, 96, X, X == 0, X == 54), new(true, 0, 56, 64, Y, Y == 0, Y == 56 || PrematureBottom && Y >= 24));
        private bool _restored;

        public bool Move(GridScrollAxis axis, int direction, int? exactPosition = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Input.Add((axis, exactPosition.HasValue));
            if (axis == GridScrollAxis.Horizontal)
                X = exactPosition ?? Math.Clamp(X + (RefuseForwardHorizontal && direction > 0 ? 0 : direction * 32), 0, 54);
            else
            {
                Y = exactPosition.HasValue && IgnoreVerticalPosition ? Y : exactPosition ?? Math.Clamp(Y + direction * VerticalStep, 0, 56);
                if (VerticalResetsHorizontal) X = 0;
                if (exactPosition.HasValue) _restored = true;
            }
            AfterInput?.Invoke(Input.Count);
            return true;
        }

        public Task<GridViewportRequest> ObserveAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pixels = new byte[96 * 72];
            for (var y = 0; y < 72; y++)
            for (var x = 0; x < 96; x++)
            {
                var actualY = y < 8 ? y - 100 : y - 8 + Y;
                if (DuplicateRows && actualY is >= 40 and < 48) actualY -= 8;
                pixels[y * 96 + x] = DataGridRegistrationTests.Pixel(x + X, actualY);
            }
            if (_restored && CorruptRestoredAnchor) pixels[96 * 20 + 10] ^= 255;
            var bitmap = BitmapSource.Create(96, 72, 96, 96, PixelFormats.Gray8, null, pixels, 96);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            return Task.FromResult(new GridViewportRequest(stream.ToArray(), new(0, 0, 96, 72), new(0, 0, 96, 8), new(0, 8, 96, 64), Schema()));
        }

        public Task<GridViewportDescription> DescribeAsync(GridViewportRequest request, CancellationToken cancellationToken)
        {
            var rows = new List<GridViewportRow>();
            for (var row = Y / 8; row * 8 < Y + 64; row++)
            {
                var top = Math.Max(row * 8, Y);
                var bottom = Math.Min((row + 1) * 8, Y + 64);
                rows.Add(new(row, new(0, top - Y + 8, 96, bottom - top), bottom - top < 8));
            }
            var columns = Schema().Columns.Where(column => column.EndX > X && column.StartX < X + 96)
                .Select(column => new GridViewportColumn(column.ColumnKey, column.Ordinal,
                    new((int)Math.Max(column.StartX - X, 0), 0, (int)(Math.Min(column.EndX, X + 96) - Math.Max(column.StartX, X)), 8),
                    new(0, 8, 96, 64), column.StartX < X || column.EndX > X + 96, column.Label, GridCellReadStatus.Text)).ToArray();
            return Task.FromResult(new GridViewportDescription(Schema(), columns, rows, 8, true, Qualified, "synthetic", null, []));
        }
        public void Dispose() { }
    }
}
