namespace UiAtlas.Core.Contracts;

/// <summary>A physical column and its original header reading, before human correction.</summary>
public sealed record GridSchemaHeaderReading(
    string ColumnKey, RectI Bounds, GridCellReadStatus Status, string? Text, string? Reason);

/// <summary>A schema reviewed against captured pixels. Does not qualify business-cell reading
/// or change the source capture's coverage/restoration result.</summary>
public sealed record GridSchemaReview(
    string AcquisitionId,
    string GridId,
    GridSchema Schema,
    RectI HeaderBounds,
    string ImagePath,
    string ImageSha256,
    string CaptureManifestPath,
    GridCaptureStatus CaptureStatus,
    GridCoverage Coverage,
    GridRestorationResult Restoration,
    IReadOnlyList<GridSchemaHeaderReading> HeaderReadings,
    IReadOnlyList<string> Reasons,
    DateTimeOffset? ReviewedUtc = null,
    string HeaderReader = "windows-ocr",
    GridCaptureScope CaptureScope = GridCaptureScope.FullTable);

/// <summary>The screen and host on which the user reviewed this grid; not live reading qualification.</summary>
public sealed record DataGridReviewContext(
    ParentScopedGridLocator WindowLocator, string SurfaceLayerKey, GridLocatorSegment Control, RectI RelativeBounds,
    bool? IsUiaNative = null);

public sealed record SavedDataGridReview(
    string GridId, string DisplayName, DataGridReviewContext Context, GridSchemaReview Review, DateTimeOffset SavedUtc);
