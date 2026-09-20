using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using UiAtlas.Core.Contracts;

namespace UiAtlas.Core.Recording.Windows;

/// <summary>Shared bounded observe/move/observe/verify service for mapper and MCP.</summary>
public sealed class DataGridAcquisitionCoordinator
{
    private readonly Func<GridViewportRequest, CancellationToken, Task<GridViewportDescription>> _describe;
    private readonly Func<MappedGridDefinition, GridTargetIdentity, IDataGridCaptureSession> _sessionFactory;

    public DataGridAcquisitionCoordinator() : this(new TableViewportDescriber()) { }
    public DataGridAcquisitionCoordinator(TableViewportDescriber describer)
        : this(describer.DescribeAsync, (definition, target) => new DataGridCaptureSession(definition, target)) { }

    internal DataGridAcquisitionCoordinator(
        Func<GridViewportRequest, CancellationToken, Task<GridViewportDescription>> describe,
        Func<MappedGridDefinition, GridTargetIdentity, IDataGridCaptureSession> sessionFactory)
    { _describe = describe; _sessionFactory = sessionFactory; }

    /// <summary>A single read-only current viewport. It never scrolls or makes qualification claims.</summary>
    public async Task<GridViewportRequest> CaptureViewportAsync(
        MappedGridDefinition definition, GridTargetIdentity target, CancellationToken cancellationToken = default)
    {
        using var gate = DataGridOperationGate.TryAcquire() ?? throw new InvalidOperationException("operation-busy");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        using var session = _sessionFactory(definition, target);
        return await session.ObserveAsync(deadline.Token).ConfigureAwait(false);
    }

    public Task<CapturedGrid> AcquireAsync(
        AcquisitionRequest request, IProgress<AcquisitionProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        new Run(request, progress, cancellationToken, _describe, _sessionFactory).ExecuteAsync();

    private sealed class Run(
        AcquisitionRequest request,
        IProgress<AcquisitionProgress>? progress,
        CancellationToken cancellation,
        Func<GridViewportRequest, CancellationToken, Task<GridViewportDescription>> describe,
        Func<MappedGridDefinition, GridTargetIdentity, IDataGridCaptureSession> sessionFactory)
    {
        private readonly Stopwatch _timer = Stopwatch.StartNew();
        private readonly DateTimeOffset _started = DateTimeOffset.UtcNow;
        private readonly List<GridCapturedTile> _tiles = [];
        private readonly List<GridJoinEvidence> _joins = [];
        private readonly List<GridMovementReceipt> _movements = [];
        private readonly Dictionary<GridScrollAxis, (int Direction, GridScrollPosition Before, GridScrollPosition After)> _lastLineMoves = [];
        private readonly List<(GridScrollAxis Axis, int Direction, GridScrollPosition Before, GridScrollPosition After)> _lineHistory = [];
        private readonly Dictionary<GridScrollAxis, int> _lastPixelStep = [];
        private readonly List<string> _reasons = [];
        private readonly List<string> _gaps = [];
        private readonly List<GridColumn> _columns = [];
        private GridSchema _schema = request.Definition.Schema;
        private GridAcquisitionStage _stage = GridAcquisitionStage.Bind;
        private DataGridEvidenceStore? _evidence;
        private IDataGridCaptureSession? _session;
        private View? _original;
        private GridScrollPosition? _originalPosition;
        private View? _current;
        private long _pngBytes;
        private bool _inputAttempted;
        private bool _unsafeToRestore;
        private bool _left, _top, _right, _bottom, _horizontalMoved, _verticalMoved, _originKnown, _schemaFrozen;
        private double _pitch, _rowOrigin;
        private int _x, _y;
        private double _preciseX, _preciseY;
        private CancellationToken _acquisitionToken;
        private CancellationToken _deadlineToken;
        private bool RowBandOnly => request.Scope == GridCaptureScope.VisibleRowBand;

        public async Task<CapturedGrid> ExecuteAsync()
        {
            GridRestorationResult restoration = new(GridRestorationStatus.SafelySkipped, "no-input-issued", false, false);
            using var deadline = new CancellationTokenSource();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, deadline.Token);
            DataGridOperationGate? gate = null;
            try
            {
                ValidateRequest();
                deadline.CancelAfter(request.Limits.MaxDurationMs);
                _deadlineToken = deadline.Token;
                _acquisitionToken = linked.Token;
                gate = DataGridOperationGate.TryAcquire();
                if (gate is null) Stop("operation-busy", GridAcquisitionStage.Bind);
                _evidence = new(request.EvidenceDirectory);
                _session = sessionFactory(request.Definition, request.Target);
                EnsureSafe();
                _originalPosition = _session.ReadPosition();
                Report(GridAcquisitionStage.Probe, "reading-starting-viewport");
                _original = _current = await ObserveAsync(false).ConfigureAwait(false);
                var initial = await DescribeAsync(_current, provisional: true).ConfigureAwait(false);
                RequireReadable(initial);
                _pitch = initial.RowPitch;
                EnsureAxisCapabilities(_originalPosition);

                Report(GridAcquisitionStage.Capture, "establishing-left-boundary");
                if (Allows(GridScrollAxis.Horizontal)) while (!await StepAsync(GridScrollAxis.Horizontal, -1).ConfigureAwait(false)) { }
                _left = true;
                if (Allows(GridScrollAxis.Vertical)) while (!await StepAsync(GridScrollAxis.Vertical, -1).ConfigureAwait(false)) { }
                var originPosition = _session.ReadPosition();
                _top = !RowBandOnly || originPosition.Vertical.Supported && originPosition.Vertical.AtStart;
                _bottom = RowBandOnly && originPosition.Vertical.Supported && originPosition.Vertical.AtEnd;
                // Boundary navigation had an arbitrary initial origin. Normalize all retained
                // navigation receipts too so every accepted join has the same coordinate basis.
                for (var index = 0; index < _tiles.Count; index++)
                    _tiles[index] = _tiles[index] with { OffsetX = _tiles[index].OffsetX - _x, OffsetY = _tiles[index].OffsetY - _y };
                _x = _y = 0;
                _preciseX = _preciseY = 0;
                _originKnown = true;
                var origin = await DescribeAsync(_current!, provisional: request.Mode == GridAcquisitionMode.MappingSurvey).ConfigureAwait(false);
                RequireReadable(origin);
                _rowOrigin = origin.Rows.Where(row => !row.IsPartial).Select(row => (double)row.Bounds.Y - _current!.Raw.BodyBounds.Y).DefaultIfEmpty(0).Min();
                if (!RowBandOnly && _rowOrigin > 2) Stop("top-row-origin-unproven", GridAcquisitionStage.Schema);
                AcceptCurrent(origin);

                while (true)
                {
                    var bandAnchor = _current!;
                    var bandPosition = _session.ReadPosition();
                    var bandY = _y;
                    if (Allows(GridScrollAxis.Horizontal)) while (!await StepAsync(GridScrollAxis.Horizontal, 1).ConfigureAwait(false)) { }
                    _right = true;
                    if (!_schemaFrozen) FreezeSchema();
                    // Exploration establishes columns at the current vertical position.
                    // Restoration returns directly to the starting view; another row band is out of scope.
                    if (RowBandOnly) break;
                    if (_x != 0)
                    {
                        await ReturnToBandStartAsync(bandAnchor, bandPosition, bandY).ConfigureAwait(false);
                    }
                    if (!Allows(GridScrollAxis.Vertical) || await StepAsync(GridScrollAxis.Vertical, 1).ConfigureAwait(false))
                    { _bottom = true; break; }
                }
                _stage = GridAcquisitionStage.Verify;
            }
            catch (GridAcquisitionStoppedException exception)
            {
                _stage = exception.Stage;
                _unsafeToRestore |= exception.UnsafeToRestore;
                _reasons.Add(exception.Message);
                if (exception.Stage is GridAcquisitionStage.Join or GridAcquisitionStage.Schema)
                    _gaps.Add(exception.Message);
            }
            catch (OperationCanceledException)
            {
                _reasons.Add(_session?.HumanTakeover == true ? "human-takeover" : cancellation.IsCancellationRequested ? "cancelled" : "duration-limit");
                _unsafeToRestore |= _session?.HumanTakeover == true;
            }
            catch (Exception exception)
            {
                _reasons.Add($"acquisition-failed:{exception.GetType().Name}");
            }
            finally
            {
                if (_session is not null)
                {
                    restoration = await RestoreAsync().ConfigureAwait(false);
                    if (_session.HumanTakeover && _session.HumanTakeoverEvent is { } inputEvent)
                        _reasons.Add("takeover-event:" + inputEvent);
                    _session.Dispose();
                }
                gate?.Dispose();
            }

            var accepted = _tiles.Where(tile => tile.Accepted).ToArray();
            var complete = _left && _right && (RowBandOnly || _top && _bottom) && _originKnown &&
                (request.Mode == GridAcquisitionMode.ImageExploration || _schema.IsComplete) && _gaps.Count == 0 && _reasons.Count == 0;
            var status = complete ? GridCaptureStatus.Complete : accepted.Length > 0 ? GridCaptureStatus.Partial : GridCaptureStatus.Failed;
            var result = new CapturedGrid(request.AcquisitionId, request.Definition.GridId, _schema, request.Target,
                _started, DateTimeOffset.UtcNow, status, _stage, _reasons.ToArray(), _tiles.ToArray(), _joins.ToArray(), _movements.ToArray(),
                new(_left, _top, _right, _bottom, _originKnown && _gaps.Count == 0, _horizontalMoved, _verticalMoved,
                    accepted.Select(tile => new RectI(tile.OffsetX, tile.OffsetY, tile.BodyBounds.Width, tile.BodyBounds.Height)).ToArray(), _gaps.ToArray()),
                restoration, _evidence?.ManifestPath ?? "", request.Limits, request.Mode, request.Approval?.ApprovedUtc, request.Scope);
            try { _evidence?.SaveManifest(result); }
            catch (Exception exception)
            {
                result = result with { Status = accepted.Length > 0 ? GridCaptureStatus.Partial : GridCaptureStatus.Failed,
                    Reasons = result.Reasons.Append($"manifest-write-failed:{exception.GetType().Name}").ToArray(), ManifestPath = "" };
            }
            Report(_stage, string.Join(';', result.Reasons), GridAcquisitionLifecycle.Finished);
            return result;
        }

        private void ValidateRequest()
        {
            if (!Enum.IsDefined(request.Scope) || RowBandOnly && request.Mode != GridAcquisitionMode.ImageExploration)
                Stop("invalid-capture-scope", GridAcquisitionStage.Bind);
            if (string.IsNullOrWhiteSpace(request.AcquisitionId) || request.AcquisitionId.Length > 128 ||
                string.IsNullOrWhiteSpace(request.EvidenceDirectory)) Stop("invalid-request", GridAcquisitionStage.Bind);
            var limits = request.Limits;
            var optimized = request.OptimizeImageTraversal && request.Mode == GridAcquisitionMode.ImageExploration && !RowBandOnly;
            if (request.OptimizeImageTraversal && !optimized || limits.MaxDurationMs < 1 || limits.MaxDurationMs > (optimized ? 120_000 : 60_000) ||
                limits.MaxMovements < 1 || limits.MaxMovements > (optimized ? 192 : 32) ||
                limits.MaxTiles < 1 || limits.MaxTiles > (optimized ? 96 : 24) || limits.MaxPngBytes is < 1 or > 64L * 1024 * 1024 ||
                limits.RestorationReserveMs < 0 || limits.RestorationReserveMs > (optimized ? 30_000 : 5_000) ||
                limits.RestorationReserveMovements < 0 || limits.RestorationReserveMovements > (optimized ? 64 : 2))
                Stop("invalid-limits", GridAcquisitionStage.Bind);
            var approval = request.Approval;
            if (approval is null)
            {
                Report(GridAcquisitionStage.Bind, "approval-required", GridAcquisitionLifecycle.AwaitingApproval);
                Stop("approval-required", GridAcquisitionStage.Bind);
            }
            if (approval!.GridId != request.Definition.GridId || approval.Target != request.Target || approval.Limits != limits ||
                approval.ApprovedUtc > DateTimeOffset.UtcNow.AddSeconds(5) || DateTimeOffset.UtcNow - approval.ApprovedUtc > TimeSpan.FromMinutes(5) ||
                Allows(GridScrollAxis.Horizontal) && !approval.AllowHorizontal || Allows(GridScrollAxis.Vertical) && !approval.AllowVertical)
                Stop("approval-does-not-match-operation", GridAcquisitionStage.Bind);
            if (request.Mode == GridAcquisitionMode.FreshRead && !request.Definition.Schema.IsComplete)
                Stop("saved-schema-incomplete", GridAcquisitionStage.Schema);
        }

        private void EnsureAxisCapabilities(GridScrollPosition position)
        {
            foreach (var axis in new[] { GridScrollAxis.Horizontal, GridScrollAxis.Vertical })
            {
                if (RowBandOnly && axis == GridScrollAxis.Vertical) continue;
                var state = Axis(position, axis);
                if (Allows(axis) && !state.Supported) Stop($"{axis.ToString().ToLowerInvariant()}-scroll-unsupported", GridAcquisitionStage.Probe);
                if (!Allows(axis) && (!state.Supported || !state.AtStart || !state.AtEnd))
                    Stop($"{axis.ToString().ToLowerInvariant()}-boundary-unproven", GridAcquisitionStage.Probe);
            }
        }

        private async Task<View> ObserveAsync(bool restoration)
        {
            CheckBudget(restoration, movement: false);
            var raw = await _session!.ObserveAsync(restoration ? _deadlineToken : _acquisitionToken).ConfigureAwait(false);
            var reservedBytes = restoration ? 0 : _original?.Raw.Png.LongLength ?? 0;
            if (raw.Png.LongLength > request.Limits.MaxPngBytes - _pngBytes - reservedBytes)
                Stop("png-byte-limit", GridAcquisitionStage.Capture);
            _pngBytes += raw.Png.LongLength;
            var tile = _evidence!.Save(raw, _tiles.Count + 1);
            tile = tile with { OffsetX = _x, OffsetY = _y };
            _tiles.Add(tile);
            var view = new View(raw, tile.TileId, DataGridEvidenceStore.Pixels(raw, raw.BodyBounds),
                DataGridEvidenceStore.Pixels(raw, raw.HeaderBounds));
            Report(restoration ? GridAcquisitionStage.Restore : GridAcquisitionStage.Capture, "viewport-captured");
            return view;
        }

        private async Task<GridViewportDescription> DescribeAsync(View view, bool provisional)
        {
            var schema = provisional ? _schema with { Columns = [], IsComplete = false, KnownColumnCount = null } : _schema;
            var result = await describe(view.Raw with { Schema = schema, OffsetX = _x, OffsetY = _y,
                LocalOcrQualified = request.LocalOcrQualified, VerifyHeaderLabels = !provisional }, _acquisitionToken).ConfigureAwait(false);
            if (_pitch > 0 && Math.Abs(result.RowPitch - _pitch) > 1)
                Stop("row-pitch-changed", GridAcquisitionStage.Schema);
            return result;
        }

        private void RequireReadable(GridViewportDescription description)
        {
            if (!description.StructureVerified) Stop("viewport-structure-unproven:" + string.Join(';', description.Reasons.Take(4)), GridAcquisitionStage.Schema);
            if (request.Mode != GridAcquisitionMode.ImageExploration && !description.ReadingQualified)
                Stop("one-viewport-reading-unqualified", GridAcquisitionStage.Probe);
        }

        // Returns true only for unchanged pixels together with current native edge evidence.
        private async Task<bool> StepAsync(GridScrollAxis axis, int direction)
        {
            var before = _current!;
            var beforePosition = _session!.ReadPosition();
            var alreadyAtEdge = request.Mode == GridAcquisitionMode.ImageExploration &&
                (direction < 0 ? Axis(beforePosition, axis).AtStart : Axis(beforePosition, axis).AtEnd);
            var count = 1;
            if (request.OptimizeImageTraversal && !alreadyAtEdge && _lastPixelStep.TryGetValue(axis, out var pixels) && pixels > 0)
                count = Math.Clamp((axis == GridScrollAxis.Horizontal ? before.Body.Width : before.Body.Height) * 2 / 5 / pixels, 1, 8);
            var issued = 0;
            if (!alreadyAtEdge || request.OptimizeImageTraversal)
                for (var index = 0; index < count; index++)
                {
                    await MoveAsync(axis, direction, null, restoration: false).ConfigureAwait(false);
                    issued++;
                    var position = Axis(_session.ReadPosition(), axis);
                    if (direction < 0 ? position.AtStart : position.AtEnd) break;
                }
            var after = await ObserveAsync(false).ConfigureAwait(false);
            var afterPosition = _session.ReadPosition();
            var registration = request.Mode == GridAcquisitionMode.ImageExploration && axis == GridScrollAxis.Horizontal
                ? GridHorizontalRegistration.Register(before.Body, after.Body, before.Header, after.Header, direction, _acquisitionToken)
                : request.Mode == GridAcquisitionMode.ImageExploration
                ? GridVerticalRegistration.Register(before.Body, after.Body, direction, _acquisitionToken)
                : DataGridRegistration.Register(before.Body, after.Body, axis, direction,
                    (int)Math.Max(12, Math.Ceiling(_pitch)), _acquisitionToken);
            if (axis == GridScrollAxis.Vertical && !DataGridRegistration.SameAnchor(before.Header, after.Header))
                registration = registration with { Accepted = false, NoChange = false, Reason = "header-changed-during-vertical-step" };
            var other = axis == GridScrollAxis.Horizontal ? GridScrollAxis.Vertical : GridScrollAxis.Horizontal;
            if (Axis(beforePosition, other).Position != Axis(afterPosition, other).Position)
                registration = registration with { Accepted = false, NoChange = false, Reason = "cross-axis-position-changed" };
            if (!SameRanges(beforePosition, afterPosition))
                registration = registration with { Accepted = false, NoChange = false, Reason = "scroll-range-changed-during-capture" };
            var boundary = registration.NoChange && (direction < 0 ? Axis(afterPosition, axis).AtStart : Axis(afterPosition, axis).AtEnd);
            if (registration.Accepted && axis == GridScrollAxis.Horizontal)
            {
                _preciseX += registration.SubpixelDisplacementX ?? registration.DisplacementX;
                registration = registration with { DisplacementX = checked((int)Math.Round(_preciseX) - _x) };
            }
            if (registration.Accepted && axis == GridScrollAxis.Vertical)
            {
                _preciseY += registration.SubpixelDisplacementY ?? registration.DisplacementY;
                registration = registration with { DisplacementY = checked((int)Math.Round(_preciseY) - _y) };
            }
            _joins.Add(new(before.TileId, after.TileId, axis, registration.Accepted, registration.DisplacementX,
                registration.DisplacementY, registration.CompatibleCandidates, registration.ComparedPixels,
                registration.MatchFraction, boundary ? "unchanged-with-native-boundary" : registration.Reason));
            if (boundary) return true;
            if (!registration.Accepted) Stop(registration.NoChange ? "no-progress-without-boundary" : registration.Reason, GridAcquisitionStage.Join);
            _lastPixelStep[axis] = Math.Max(1, (int)Math.Ceiling(Math.Abs(axis == GridScrollAxis.Horizontal ? registration.DisplacementX : registration.DisplacementY) / (double)Math.Max(1, issued)));
            _x = checked(_x + registration.DisplacementX);
            _y = checked(_y + registration.DisplacementY);
            var afterTileIndex = _tiles.FindIndex(tile => tile.TileId == after.TileId);
            _tiles[afterTileIndex] = _tiles[afterTileIndex] with { OffsetX = _x, OffsetY = _y };
            _current = after;
            if (axis == GridScrollAxis.Horizontal) _horizontalMoved = true; else _verticalMoved = true;
            var description = await DescribeAsync(after, provisional: !_originKnown || !_schemaFrozen && request.Mode == GridAcquisitionMode.MappingSurvey).ConfigureAwait(false);
            RequireReadable(description);
            if (_originKnown) AcceptCurrent(description);
            return false;
        }

        private async Task MoveAsync(GridScrollAxis axis, int direction, int? exactPosition, bool restoration)
        {
            CheckBudget(restoration, movement: true);
            // Never issue a capture movement that cannot be followed by a retained observation.
            if (!restoration) CheckBudget(false, movement: false);
            EnsureSafe();
            var token = restoration ? _deadlineToken : _acquisitionToken;
            token.ThrowIfCancellationRequested();
            var before = _session!.ReadPosition();
            var started = DateTimeOffset.UtcNow;
            var outcome = "input-failed";
            _inputAttempted = true;
            try
            {
                token.ThrowIfCancellationRequested();
                if (!_session.Move(axis, direction, exactPosition, token)) Stop("scroll-command-failed", restoration ? GridAcquisitionStage.Restore : GridAcquisitionStage.Capture);
                outcome = "delivered";
            }
            finally
            {
                GridScrollPosition? after = null;
                try { after = _session.ReadPosition(); } catch (Exception) { }
                _movements.Add(new(_movements.Count + 1, axis, exactPosition ?? Axis(before, axis).Position + direction,
                    restoration, started, DateTimeOffset.UtcNow, before, after, outcome));
                if (!restoration && exactPosition is null && outcome == "delivered" && after is not null &&
                    Axis(before, axis).Position != Axis(after, axis).Position)
                {
                    _lastLineMoves[axis] = (direction, before, after);
                    _lineHistory.Add((axis, direction, before, after));
                }
            }
            await Task.Delay(40, token).ConfigureAwait(false);
            EnsureSafe();
        }

        private async Task ReturnToBandStartAsync(View anchor, GridScrollPosition position, int bandY)
        {
            await MoveAsync(GridScrollAxis.Horizontal, -1, position.Horizontal.Position, false).ConfigureAwait(false);
            var returned = await ObserveAsync(false).ConfigureAwait(false);
            var currentPosition = _session!.ReadPosition();
            var matches = DataGridRegistration.SameAnchor(anchor.Body, returned.Body) &&
                          DataGridRegistration.SameAnchor(anchor.Header, returned.Header) &&
                          currentPosition.Horizontal.Position == position.Horizontal.Position &&
                          currentPosition.Vertical.Position == position.Vertical.Position;
            if (matches)
            {
                var returnedIndex = _tiles.FindIndex(tile => tile.TileId == returned.TileId);
                var anchorTile = _tiles.Single(tile => tile.TileId == anchor.TileId);
                // A fresh return image with identical body/header pixels has the same
                // positioned row occurrences. Keep it in the accepted connected graph
                // so the next vertical edge starts from a retained accepted observation.
                _tiles[returnedIndex] = _tiles[returnedIndex] with
                    { OffsetX = 0, OffsetY = bandY, Accepted = true, Rows = anchorTile.Rows };
            }
            _joins.Add(new(anchor.TileId, returned.TileId, GridScrollAxis.Horizontal, matches, 0, 0,
                matches ? 1 : 0, anchor.Body.Gray.Length, matches ? 1 : 0, matches ? "return-anchor-verified" : "return-anchor-mismatch"));
            if (!matches) Stop("horizontal-return-unverified", GridAcquisitionStage.Join);
            _current = returned;
            _x = 0;
            _preciseX = 0;
            _y = bandY;
        }

        private void AcceptCurrent(GridViewportDescription description)
        {
            var current = _current!;
            var rows = new List<GridCapturedRowRegion>();
            foreach (var row in description.Rows)
            {
                var logicalY = _y + row.Bounds.Y - current.Raw.BodyBounds.Y;
                var unrounded = (logicalY - _rowOrigin) / _pitch;
                var index = row.IsPartial ? (int)Math.Floor(unrounded) : (int)Math.Round(unrounded);
                if (!row.IsPartial && Math.Abs(unrounded - index) * _pitch > 2)
                    Stop("row-occurrence-alignment-unproven", GridAcquisitionStage.Join);
                rows.Add(new($"{request.AcquisitionId}:row:{index}", index, row.Bounds, !row.IsPartial));
            }
            var tileIndex = _tiles.FindIndex(tile => tile.TileId == current.TileId);
            _tiles[tileIndex] = _tiles[tileIndex] with { OffsetX = _x, OffsetY = _y, Accepted = true, Rows = rows };
            if (!_schemaFrozen && request.Mode == GridAcquisitionMode.MappingSurvey)
            {
                foreach (var column in description.Columns.Where(column => !column.IsPartial))
                {
                    var start = column.HeaderBounds.X - current.Raw.BodyBounds.X + _x;
                    var end = start + column.HeaderBounds.Width;
                    var existing = _columns.FirstOrDefault(candidate => Math.Abs(candidate.StartX - start) <= 2 && Math.Abs(candidate.EndX - end) <= 2);
                    if (existing is not null)
                    {
                        if (existing.Label != column.Label) Stop("overlapping-header-label-conflict", GridAcquisitionStage.Schema);
                        continue;
                    }
                    if (_columns.Any(candidate => Math.Min(candidate.EndX, end) - Math.Max(candidate.StartX, start) > 2))
                        Stop("overlapping-column-geometry-conflict", GridAcquisitionStage.Schema);
                    _columns.Add(new(column.ColumnKey, 0, column.Label ?? "", start, end));
                }
                _schema = _schema with { Columns = _columns.OrderBy(column => column.StartX).ToArray(), IsComplete = false,
                    KnownColumnCount = null, RowHeight = description.RowPitch, HeaderHeight = current.Raw.HeaderBounds.Height };
            }
        }

        private void FreezeSchema()
        {
            if (request.Mode == GridAcquisitionMode.MappingSurvey)
            {
                var ordered = _columns.OrderBy(column => column.StartX).ToArray();
                if (ordered.Length == 0 || ordered[0].StartX > 2 ||
                    ordered.Zip(ordered.Skip(1)).Any(pair => Math.Abs(pair.First.EndX - pair.Second.StartX) > 2) ||
                    ordered[^1].EndX < _x + _current!.Raw.BodyBounds.Width - 2)
                    Stop("full-width-column-schema-unproven", GridAcquisitionStage.Schema);
                var columns = ordered.Select((column, index) => column with { Ordinal = index, ColumnKey = $"column-{index:D4}" }).ToArray();
                var revision = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|',
                    columns.Select(column => FormattableString.Invariant($"{column.StartX}:{column.EndX}:{column.Label}")))))).ToLowerInvariant()[..16];
                var priorRecordColumn = request.Definition.Schema.Columns.FirstOrDefault(column => column.ColumnKey == request.Definition.Schema.RecordIdColumnKey);
                var recordKey = priorRecordColumn is null ? null : columns.FirstOrDefault(column =>
                    Math.Abs(column.StartX - priorRecordColumn.StartX) <= 2 && Math.Abs(column.EndX - priorRecordColumn.EndX) <= 2)?.ColumnKey;
                _schema = _schema with { Revision = revision, Columns = columns, IsComplete = true, KnownColumnCount = columns.Length, RecordIdColumnKey = recordKey };
            }
            _schemaFrozen = true;
        }

        private async Task<GridRestorationResult> RestoreAsync()
        {
            var started = DateTimeOffset.UtcNow;
            var failureStage = _stage;
            try
            {
                var unsafeReason = _session!.CheckSafety();
                if (_unsafeToRestore || _session.HumanTakeover || unsafeReason is not null)
                    return new(GridRestorationStatus.SafelySkipped, unsafeReason ?? "unsafe-after-stop", false, false, started, DateTimeOffset.UtcNow);
                if (!_inputAttempted)
                    return new(GridRestorationStatus.SafelySkipped, "no-scroll-input-issued", false, false, started, DateTimeOffset.UtcNow);
                if (_original is null || _originalPosition is null)
                    return new(GridRestorationStatus.SafelySkipped, "original-anchor-unavailable", false, false, started, DateTimeOffset.UtcNow);
                Report(GridAcquisitionStage.Restore, "restoring-original-position");
                // Some controls reset horizontal position when vertical position changes.
                foreach (var axis in new[] { GridScrollAxis.Vertical, GridScrollAxis.Horizontal }.Where(Allows))
                {
                    var desired = Axis(_originalPosition, axis).Position;
                    if (Axis(_session.ReadPosition(), axis).Position == desired) continue;
                    await MoveAsync(axis, -1, desired, true).ConfigureAwait(false);
                    var actualPosition = _session.ReadPosition();
                    if (request.OptimizeImageTraversal && Axis(actualPosition, axis).Position != desired)
                    {
                        // Reverse only the measured input chain when a native thumb command is ignored.
                        foreach (var step in _lineHistory.Where(s => s.Axis == axis).Reverse().ToArray())
                        {
                            if (Axis(actualPosition, axis).Position == desired) break;
                            if (!SameRanges(step.After, actualPosition)) break;
                            await MoveAsync(axis, -step.Direction, null, true).ConfigureAwait(false);
                            actualPosition = _session.ReadPosition();
                            // Database grids can move selection to the viewport edge on the first reverse
                            // command. Positions are not pixel distances; final original pixels decide success.
                        }
                    }
                    var other = axis == GridScrollAxis.Horizontal ? GridScrollAxis.Vertical : GridScrollAxis.Horizontal;
                    // If an exact-position command was ignored, undo one observed line movement only
                    // when it directly connects the current state to the original position. Native
                    // units are never assumed to be pixels or a fixed line size. Budgets still apply.
                    if (Axis(actualPosition, axis).Position != desired && _lastLineMoves.TryGetValue(axis, out var line) &&
                        SameRanges(line.Before, actualPosition) && SameRanges(line.After, actualPosition) &&
                        Axis(line.Before, axis).Position == desired &&
                        Axis(line.After, axis).Position == Axis(actualPosition, axis).Position &&
                        Axis(line.Before, other).Position == Axis(line.After, other).Position &&
                        Axis(line.After, other).Position == Axis(actualPosition, other).Position)
                        await MoveAsync(axis, -line.Direction, null, true).ConfigureAwait(false);
                }
                var restored = await ObserveAsync(true).ConfigureAwait(false);
                var actual = _session.ReadPosition();
                var positionVerified = actual.Horizontal.Position == _originalPosition.Horizontal.Position &&
                                       actual.Vertical.Position == _originalPosition.Vertical.Position;
                var anchorVerified = DataGridRegistration.SameAnchor(_original.Body, restored.Body) &&
                                     DataGridRegistration.SameAnchor(_original.Header, restored.Header);
                return new(positionVerified && anchorVerified ? GridRestorationStatus.Succeeded : GridRestorationStatus.Failed,
                    positionVerified && anchorVerified ? "original-position-and-pixels-verified" : "restoration-anchor-or-position-mismatch",
                    positionVerified, anchorVerified, started, DateTimeOffset.UtcNow);
            }
            catch (GridAcquisitionStoppedException exception)
            {
                return new(exception.UnsafeToRestore ? GridRestorationStatus.SafelySkipped : GridRestorationStatus.Failed,
                    exception.Message, false, false, started, DateTimeOffset.UtcNow);
            }
            catch (OperationCanceledException)
            { return new(GridRestorationStatus.Failed, "restoration-duration-limit", false, false, started, DateTimeOffset.UtcNow); }
            catch (Exception exception)
            { return new(GridRestorationStatus.Failed, $"restoration-failed:{exception.GetType().Name}", false, false, started, DateTimeOffset.UtcNow); }
            finally { _stage = failureStage; }
        }

        private void CheckBudget(bool restoration, bool movement)
        {
            var reserveMs = restoration ? 0 : Math.Max(5_000, request.Limits.RestorationReserveMs);
            if (_timer.ElapsedMilliseconds >= request.Limits.MaxDurationMs - reserveMs)
                Stop(restoration ? "restoration-duration-limit" : "duration-limit", restoration ? GridAcquisitionStage.Restore : GridAcquisitionStage.Capture);
            var reserveMoves = restoration ? 0 : Math.Max(request.Limits.RestorationReserveMovements,
                (Allows(GridScrollAxis.Horizontal) ? 1 : 0) + (Allows(GridScrollAxis.Vertical) ? 1 : 0));
            if (movement && _movements.Count >= request.Limits.MaxMovements - reserveMoves)
                Stop("movement-limit", restoration ? GridAcquisitionStage.Restore : GridAcquisitionStage.Capture);
            if (!movement && _tiles.Count >= request.Limits.MaxTiles - (restoration || _original is null ? 0 : 1))
                Stop("tile-limit", restoration ? GridAcquisitionStage.Restore : GridAcquisitionStage.Capture);
            if (!restoration && _original is not null && _pngBytes + _original.Raw.Png.LongLength >= request.Limits.MaxPngBytes)
                Stop("png-byte-limit", GridAcquisitionStage.Capture);
        }

        private void EnsureSafe()
        {
            var reason = _session!.CheckSafety();
            if (reason is not null) Stop(reason, _stage, unsafeToRestore: true);
        }

        private bool Allows(GridScrollAxis axis) => !(RowBandOnly && axis == GridScrollAxis.Vertical) && request.Definition.SupportedAxes.HasFlag(
            axis == GridScrollAxis.Horizontal ? GridScrollAxes.Horizontal : GridScrollAxes.Vertical);
        private static GridAxisPosition Axis(GridScrollPosition position, GridScrollAxis axis) =>
            axis == GridScrollAxis.Horizontal ? position.Horizontal : position.Vertical;
        private static bool SameRanges(GridScrollPosition before, GridScrollPosition after) =>
            SameRange(before.Horizontal, after.Horizontal) && SameRange(before.Vertical, after.Vertical);
        private static bool SameRange(GridAxisPosition before, GridAxisPosition after) =>
            before.Supported == after.Supported && before.Minimum == after.Minimum &&
            before.Maximum == after.Maximum && before.PageSize == after.PageSize;
        private void Report(GridAcquisitionStage stage, string reason, GridAcquisitionLifecycle lifecycle = GridAcquisitionLifecycle.Running)
        {
            _stage = stage;
            try { progress?.Report(new(request.AcquisitionId, lifecycle, stage, _timer.ElapsedMilliseconds, _tiles.Count,
                _tiles.Where(tile => tile.Accepted).SelectMany(tile => tile.Rows).Where(row => row.IsComplete).Select(row => row.RowOccurrenceId).Distinct().Count(),
                _movements.Count, reason.Length > 512 ? reason[..512] : reason)); }
            catch (Exception) { /* A progress observer has no authority to interrupt safe restoration. */ }
        }
        private static void Stop(string reason, GridAcquisitionStage stage, bool unsafeToRestore = false) =>
            throw new GridAcquisitionStoppedException(reason, stage, unsafeToRestore);
        private sealed record View(GridViewportRequest Raw, string TileId, GridPixelBuffer Body, GridPixelBuffer Header);
    }
}
