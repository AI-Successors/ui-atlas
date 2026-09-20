using System.Text.Json.Serialization;

namespace UiAtlas.Core.Contracts;

[JsonConverter(typeof(JsonStringEnumConverter<GridAcquisitionMode>))]
public enum GridAcquisitionMode { MappingSurvey, FreshRead, ImageExploration }

[JsonConverter(typeof(JsonStringEnumConverter<GridCaptureScope>))]
public enum GridCaptureScope { FullTable, VisibleRowBand }

[JsonConverter(typeof(JsonStringEnumConverter<GridCaptureStatus>))]
public enum GridCaptureStatus { Complete, Partial, Failed }

[JsonConverter(typeof(JsonStringEnumConverter<GridAcquisitionLifecycle>))]
public enum GridAcquisitionLifecycle { AwaitingApproval, Running, Finished }

[JsonConverter(typeof(JsonStringEnumConverter<GridAcquisitionStage>))]
public enum GridAcquisitionStage { Bind, Probe, Schema, Capture, Join, Restore, Extract, Verify }

[JsonConverter(typeof(JsonStringEnumConverter<GridScrollAxis>))]
public enum GridScrollAxis { Horizontal, Vertical }

[JsonConverter(typeof(JsonStringEnumConverter<GridRestorationStatus>))]
public enum GridRestorationStatus { Succeeded, Failed, SafelySkipped }

/// <summary>All limits include boundary surveys, return movements and restoration.</summary>
public sealed record GridCaptureLimits(
    int MaxDurationMs = 60_000,
    int MaxMovements = 32,
    int MaxTiles = 24,
    long MaxPngBytes = 64L * 1024 * 1024,
    int RestorationReserveMs = 5_000,
    int RestorationReserveMovements = 2);

/// <summary>A short-lived live identity. Saved HWND values alone never authorize input.</summary>
public sealed record GridTargetIdentity(
    long SelectedHwnd,
    long RootOwnerHwnd,
    int ProcessId,
    DateTimeOffset ProcessStartedUtc,
    long HostHwnd,
    RectI WindowBounds,
    RectI HostBounds);

/// <summary>Produced by an explicit user confirmation of this exact bounded operation.</summary>
public sealed record GridOperationApproval(
    string GridId,
    GridTargetIdentity Target,
    GridCaptureLimits Limits,
    bool AllowHorizontal,
    bool AllowVertical,
    DateTimeOffset ApprovedUtc);

public sealed record AcquisitionRequest(
    MappedGridDefinition Definition,
    GridAcquisitionMode Mode,
    GridCaptureLimits Limits,
    GridTargetIdentity Target,
    GridOperationApproval? Approval,
    string EvidenceDirectory,
    string AcquisitionId,
    bool LocalOcrQualified = false,
    GridCaptureScope Scope = GridCaptureScope.FullTable,
    bool OptimizeImageTraversal = false);

public sealed record AcquisitionProgress(
    string AcquisitionId,
    GridAcquisitionLifecycle Lifecycle,
    GridAcquisitionStage Stage,
    long ElapsedMs,
    int TileCount,
    int RowCount,
    int MovementCount,
    string Reason);

/// <summary>Bounds are original image pixels; identity denotes a positioned occurrence, never its text.</summary>
public sealed record GridCapturedRowRegion(
    string RowOccurrenceId,
    int RowIndex,
    RectI Bounds,
    bool IsComplete);

/// <summary>Offsets locate the body in logical pixels. Header/body/row bounds are image-local.</summary>
public sealed record GridCapturedTile(
    string TileId,
    string PngPath,
    string Sha256,
    DateTimeOffset CapturedUtc,
    RectI ScreenshotBounds,
    RectI HeaderBounds,
    RectI BodyBounds,
    int OffsetX,
    int OffsetY,
    bool Accepted,
    IReadOnlyList<GridCapturedRowRegion> Rows);

public sealed record GridJoinEvidence(
    string FromTileId,
    string ToTileId,
    GridScrollAxis Axis,
    bool Accepted,
    int DisplacementX,
    int DisplacementY,
    int CompatibleCandidates,
    int ComparedPixels,
    double MatchFraction,
    string Reason);

/// <summary>Native coordinates can corroborate boundaries; they are not pixel placement evidence.</summary>
public sealed record GridAxisPosition(
    bool Supported,
    int Minimum,
    int Maximum,
    uint PageSize,
    int Position,
    bool AtStart,
    bool AtEnd);

public sealed record GridScrollPosition(GridAxisPosition Horizontal, GridAxisPosition Vertical);

public sealed record GridMovementReceipt(
    int Sequence,
    GridScrollAxis Axis,
    int RequestedPosition,
    bool IsRestoration,
    DateTimeOffset StartedUtc,
    DateTimeOffset EndedUtc,
    GridScrollPosition Before,
    GridScrollPosition? After,
    string Outcome);

public sealed record GridCoverage(
    bool LeftBoundary,
    bool TopBoundary,
    bool RightBoundary,
    bool BottomBoundary,
    bool Continuous,
    bool HorizontalMovementObserved,
    bool VerticalMovementObserved,
    IReadOnlyList<RectI> AcceptedBodyRegions,
    IReadOnlyList<string> Gaps)
{
    public bool ColumnCoverageComplete => LeftBoundary && RightBoundary && Continuous && Gaps.Count == 0;
    public bool RowCoverageComplete => TopBoundary && BottomBoundary && Continuous && Gaps.Count == 0;
}

public sealed record GridRestorationResult(
    GridRestorationStatus Status,
    string Reason,
    bool PositionVerified,
    bool AnchorVerified,
    DateTimeOffset? StartedUtc = null,
    DateTimeOffset? EndedUtc = null);

/// <summary>Complete means the requested scope was captured, not that all rows or cells were extracted.</summary>
public sealed record CapturedGrid(
    string AcquisitionId,
    string GridId,
    GridSchema Schema,
    GridTargetIdentity Source,
    DateTimeOffset StartedUtc,
    DateTimeOffset EndedUtc,
    GridCaptureStatus Status,
    GridAcquisitionStage Stage,
    IReadOnlyList<string> Reasons,
    IReadOnlyList<GridCapturedTile> Tiles,
    IReadOnlyList<GridJoinEvidence> Joins,
    IReadOnlyList<GridMovementReceipt> Movements,
    GridCoverage Coverage,
    GridRestorationResult Restoration,
    string ManifestPath,
    GridCaptureLimits? Limits = null,
    GridAcquisitionMode Mode = GridAcquisitionMode.FreshRead,
    DateTimeOffset? ApprovedUtc = null,
    GridCaptureScope Scope = GridCaptureScope.FullTable);
