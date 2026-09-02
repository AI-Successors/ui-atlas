using UiAtlas.Core.Contracts;

namespace UiAtlas.Core.Storage;

public static class GraphExport
{
    public static UiKnowledgeGraph ApplyProfile(UiKnowledgeGraph graph, bool includeSensitiveEvidence)
    {
        if (includeSensitiveEvidence)
            return graph with { Metadata = graph.Metadata with { PrivacyProfile = FormatVersions.FullEvidenceProfile } };

        var exportSalt = Guid.NewGuid().ToString("N");
        var nodeIds = graph.Nodes.ToDictionary(
            node => node.Id,
            node => UiAtlas.Core.Build.StableIdentity.Create("node", exportSalt, node.Id),
            StringComparer.Ordinal);
        var edgeIds = graph.Edges.ToDictionary(
            edge => edge.Id,
            edge => UiAtlas.Core.Build.StableIdentity.Create("edge", exportSalt, edge.Id),
            StringComparer.Ordinal);
        var kindCounters = new Dictionary<GraphNodeKind, int>();
        var nodes = graph.Nodes.OrderBy(node => node.Kind).ThenBy(node => node.Id, StringComparer.Ordinal).Select(node =>
        {
            kindCounters.TryGetValue(node.Kind, out var ordinal);
            kindCounters[node.Kind] = ++ordinal;
            var id = nodeIds[node.Id];
            return node with
            {
                Id = id,
                ParentId = string.IsNullOrEmpty(node.ParentId) ? string.Empty : nodeIds[node.ParentId],
                StableKey = id,
                Label = $"{node.Kind} {ordinal}",
                Properties = [new GraphProperty("area", ExportArea(node))],
                Evidence = node.Evidence.Select(SanitizeEvidence).ToArray()
            };
        }).OrderBy(node => node.Id, StringComparer.Ordinal).ToArray();
        var edges = graph.Edges.Select(edge => edge with
        {
            Id = edgeIds[edge.Id],
            FromId = nodeIds[edge.FromId],
            ToId = nodeIds[edge.ToId],
            Properties = Array.Empty<GraphProperty>(),
            Evidence = edge.Evidence.Select(SanitizeEvidence).ToArray()
        }).OrderBy(edge => edge.Id, StringComparer.Ordinal).ToArray();
        var semanticHash = UiAtlas.Core.Build.GraphSemantics.ComputeHash(nodes, edges);
        return graph with
        {
            Metadata = graph.Metadata with
            {
                GraphId = UiAtlas.Core.Build.StableIdentity.Create("graph", exportSalt, semanticHash),
                BuiltUtc = DateTimeOffset.UnixEpoch,
                SemanticHash = semanticHash,
                PrivacyProfile = FormatVersions.SafeExportProfile,
                SourceBundleId = "[redacted]",
                SourceBundleIds = ["[redacted]"],
                LogicalMapId = "[redacted]"
            },
            Nodes = nodes,
            Edges = edges
        };
    }

    private static string ExportArea(GraphNode node)
    {
        if (node.Kind == GraphNodeKind.Application) return "app";
        return node.Properties.FirstOrDefault(property => property.Name == "layer")?.Value switch
        {
            "raw-data-streams" => "raw-data-streams",
            "raw-world" => "raw-world",
            "semantic-world" => "semantic-world",
            "prediction" => "prediction",
            _ => throw new InvalidDataException("Graph node has no supported export area.")
        };
    }

    private static EvidenceRef SanitizeEvidence(EvidenceRef value) => value with
    {
        BundleId = "[redacted]", FrameSequence = 0, ObservationEntry = "[redacted]", Bounds = null, ScreenshotEntry = null
    };
}
