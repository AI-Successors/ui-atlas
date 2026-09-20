namespace UiAtlas.Core.Contracts;

[Flags]
public enum GridScrollAxes
{
    None = 0,
    Horizontal = 1,
    Vertical = 2
}

/// <summary>ReadingVerified describes a proven reading mechanism, not complete capture.</summary>
public enum GridQualification
{
    Candidate,
    Mapped,
    ReadingVerified
}

/// <summary>A parent-scoped host selector. No process instance or native handle is durable identity.</summary>
public sealed record GridLocatorSegment(
    string ClassName,
    string AutomationId = "",
    string ControlType = "",
    string Name = "",
    int SiblingOrdinal = 0);

public sealed record ParentScopedGridLocator(
    string ProcessName,
    string WindowClassName,
    string WindowTitle,
    IReadOnlyList<GridLocatorSegment> Ancestors,
    GridLocatorSegment Host);

/// <summary>Physical pixel hints relative to the live host at ReferenceHostWidth/Height.
/// The acquisition service must revalidate them before reading or input.</summary>
public sealed record GridRegions(
    int ReferenceHostWidth,
    int ReferenceHostHeight,
    RectI Data,
    RectI Header,
    RectI Body,
    RectI ScrollScope);

/// <summary>StartX/EndX are physical pixel intervals in unscrolled table-content coordinates.
/// ColumnKey is identity; Label is display text and may be duplicated or empty.</summary>
public sealed record GridColumn(
    string ColumnKey,
    int Ordinal,
    string Label,
    double StartX,
    double EndX);

public sealed record GridSchema(
    string Revision,
    bool IsComplete,
    int? KnownColumnCount,
    IReadOnlyList<GridColumn> Columns,
    double RowHeight,
    double HeaderHeight,
    string Layout = "rectangular-single-header-uniform-rows",
    string? RecordIdColumnKey = null,
    bool UniformBlankCellsQualified = false);

/// <summary>The last mapping attempt is diagnostic history, never authority for a saved schema.</summary>
public sealed record GridMappingAttempt(
    DateTimeOffset AttemptedUtc,
    string Stage,
    string Status,
    string Reason);

/// <summary>One table with ordered physical columns. Contains structure only, never current rows.</summary>
public sealed record MappedGridDefinition(
    string GridId,
    string LogicalMapId,
    string SurfaceStableKey,
    string HostStableKey,
    string DisplayName,
    ParentScopedGridLocator Locator,
    GridRegions Regions,
    GridScrollAxes SupportedAxes,
    GridSchema Schema,
    GridQualification Qualification,
    string QualificationReason,
    DateTimeOffset UpdatedUtc,
    GridMappingAttempt? LastAttempt = null);
