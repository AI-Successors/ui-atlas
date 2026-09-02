using UiAtlas.Core.Build;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Storage;

namespace UiAtlas.Core.Tests;

public sealed class GraphControlConfirmationTests
{
    [Fact]
    public void ConfirmButtonCandidatePersistsConfirmationAcrossSemanticAndRawNodes()
    {
        var raw = Node("raw-button", "raw-surface", "raw-world",
            new("controlType", "Button"),
            new("verificationStatus", "Unverified"),
            new("offscreen", "True"),
            new("effectivelyVisible", "False"));
        var semantic = Node("semantic-button", "semantic-surface", "semantic-world",
            new("controlType", "Button"),
            new("semanticControlKind", "Button"),
            new("sourceRawControlId", raw.Id),
            new("verificationStatus", "Unverified"),
            new("offscreen", "True"),
            new("effectivelyVisible", "False"));
        var graph = Graph(raw, semantic);
        var timestamp = new DateTimeOffset(2026, 8, 28, 1, 2, 3, TimeSpan.Zero);

        var confirmed = GraphControlConfirmation.ConfirmButtonCandidate(graph, semantic.Id, timestamp);

        Assert.All(confirmed.Nodes, node =>
        {
            Assert.Contains(node.Properties, property => property is { Name: "verificationStatus", Value: "Confirmed" });
            Assert.Contains(node.Properties, property => property is { Name: "enabled", Value: "True" });
            Assert.Contains(node.Properties, property => property is { Name: "effectivelyVisible", Value: "True" });
            Assert.Contains(node.Properties, property => property is { Name: "offscreen", Value: "False" });
            Assert.Contains(node.Properties, property => property is { Name: "supportedPattern", Value: "Invoke" });
            Assert.Contains(node.Properties, property => property is { Name: "confirmationSource", Value: "User" });
        });
        Assert.Equal(GraphSemantics.ComputeHash(confirmed.Nodes, confirmed.Edges), confirmed.Metadata.SemanticHash);
    }

    [Fact]
    public void NonButtonOrAlreadyObservedControlCannotBeConfirmedAsButton()
    {
        var edit = Node("edit", "surface", "semantic-world",
            new("controlType", "Edit"), new("verificationStatus", "Unverified"));
        var observedButton = Node("button", "surface", "semantic-world",
            new("controlType", "Button"), new("verificationStatus", "Observed"));
        var graph = Graph(edit, observedButton);

        Assert.False(GraphControlConfirmation.IsConfirmableButtonCandidate(edit));
        Assert.False(GraphControlConfirmation.IsConfirmableButtonCandidate(observedButton));
        Assert.Throws<InvalidOperationException>(() =>
            GraphControlConfirmation.ConfirmButtonCandidate(graph, edit.Id, DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void RemoveButtonCandidateDeletesSemanticRawAndRawStreamRepresentations()
    {
        var raw = Node("raw-button", "raw-surface", "raw-world",
            new("controlType", "Button"), new("verificationStatus", "Unverified"));
        var semantic = Node("semantic-button", "semantic-surface", "semantic-world",
            new("controlType", "Button"), new("sourceRawControlId", raw.Id),
            new("verificationStatus", "Unverified"));
        var stream = Node("stream-button", "stream-surface", "raw-data-streams",
            new("controlType", "Button"), new("stableControlKey", raw.Id),
            new("verificationStatus", "Unverified"));
        var retained = Node("retained", "raw-surface", "raw-world",
            new("controlType", "Edit"), new("verificationStatus", "Observed"));
        var graph = Graph([raw, semantic, stream, retained],
        [
            Edge("raw-surface", raw.Id),
            Edge("semantic-surface", semantic.Id),
            Edge("stream-surface", stream.Id)
        ]);

        var updated = GraphControlConfirmation.RemoveButtonCandidate(graph, semantic.Id);

        Assert.DoesNotContain(updated.Nodes, node => node.Id is "raw-button" or "semantic-button" or "stream-button");
        Assert.Contains(updated.Nodes, node => node.Id == retained.Id);
        Assert.DoesNotContain(updated.Edges, edge =>
            edge.ToId is "raw-button" or "semantic-button" or "stream-button");
        Assert.Equal(GraphSemantics.ComputeHash(updated.Nodes, updated.Edges), updated.Metadata.SemanticHash);
    }

    [Fact]
    public void RemovingCandidateReparentsRetainedChildren()
    {
        var surface = new GraphNode("raw-surface", GraphNodeKind.Surface, "app", "raw-surface", "Surface",
            [new("layer", "raw-world")], []);
        var parent = Node("raw-button", "raw-surface", "raw-world",
            new("controlType", "Button"), new("verificationStatus", "Unverified"));
        var child = Node("raw-child", parent.Id, "raw-world",
            new("controlType", "Text"), new("verificationStatus", "Observed"));
        var graph = Graph([surface, parent, child], [Edge("raw-surface", parent.Id), Edge(parent.Id, child.Id)]);

        var updated = GraphControlConfirmation.RemoveButtonCandidate(graph, parent.Id);

        var retainedChild = Assert.Single(updated.Nodes, node => node.Id == child.Id);
        Assert.Equal("raw-surface", retainedChild.ParentId);
        Assert.Contains(updated.Edges, edge =>
            edge.Kind == "contains" && edge.FromId == "raw-surface" && edge.ToId == child.Id);
    }

    [Fact]
    public void ObservedOrInteractionSourceButtonCannotBeRemovedAsCandidate()
    {
        var observed = Node("observed", "surface", "raw-world",
            new("controlType", "Button"), new("verificationStatus", "Observed"));
        var candidate = Node("candidate", "surface", "raw-world",
            new("controlType", "Button"), new("verificationStatus", "Unverified"));
        var interaction = new GraphEdge("interaction", "interaction", "state-a", "state-b",
            [new("sourceControlId", candidate.Id)], []);

        Assert.False(GraphControlConfirmation.IsRemovableButtonCandidate(observed));
        Assert.Throws<InvalidOperationException>(() =>
            GraphControlConfirmation.RemoveButtonCandidate(Graph(observed), observed.Id));
        Assert.Throws<InvalidOperationException>(() =>
            GraphControlConfirmation.RemoveButtonCandidate(Graph([candidate], [interaction]), candidate.Id));
    }

    [Fact]
    public void RemovingVisualCandidateKeepsCompleteRecordedGraphValid()
    {
        using var temp = new TempDirectory();
        var graph = new RecordingGraphBuilder().Build(
            SyntheticBundleFactory.Create(temp.Path, visualFallback: true));
        var target = Assert.Single(graph.Nodes, node =>
            node.Kind == GraphNodeKind.Control &&
            node.Properties.Any(property => property is { Name: "layer", Value: "semantic-world" }) &&
            node.Properties.Any(property => property is
                { Name: "className", Value: "UiAtlas.VisualControlRegion" }));
        var rawId = Assert.Single(target.Properties, property => property.Name == "sourceRawControlId").Value;
        var nodes = graph.Nodes.Select(node => node.Id is var id && (id == target.Id || id == rawId)
            ? WithVerificationStatus(node, "Unverified")
            : node).ToArray();
        graph = graph with
        {
            Nodes = nodes,
            Metadata = graph.Metadata with { SemanticHash = GraphSemantics.ComputeHash(nodes, graph.Edges) }
        };

        var updated = GraphControlConfirmation.RemoveButtonCandidate(graph, target.Id);

        Assert.DoesNotContain(updated.Nodes, node =>
            node.Id is var id && (id == target.Id || id == rawId) ||
            node.Properties.Any(property => property.Name == "stableControlKey" && property.Value == rawId));
        Assert.True(GraphValidator.Validate(updated).IsValid,
            string.Join(Environment.NewLine, GraphValidator.Validate(updated).Issues.Select(issue =>
                $"{issue.Code}: {issue.Message}")));

        var databasePath = Path.Combine(temp.Path, "curated.db");
        SqliteGraphStore.Save(updated, databasePath);
        Assert.DoesNotContain(SqliteGraphStore.Load(databasePath).Nodes, node => node.Id == target.Id);

        var jsonPath = Path.Combine(temp.Path, "curated.json");
        GraphJsonStore.Save(updated, jsonPath);
        Assert.DoesNotContain(GraphJsonStore.Load(jsonPath).Nodes, node => node.Id == target.Id);
    }

    private static GraphNode Node(string id, string parentId, string layer, params GraphProperty[] properties) =>
        new(id, GraphNodeKind.Control, parentId, id, id,
            [new("layer", layer), .. properties],
            [new("session", 1, "raw/observations/frame-000001.json", new RectI(10, 20, 80, 30))]);

    private static UiKnowledgeGraph Graph(params GraphNode[] nodes)
        => Graph(nodes, []);

    private static UiKnowledgeGraph Graph(IReadOnlyList<GraphNode> nodes, IReadOnlyList<GraphEdge> edges)
    {
        var metadata = new GraphMetadata(
            "ui-atlas.graph/1", "test", "graph", DateTimeOffset.UnixEpoch,
            "session", new string('0', 64), "full-evidence/1");
        return new(metadata, nodes, edges);
    }

    private static GraphEdge Edge(string fromId, string toId) =>
        new($"edge-{fromId}-{toId}", "contains", fromId, toId, [], []);

    private static GraphNode WithVerificationStatus(GraphNode node, string status) => node with
    {
        Properties =
        [
            .. node.Properties.Where(property => property.Name != "verificationStatus"),
            new("verificationStatus", status)
        ]
    };
}
