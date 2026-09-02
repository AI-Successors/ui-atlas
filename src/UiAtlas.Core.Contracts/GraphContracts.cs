using System.Text.Json.Serialization;

namespace UiAtlas.Core.Contracts;

[JsonConverter(typeof(JsonStringEnumConverter<GraphNodeKind>))]
public enum GraphNodeKind { Application, Window, Surface, State, Control }

public sealed record GraphProperty(string Name, string Value, bool Sensitive = false);

public sealed record EvidenceRef(
    string BundleId,
    long FrameSequence,
    string ObservationEntry,
    RectI? Bounds,
    string? ScreenshotEntry = null);

public sealed record GraphNode(
    string Id,
    GraphNodeKind Kind,
    string ParentId,
    string StableKey,
    string Label,
    IReadOnlyList<GraphProperty> Properties,
    IReadOnlyList<EvidenceRef> Evidence);

public sealed record GraphEdge(
    string Id,
    string Kind,
    string FromId,
    string ToId,
    IReadOnlyList<GraphProperty> Properties,
    IReadOnlyList<EvidenceRef> Evidence);

public sealed record GraphMetadata(
    string FormatVersion,
    string ToolVersion,
    string GraphId,
    DateTimeOffset BuiltUtc,
    string SourceBundleId,
    string SemanticHash,
    string PrivacyProfile,
    IReadOnlyList<string>? SourceBundleIds = null,
    string? LogicalMapId = null)
{
    public IReadOnlyList<string> EffectiveSourceBundleIds =>
        SourceBundleIds is { Count: > 0 } ? SourceBundleIds : [SourceBundleId];

    public string EffectiveLogicalMapId =>
        string.IsNullOrWhiteSpace(LogicalMapId) ? SourceBundleId : LogicalMapId;
}

public sealed record UiKnowledgeGraph(
    GraphMetadata Metadata,
    IReadOnlyList<GraphNode> Nodes,
    IReadOnlyList<GraphEdge> Edges);

public sealed record ValidationIssue(string Code, string Severity, string Path, string Message);

public sealed record ValidationReport(bool IsValid, IReadOnlyList<ValidationIssue> Issues)
{
    public static ValidationReport Valid { get; } = new(true, []);
}
