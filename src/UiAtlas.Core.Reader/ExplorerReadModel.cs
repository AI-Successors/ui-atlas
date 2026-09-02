using UiAtlas.Core.Contracts;

namespace UiAtlas.Core.Reader;

public sealed record ExplorerItem(string Id, GraphNodeKind Kind, string Layer, string Label, string ParentId, int EvidenceCount)
{
    public string DisplayLabel => $"[{Layer}] {Label}";
}

public sealed class ExplorerReadModel
{
    public const int MaxExplorerNodes = 5_000;
    public const int MaxExplorerEdges = 10_000;

    public ExplorerReadModel(UiKnowledgeGraph graph)
    {
        if (graph.Nodes.Count > MaxExplorerNodes || graph.Edges.Count > MaxExplorerEdges)
            throw new InvalidDataException("Graph exceeds the explorer materialization limit; use the reader API or CLI for this graph.");
        Graph = graph;
        Items = graph.Nodes.Select(x => new ExplorerItem(x.Id, x.Kind,
            x.Properties.FirstOrDefault(property => property.Name == "layer")?.Value ?? "unlayered",
            x.Label, x.ParentId, x.Evidence.Count)).ToArray();
    }

    public UiKnowledgeGraph Graph { get; }
    public IReadOnlyList<ExplorerItem> Items { get; }

    public IReadOnlyList<ExplorerItem> Filter(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return Items;
        var direct = Items.Where(x => x.Label.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                                      x.Layer.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                                      x.Kind.ToString().Contains(query, StringComparison.OrdinalIgnoreCase)).Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        var byId = Items.ToDictionary(x => x.Id, StringComparer.Ordinal);
        foreach (var id in direct.ToArray())
        {
            var current = byId[id];
            var depth = 0;
            while (!string.IsNullOrEmpty(current.ParentId) && byId.TryGetValue(current.ParentId, out current) && depth++ < 64)
                if (!direct.Add(current.Id)) break;
        }
        return Items.Where(x => direct.Contains(x.Id)).ToArray();
    }
}
