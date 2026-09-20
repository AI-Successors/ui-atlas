using System.Text.Json.Serialization;

namespace UiAtlas.Core.Contracts;

[JsonConverter(typeof(JsonStringEnumConverter<GridCellReadStatus>))]
public enum GridCellReadStatus { Text, Empty, Unreadable }

[JsonConverter(typeof(JsonStringEnumConverter<GridDataStatus>))]
public enum GridDataStatus { Complete, Partial, Failed }

[JsonConverter(typeof(JsonStringEnumConverter<GridExtractionStatus>))]
public enum GridExtractionStatus { Complete, Partial, Failed }

public sealed record GridReadResult(
    string AcquisitionId,
    string GridId,
    GridTargetIdentity? Source,
    GridSchema Schema,
    IReadOnlyList<GridRowData> Rows,
    GridDataStatus Status,
    IReadOnlyList<string> Reasons,
    GridCaptureStatus CaptureStatus,
    GridExtractionStatus ExtractionStatus,
    GridRestorationResult Restoration,
    GridAcquisitionStage Stage,
    DateTimeOffset? CaptureStartedUtc,
    DateTimeOffset? CaptureEndedUtc,
    DateTimeOffset ExtractionStartedUtc,
    DateTimeOffset ExtractionEndedUtc,
    string CaptureManifestPath,
    string ReadingMechanism = "windows-ocr",
    GridCoverage? Coverage = null,
    int CaptureTileCount = 0,
    int MovementCount = 0);

/// <summary>Original tile pixels; bounds are never mosaic or screen coordinates.</summary>
public sealed record GridCellSource(string TileId, RectI PixelBounds, string Sha256);

public sealed record GridCellValue(
    string ColumnKey,
    GridCellReadStatus Status,
    string? Text,
    string? Reason,
    IReadOnlyList<GridCellSource> Sources);

/// <summary>Row identity comes from accepted placement, never from cell values.</summary>
public sealed record GridRowData(
    string AcquisitionRowId,
    int RowIndex,
    string? RecordId,
    IReadOnlyList<GridCellValue> Cells);

public sealed record GridExtractionOptions(
    int MaxRows = 200,
    int MaxColumns = 32,
    int MaxDurationMs = 30_000,
    int RowBatchSize = 8,
    string? RecordIdColumnKey = null,
    bool UniformBlankCellsQualified = false);

/// <summary>Counts are null when the provider is unsupported or could not be read.</summary>
public sealed record GridNativeCapabilities(
    bool ContainerGridSupported,
    bool ContainerTableSupported,
    int? RowCount,
    int? ColumnCount,
    int InspectedDescendants,
    bool GridItemSupported,
    bool TableItemSupported,
    bool ValueSupported,
    bool LegacySupported,
    bool TextSupported,
    bool ProbeCompleted,
    string? Reason);

public sealed record GridViewportColumn(
    string ColumnKey,
    int PhysicalOrdinal,
    RectI HeaderBounds,
    RectI BodyBounds,
    bool IsPartial,
    string? Label,
    GridCellReadStatus LabelStatus);

public sealed record GridViewportRow(int LocalOrdinal, RectI Bounds, bool IsPartial);

public sealed record GridNativeProbeResult(
    GridNativeCapabilities Capabilities,
    RectI? ContainerBounds,
    IReadOnlyList<RectI> ColumnHeaderBounds,
    IReadOnlyList<GridNativeTextEvidence>? TextEvidence = null);

/// <summary>Bounded diagnostic native text; does not imply a row/cell association or empty table.</summary>
public sealed record GridNativeTextEvidence(
    string SourceId,
    RectI Bounds,
    int ControlTypeId,
    string Name,
    string? Value,
    int? LegacyRole,
    bool IsContainer);

/// <summary>Immutable current capture, with image-local regions and accepted content offsets.</summary>
public sealed record GridViewportRequest(
    byte[] Png,
    RectI ScreenshotBounds,
    RectI HeaderBounds,
    RectI BodyBounds,
    GridSchema Schema,
    GridNativeProbeResult? NativeProbe = null,
    int OffsetX = 0,
    int OffsetY = 0,
    bool LocalOcrQualified = false,
    bool VerifyHeaderLabels = true);

public sealed record GridViewportDescription(
    GridSchema SchemaFragment,
    IReadOnlyList<GridViewportColumn> Columns,
    IReadOnlyList<GridViewportRow> Rows,
    double RowPitch,
    bool StructureVerified,
    bool ReadingQualified,
    string ReadingMechanism,
    GridNativeProbeResult? NativeProbe,
    IReadOnlyList<string> Reasons);
