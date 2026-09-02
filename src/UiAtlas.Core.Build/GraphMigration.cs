using UiAtlas.Core.Contracts;

namespace UiAtlas.Core.Build;

public static class GraphMigration
{
    public static UiKnowledgeGraph UpgradeToCurrent(UiKnowledgeGraph graph)
    {
        if (graph is null || graph.Metadata is null || graph.Nodes is null || graph.Edges is null)
            throw new InvalidDataException("Graph envelope is incomplete.");
        if (graph.Metadata.FormatVersion == FormatVersions.Graph) return graph;
        if (graph.Metadata.FormatVersion is not (FormatVersions.LegacyGraph or FormatVersions.IntermediateGraph or FormatVersions.PreviousGraph))
            throw new InvalidDataException("Unsupported graph version.");
        if (graph.Nodes.Any(node => node is null || node.Properties is null || node.Properties.Any(property => property is null)) ||
            graph.Edges.Any(edge => edge is null))
            throw new InvalidDataException("Legacy graph contains malformed nested members.");

        var nodes = graph.Metadata.FormatVersion == FormatVersions.LegacyGraph
            ? graph.Nodes.Select(node => node.Properties.Any(property => property.Name == "layer")
                ? node
                : node with { Properties = [.. node.Properties, new GraphProperty("layer", "legacy-observed")] }).ToArray()
            : graph.Nodes.ToArray();
        var edges = graph.Edges.ToArray();
        return graph with
        {
            Metadata = graph.Metadata with
            {
                FormatVersion = FormatVersions.Graph,
                SourceBundleIds = graph.Metadata.EffectiveSourceBundleIds.ToArray(),
                LogicalMapId = graph.Metadata.EffectiveLogicalMapId,
                SemanticHash = GraphSemantics.ComputeHash(nodes, edges)
            },
            Nodes = nodes,
            Edges = edges
        };
    }
}
