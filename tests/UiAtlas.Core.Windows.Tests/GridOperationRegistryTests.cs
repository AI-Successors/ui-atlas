using UiAtlas.Core.Contracts;
using UiAtlas.Core.Mcp;
using UiAtlas.Core.Recording.Windows;

namespace UiAtlas.Core.Windows.Tests;

public sealed class GridOperationRegistryTests
{
    [Fact]
    public async Task DuplicateStartAndPollingNeverRepeatWorkAndConflictingRequestsFail()
    {
        var calls = 0;
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var registry = Registry(async (args, id, progress, token) =>
        {
            Interlocked.Increment(ref calls);
            await released.Task.WaitAsync(token);
            return Result(id, args.GridId);
        });
        var arguments = new GridExplorationArguments("map-a", "grid-a", "caller-request");
        var first = registry.Start(arguments);
        var second = registry.Start(arguments);
        Assert.Equal(first.AcquisitionId, second.AcquisitionId);
        Assert.Equal(first.AcquisitionId, registry.Get(first.AcquisitionId).AcquisitionId);
        Assert.Equal("request_conflict", Assert.Throws<GridOperationException>(() => registry.Start(arguments with { GridId = "other-grid" })).Code);
        Assert.Equal("busy", Assert.Throws<GridOperationException>(() => registry.Start(arguments with { RequestId = "another" })).Code);
        released.SetResult();
        await Finish(registry, first.AcquisitionId);
        Assert.Equal(1, calls);
        Assert.Equal(first.AcquisitionId, registry.Start(arguments).AcquisitionId);
    }

    [Fact]
    public async Task CancellationSignalsOnlyTheExistingOperationAndReturnsItsTerminalReceipt()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var registry = Registry(async (args, id, progress, token) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Result(id, args.GridId);
        });
        var operation = registry.Start(new("map", "grid", "cancel-me"));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(registry.Cancel(operation.AcquisitionId).CancellationRequested);
        var finished = await Finish(registry, operation.AcquisitionId);
        Assert.Equal(GridCaptureStatus.Failed, finished.Result!.Status);
        Assert.Contains("cancelled", finished.Result.Reasons);
        Assert.Equal("unknown_acquisition", Assert.Throws<GridOperationException>(() => registry.Cancel("missing")).Code);
    }

    [Fact]
    public async Task CompletedRetentionEvictsTheFifthAndExpiresWithoutRestarting()
    {
        var clock = new ManualClock();
        var released = new List<string>();
        await using var registry = Registry((args, id, _, _) => Task.FromResult(Result(id, args.GridId)), released.Add, clock);
        var ids = new List<string>();
        for (var index = 0; index < 5; index++)
        {
            var operation = registry.Start(new("map", "grid", "request-" + index));
            ids.Add(operation.AcquisitionId);
            await Finish(registry, operation.AcquisitionId);
            clock.Advance(TimeSpan.FromSeconds(1));
        }
        Assert.Equal("unknown_acquisition", Assert.Throws<GridOperationException>(() => registry.Get(ids[0])).Code);
        Assert.Contains(ids[0], released);
        clock.Advance(TimeSpan.FromMinutes(11));
        Assert.Equal("unknown_acquisition", Assert.Throws<GridOperationException>(() => registry.Get(ids[^1])).Code);
        Assert.Equal(5, released.Count);
    }

    [Fact]
    public async Task DisposalCancelsActiveWorkAndWaitsForTheSafeStop()
    {
        var stopped = false;
        var registry = Registry(async (args, id, _, token) =>
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { stopped = true; }
            return Result(id, args.GridId);
        });
        registry.Start(new("map", "grid", "disconnect"));
        await registry.DisposeAsync();
        Assert.True(stopped);
        Assert.Equal("host_stopping", Assert.Throws<GridOperationException>(() => registry.Start(new("map", "grid", "new"))).Code);
    }

    [Theory]
    [InlineData("../outside", "grid")]
    [InlineData("map", "C:\\path")]
    public async Task PathsNeverStart(string map, string grid)
    {
        var called = false;
        await using var registry = Registry((args, id, _, _) => { called = true; return Task.FromResult(Result(id, args.GridId)); });
        Assert.Throws<GridOperationException>(() => registry.Start(new(map, grid, "request")));
        Assert.False(called);
    }

    private static GridOperationRegistry Registry(
        Func<GridExplorationArguments, string, IProgress<AcquisitionProgress>, CancellationToken, Task<GridExplorationResult>> run,
        Action<string>? cleanup = null, TimeProvider? time = null) => new(run,
        (args, id, progress, error) => GridExplorationResult.Failed(progress.Stage,
            error is OperationCanceledException ? "cancelled" : error.GetType().Name), cleanup, time);

    private static GridExplorationResult Result(string id, string grid) =>
        GridExplorationResult.Failed(GridAcquisitionStage.Probe, "test-only-no-live-data");

    private static async Task<GridOperationSnapshot> Finish(GridOperationRegistry registry, string id)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var snapshot = registry.Get(id);
            if (snapshot.Progress.Lifecycle == GridAcquisitionLifecycle.Finished) return snapshot;
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _utc = new(2026, 9, 19, 18, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _utc;
        public void Advance(TimeSpan duration) => _utc += duration;
    }
}
