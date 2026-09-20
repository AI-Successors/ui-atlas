using UiAtlas.Core.Contracts;

namespace UiAtlas.Core.Mcp;

public sealed record GridExplorationArguments(string MapId, string GridId, string RequestId, string? AppId = null,
    string? ReadGridRef = null);

public sealed record GridOperationSnapshot(string AcquisitionId, string MapId, string GridId,
    AcquisitionProgress Progress, bool CancellationRequested, DateTimeOffset CreatedUtc,
    DateTimeOffset? FinishedUtc, DateTimeOffset? ExpiresUtc, GridExplorationResult? Result, string? AppId = null);

public sealed class GridOperationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>One active operation, four completed results, ten minutes. No restart or durable jobs.</summary>
public sealed class GridOperationRegistry : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly Dictionary<string, Operation> _operations = new(StringComparer.Ordinal);
    private readonly Func<GridExplorationArguments, string, IProgress<AcquisitionProgress>, CancellationToken, Task<GridExplorationResult>> _run;
    private readonly Func<GridExplorationArguments, string, AcquisitionProgress, Exception, GridExplorationResult> _failure;
    private readonly Action<string> _releaseEvidence;
    private readonly TimeProvider _time;
    private readonly ITimer _timer;
    private bool _stopping;
    public static readonly TimeSpan Retention = TimeSpan.FromMinutes(10);

    public GridOperationRegistry(
        Func<GridExplorationArguments, string, IProgress<AcquisitionProgress>, CancellationToken, Task<GridExplorationResult>> run,
        Func<GridExplorationArguments, string, AcquisitionProgress, Exception, GridExplorationResult> failure,
        Action<string>? releaseEvidence = null, TimeProvider? timeProvider = null)
    {
        _run = run;
        _failure = failure;
        _releaseEvidence = releaseEvidence ?? (_ => { });
        _time = timeProvider ?? TimeProvider.System;
        _timer = _time.CreateTimer(_ => { lock (_sync) Prune(); }, null,
            TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    public GridOperationSnapshot Start(GridExplorationArguments arguments)
    {
        Validate(arguments);
        lock (_sync)
        {
            Prune();
            if (_stopping) throw new GridOperationException("host_stopping", "The host is stopping; no operation was started.");
            var prior = _operations.Values.SingleOrDefault(x => x.Arguments.RequestId == arguments.RequestId);
            if (prior is not null)
            {
                if (prior.Arguments != arguments)
                    throw new GridOperationException("request_conflict", "This request ID already has different arguments.");
                return Snapshot(prior);
            }
            if (_operations.Values.Any(x => x.FinishedUtc is null))
                throw new GridOperationException("busy", "One grid exploration is already active. Poll or cancel it before starting another.");
            var id = Guid.NewGuid().ToString("N");
            var operation = new Operation(arguments, id, _time.GetUtcNow());
            _operations.Add(id, operation);
            operation.Work = Task.Run(() => RunAsync(operation));
            return Snapshot(operation);
        }
    }

    public GridOperationSnapshot Get(string acquisitionId)
    {
        lock (_sync) { Prune(); return Snapshot(Find(acquisitionId)); }
    }

    public GridOperationSnapshot Cancel(string acquisitionId)
    {
        Operation operation;
        lock (_sync)
        {
            Prune();
            operation = Find(acquisitionId);
            if (operation.FinishedUtc is null) operation.CancellationRequested = true;
        }
        // UI cancellation may dispatch to an STA; never hold the registry lock across callbacks.
        if (operation.CancellationRequested)
        {
            try { operation.Stop.Cancel(); }
            catch (ObjectDisposedException) { /* A concurrent completion/expiry already stopped it. */ }
        }
        lock (_sync) return Snapshot(operation);
    }

    private async Task RunAsync(Operation operation)
    {
        GridExplorationResult result;
        var progress = new InlineProgress(value =>
        {
            lock (_sync)
            {
                if (operation.FinishedUtc is null)
                    operation.Progress = value with { AcquisitionId = operation.Id };
            }
        });
        try
        {
            result = await _run(operation.Arguments, operation.Id, progress, operation.Stop.Token).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            AcquisitionProgress current;
            lock (_sync) current = operation.Progress;
            result = _failure(operation.Arguments, operation.Id, current, error);
        }
        lock (_sync)
        {
            operation.Result = result;
            operation.FinishedUtc = _time.GetUtcNow();
            operation.Progress = operation.Progress with
            {
                Lifecycle = GridAcquisitionLifecycle.Finished, Stage = result.Stage,
                ElapsedMs = (long)(operation.FinishedUtc.Value - operation.CreatedUtc).TotalMilliseconds,
                RowCount = result.Data?.Rows.Count ?? 0, Reason = string.Join("; ", result.Reasons)
            };
            Prune();
        }
    }

    private Operation Find(string id) => _operations.TryGetValue(id, out var value) ? value :
        throw new GridOperationException("unknown_acquisition",
            "Unknown or expired acquisition. It was not restarted. Do not automatically resubmit; a fresh attempt needs a new request ID and approval.");

    private GridOperationSnapshot Snapshot(Operation operation) => new(operation.Id,
        operation.Result?.MapId ?? operation.Arguments.MapId, operation.Result?.GridId ?? operation.Arguments.GridId, operation.Progress,
        operation.CancellationRequested, operation.CreatedUtc, operation.FinishedUtc,
        operation.FinishedUtc + Retention, operation.Result, operation.Arguments.AppId);

    private void Prune()
    {
        var finished = _operations.Values.Where(x => x.FinishedUtc is not null)
            .OrderByDescending(x => x.FinishedUtc).ToArray();
        foreach (var operation in finished.Where((x, index) => index >= 4 || x.FinishedUtc + Retention <= _time.GetUtcNow()))
        {
            _operations.Remove(operation.Id);
            Release(operation);
        }
    }

    private void Release(Operation operation)
    {
        operation.Stop.Dispose();
        try { _releaseEvidence(operation.Id); }
        catch (Exception error) { Console.Error.WriteLine($"Evidence cleanup failed ({operation.Id}): {error.GetType().Name}"); }
    }

    public async ValueTask DisposeAsync()
    {
        Operation[] operations;
        lock (_sync)
        {
            if (_stopping) return;
            _stopping = true;
            _timer.Dispose();
            operations = _operations.Values.ToArray();
            foreach (var operation in operations.Where(x => x.FinishedUtc is null))
                operation.CancellationRequested = true;
        }
        foreach (var operation in operations.Where(x => x.CancellationRequested))
        {
            try { operation.Stop.Cancel(); }
            catch (ObjectDisposedException) { /* Completed and evicted concurrently. */ }
        }
        await Task.WhenAll(operations.Select(x => x.Work)).ConfigureAwait(false);
        lock (_sync)
        {
            foreach (var operation in _operations.Values) Release(operation);
            _operations.Clear();
        }
    }

    public static void Validate(GridExplorationArguments arguments)
    {
        var validTarget = arguments.AppId is null
            ? Storage.LocalArtifactCatalog.IsValidId(arguments.MapId) && Storage.LocalArtifactCatalog.IsValidId(arguments.GridId)
            : Storage.LocalArtifactCatalog.IsValidId(arguments.AppId) && arguments.MapId == "" && arguments.GridId == "";
        if (!validTarget || arguments.ReadGridRef is not null &&
            (arguments.AppId is null || !Storage.LocalArtifactCatalog.IsValidId(arguments.ReadGridRef)) ||
            string.IsNullOrWhiteSpace(arguments.RequestId) || arguments.RequestId.Length > 128 ||
            arguments.RequestId.Any(char.IsControl))
            throw new GridOperationException("invalid_arguments", "Target and request IDs must be bounded identifiers, never paths.");
    }

    private sealed class Operation(GridExplorationArguments arguments, string id, DateTimeOffset createdUtc)
    {
        public GridExplorationArguments Arguments { get; } = arguments;
        public string Id { get; } = id;
        public DateTimeOffset CreatedUtc { get; } = createdUtc;
        public AcquisitionProgress Progress { get; set; } = new(id, GridAcquisitionLifecycle.AwaitingApproval,
            GridAcquisitionStage.Bind, 0, 0, 0, 0, "Preparing fresh binding and local confirmation.");
        public CancellationTokenSource Stop { get; } = new();
        public bool CancellationRequested { get; set; }
        public Task Work { get; set; } = Task.CompletedTask;
        public DateTimeOffset? FinishedUtc { get; set; }
        public GridExplorationResult? Result { get; set; }
    }

    private sealed class InlineProgress(Action<AcquisitionProgress> report) : IProgress<AcquisitionProgress>
    {
        public void Report(AcquisitionProgress value) => report(value);
    }
}
