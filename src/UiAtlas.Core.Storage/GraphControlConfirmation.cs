using UiAtlas.Core.Build;
using UiAtlas.Core.Contracts;

namespace UiAtlas.Core.Storage;

public static class GraphControlConfirmation
{
    public static bool IsUnverified(GraphNode node) =>
        node.Kind == GraphNodeKind.Control &&
        node.Properties.Any(property =>
            property.Name.Equals("verificationStatus", StringComparison.Ordinal) &&
            property.Value.Equals("Unverified", StringComparison.OrdinalIgnoreCase));

    public static bool IsConfirmableButtonCandidate(GraphNode node)
    {
        if (!IsUnverified(node)) return false;
        return node.Properties.Any(property =>
            property.Name is "controlType" or "semanticControlKind" &&
            property.Value.Contains("Button", StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsRemovableButtonCandidate(GraphNode node) =>
        IsConfirmableButtonCandidate(node);

    public static UiKnowledgeGraph ConfirmButtonCandidate(
        UiKnowledgeGraph graph,
        string controlId,
        DateTimeOffset confirmedUtc)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentException.ThrowIfNullOrWhiteSpace(controlId);
        var target = graph.Nodes.FirstOrDefault(node => node.Id == controlId)
            ?? throw new ArgumentException("Control does not exist in this map.", nameof(controlId));
        if (!IsConfirmableButtonCandidate(target))
            throw new InvalidOperationException("Only an unverified button candidate can be confirmed.");

        var relatedIds = new HashSet<string>(StringComparer.Ordinal) { target.Id };
        var rawSourceId = Property(target, "sourceRawControlId");
        if (!string.IsNullOrWhiteSpace(rawSourceId)) relatedIds.Add(rawSourceId);
        foreach (var node in graph.Nodes.Where(node =>
                     node.Kind == GraphNodeKind.Control &&
                     string.Equals(Property(node, "sourceRawControlId"), target.Id, StringComparison.Ordinal)))
            relatedIds.Add(node.Id);

        var nodes = graph.Nodes
            .Select(node => relatedIds.Contains(node.Id) ? Confirm(node, confirmedUtc) : node)
            .ToArray();
        var semanticHash = GraphSemantics.ComputeHash(nodes, graph.Edges);
        return graph with
        {
            Metadata = graph.Metadata with { SemanticHash = semanticHash },
            Nodes = nodes
        };
    }

    public static UiKnowledgeGraph RemoveButtonCandidate(
        UiKnowledgeGraph graph,
        string controlId)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentException.ThrowIfNullOrWhiteSpace(controlId);
        var target = graph.Nodes.FirstOrDefault(node => node.Id == controlId)
            ?? throw new ArgumentException("Control does not exist in this map.", nameof(controlId));
        if (!IsRemovableButtonCandidate(target))
            throw new InvalidOperationException("Only an unverified button candidate can be removed.");

        var relatedIds = RelatedControlIds(graph, target);
        if (graph.Nodes.Any(node => relatedIds.Contains(node.Id) &&
                node.Properties.Any(property =>
                    property.Name == "verificationStatus" &&
                    property.Value.Equals("Confirmed", StringComparison.OrdinalIgnoreCase))))
            throw new InvalidOperationException("A confirmed control cannot be removed as a false candidate.");
        if (HasRecordedUse(graph, relatedIds))
            throw new InvalidOperationException(
                "This candidate is used by a recorded interaction or popup and cannot be removed here.");

        var originalById = graph.Nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        var reparentedChildren = new Dictionary<string, string>(StringComparer.Ordinal);
        var nodes = graph.Nodes
            .Where(node => !relatedIds.Contains(node.Id))
            .Select(node =>
            {
                if (!relatedIds.Contains(node.ParentId)) return node;
                var parentId = NearestRetainedParent(node.ParentId, originalById, relatedIds);
                reparentedChildren[node.Id] = parentId;
                return node with { ParentId = parentId };
            })
            .ToArray();

        var edges = graph.Edges
            .Where(edge => !relatedIds.Contains(edge.FromId) && !relatedIds.Contains(edge.ToId))
            .ToList();
        foreach (var child in nodes.Where(node => reparentedChildren.ContainsKey(node.Id)))
        {
            var parentId = reparentedChildren[child.Id];
            if (edges.Any(edge => edge.Kind == "contains" && edge.FromId == parentId && edge.ToId == child.Id))
                continue;
            var priorContainment = graph.Edges.FirstOrDefault(edge =>
                edge.Kind == "contains" && edge.ToId == child.Id && relatedIds.Contains(edge.FromId));
            edges.Add(new(
                StableIdentity.Create("edge", parentId, child.Id, "contains"),
                "contains",
                parentId,
                child.Id,
                [],
                priorContainment?.Evidence ?? child.Evidence.Take(1).ToArray()));
        }

        var finalEdges = edges.ToArray();
        var semanticHash = GraphSemantics.ComputeHash(nodes, finalEdges);
        return graph with
        {
            Metadata = graph.Metadata with { SemanticHash = semanticHash },
            Nodes = nodes,
            Edges = finalEdges
        };
    }

    private static GraphNode Confirm(GraphNode node, DateTimeOffset confirmedUtc)
    {
        var properties = node.Properties
            .Where(property => property.Name is not
                ("verificationStatus" or "enabled" or "offscreen" or "effectivelyVisible" or
                 "confirmationSource" or "confirmedUtc"))
            .ToList();
        properties.Add(new("verificationStatus", "Confirmed"));
        properties.Add(new("enabled", bool.TrueString));
        properties.Add(new("offscreen", bool.FalseString));
        properties.Add(new("effectivelyVisible", bool.TrueString));
        properties.Add(new("confirmationSource", "User"));
        properties.Add(new("confirmedUtc", confirmedUtc.ToUniversalTime().ToString("O")));
        if (IsButton(node) && !properties.Any(property =>
                property.Name == "supportedPattern" &&
                property.Value.Equals("Invoke", StringComparison.OrdinalIgnoreCase)))
            properties.Add(new("supportedPattern", "Invoke"));
        return node with { Properties = properties };
    }

    private static bool IsButton(GraphNode node) => node.Properties.Any(property =>
        property.Name is "controlType" or "semanticControlKind" &&
        property.Value.Contains("Button", StringComparison.OrdinalIgnoreCase));

    private static HashSet<string> RelatedControlIds(UiKnowledgeGraph graph, GraphNode target)
    {
        var result = new HashSet<string>(StringComparer.Ordinal) { target.Id };
        var rawIds = new HashSet<string>(StringComparer.Ordinal);
        var layer = Property(target, "layer");
        if (layer == "raw-world") rawIds.Add(target.Id);
        foreach (var rawId in target.Properties
                     .Where(property => property.Name == "sourceRawControlId")
                     .Select(property => property.Value)
                     .Where(value => !string.IsNullOrWhiteSpace(value)))
            rawIds.Add(rawId);
        var stableControlKey = Property(target, "stableControlKey");
        if (!string.IsNullOrWhiteSpace(stableControlKey)) rawIds.Add(stableControlKey);

        foreach (var node in graph.Nodes.Where(node => node.Kind == GraphNodeKind.Control))
        {
            if (rawIds.Contains(node.Id) ||
                node.Properties.Any(property =>
                    property.Name == "sourceRawControlId" && rawIds.Contains(property.Value)) ||
                node.Properties.Any(property =>
                    property.Name == "stableControlKey" && rawIds.Contains(property.Value)))
                result.Add(node.Id);
        }
        return result;
    }

    private static bool HasRecordedUse(UiKnowledgeGraph graph, IReadOnlySet<string> relatedIds)
    {
        if (graph.Edges.Any(edge =>
                edge.Kind != "contains" &&
                (relatedIds.Contains(edge.FromId) ||
                 relatedIds.Contains(edge.ToId) ||
                 edge.Properties.Any(property =>
                     property.Name == "sourceControlId" && relatedIds.Contains(property.Value)))))
            return true;
        return graph.Nodes.Any(node =>
            !relatedIds.Contains(node.Id) &&
            node.Properties.Any(property =>
                property.Name is "interactionSourceControlId" or "interactionSourceRawControlId" &&
                relatedIds.Contains(property.Value)));
    }

    private static string NearestRetainedParent(
        string parentId,
        IReadOnlyDictionary<string, GraphNode> nodesById,
        IReadOnlySet<string> removedIds)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (removedIds.Contains(parentId))
        {
            if (!visited.Add(parentId) || !nodesById.TryGetValue(parentId, out var parent))
                throw new InvalidOperationException("The candidate hierarchy cannot be safely repaired.");
            parentId = parent.ParentId;
        }
        if (!nodesById.ContainsKey(parentId))
            throw new InvalidOperationException("The candidate has no retained parent in the map.");
        return parentId;
    }

    private static string? Property(GraphNode node, string name) =>
        node.Properties.FirstOrDefault(property => property.Name == name)?.Value;
}
