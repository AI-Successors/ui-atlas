using UiAtlas.Core.Contracts;
using UiAtlas.Core.Storage;

namespace UiAtlas.Core.Reader;

public sealed class UiGraphReader
{
    public UiKnowledgeGraph Load(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".json" => GraphJsonStore.Load(path),
        ".db" or ".sqlite" => SqliteGraphStore.Load(path),
        _ => throw new NotSupportedException("Expected .json, .db, or .sqlite graph file.")
    };

    public IReadOnlyList<GraphNode> Search(UiKnowledgeGraph graph, string query) => graph.Nodes
        .Where(x => x.Label.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    x.Properties.Any(p => p.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || p.Value.Contains(query, StringComparison.OrdinalIgnoreCase)))
        .OrderBy(x => x.Kind).ThenBy(x => x.Label, StringComparer.OrdinalIgnoreCase).ToArray();

    public IReadOnlyList<GraphNode> Children(UiKnowledgeGraph graph, string parentId) => graph.Nodes
        .Where(x => x.ParentId == parentId).OrderBy(x => x.Kind).ThenBy(x => x.Label, StringComparer.OrdinalIgnoreCase).ToArray();
}

public sealed record GraphDiff(
    IReadOnlyList<string> AddedNodes,
    IReadOnlyList<string> RemovedNodes,
    IReadOnlyList<string> AddedEdges,
    IReadOnlyList<string> RemovedEdges);

public static class UiGraphDiff
{
    public static GraphDiff Compare(UiKnowledgeGraph left, UiKnowledgeGraph right)
    {
        var leftNodes = left.Nodes.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        var rightNodes = right.Nodes.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        var leftEdges = left.Edges.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        var rightEdges = right.Edges.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        return new(rightNodes.Except(leftNodes).Order().ToArray(), leftNodes.Except(rightNodes).Order().ToArray(),
            rightEdges.Except(leftEdges).Order().ToArray(), leftEdges.Except(rightEdges).Order().ToArray());
    }
}
