using UiAtlas.Core.Build;
using UiAtlas.Core.Contracts;

namespace UiAtlas.Core.Tests;

public sealed class RawSemanticWorldTests
{
    [Fact]
    public void BuilderMaterializesRawAndSemanticWorldsWithLineage()
    {
        using var temp = new TempDirectory();
        var graph = new RecordingGraphBuilder().Build(SyntheticBundleFactory.Create(temp.Path));

        Assert.Equal(FormatVersions.Graph, graph.Metadata.FormatVersion);
        var rawSurfaces = Nodes(graph, GraphNodeKind.Surface, "raw-world");
        var semanticSurfaces = Nodes(graph, GraphNodeKind.Surface, "semantic-world");
        var rawControls = Nodes(graph, GraphNodeKind.Control, "raw-world");
        var semanticControls = Nodes(graph, GraphNodeKind.Control, "semantic-world");

        Assert.Contains(rawSurfaces, node => Property(node, "surfaceClass") == "RawWindow");
        var rawPopup = Assert.Single(rawSurfaces, node => Property(node, "surfaceClass") == "RawPopupWindow");
        Assert.NotNull(Property(rawPopup, "ownerRawSurfaceId"));
        Assert.Contains(semanticSurfaces, node => Property(node, "semanticSurfaceKind") == "PopupFamily");
        Assert.Contains(semanticSurfaces, node => Property(node, "semanticSurfaceKind") == "PopupVariant");
        Assert.Equal(rawControls.Count, semanticControls.Count);
        Assert.All(semanticControls, semantic =>
        {
            var rawId = Property(semantic, "sourceRawControlId");
            Assert.Contains(rawControls, raw => raw.Id == rawId);
        });
        Assert.Contains(rawControls, control => rawControls.Any(parent => parent.Id == control.ParentId && parent.Kind == GraphNodeKind.Control));
        Assert.True(graph.Nodes.Count(node => node.Kind == GraphNodeKind.State && Property(node, "layer") == "raw-world") >= 3);
        Assert.Contains(graph.Edges, edge => edge.Kind == "observed-transition");
    }

    [Fact]
    public void BorderlessToolHostedTransientIsCanonicalPopupNotToolWindow()
    {
        using var temp = new TempDirectory();
        var graph = new RecordingGraphBuilder().Build(SyntheticBundleFactory.Create(temp.Path, wordLikeBorderlessPopup: true));

        var popup = Assert.Single(Nodes(graph, GraphNodeKind.Surface, "raw-world"),
            node => Property(node, "surfaceClass") == "RawPopupWindow");
        Assert.DoesNotContain(Nodes(graph, GraphNodeKind.Surface, "raw-world"),
            node => Property(node, "surfaceClass") == "RawToolWindow");
        var semantic = Assert.Single(Nodes(graph, GraphNodeKind.Surface, "semantic-world"),
            node => Property(node, "sourceRawSurfaceId") == popup.Id && Property(node, "semanticSurfaceKind") == "PopupVariant");
        Assert.Equal("SemanticPopupWindow", Property(semantic, "semanticClass"));
    }

    [Fact]
    public void RootReportedPopupSubtreeMovesToEffectiveRawAndSemanticOwner()
    {
        using var temp = new TempDirectory();
        var graph = new RecordingGraphBuilder().Build(SyntheticBundleFactory.Create(temp.Path,
            wordLikeBorderlessPopup: true, rootReportedPopupSubtree: true));
        var rawPopup = Assert.Single(Nodes(graph, GraphNodeKind.Surface, "raw-world"),
            node => Property(node, "surfaceClass") == "RawPopupWindow");
        var rawChoice = Assert.Single(Nodes(graph, GraphNodeKind.Control, "raw-world"),
            node => Property(node, "automationId") == "choice");
        var semanticChoice = Assert.Single(Nodes(graph, GraphNodeKind.Control, "semantic-world"),
            node => Property(node, "sourceRawControlId") == rawChoice.Id);
        var semanticPopup = Assert.Single(Nodes(graph, GraphNodeKind.Surface, "semantic-world"),
            node => Property(node, "sourceRawSurfaceId") == rawPopup.Id && Property(node, "semanticSurfaceKind") == "PopupVariant");

        Assert.Equal(rawPopup.Id, Property(rawChoice, "rawSurfaceId"));
        Assert.Equal(semanticPopup.Id, Property(semanticChoice, "semanticSurfaceId"));
        var rawDataStreamChoice = Assert.Single(Nodes(graph, GraphNodeKind.Control, "raw-data-streams"),
            node => Property(node, "automationId") == "choice");
        var rawDataStreamOwnerId = Property(rawDataStreamChoice, "rawDataStreamSurfaceId");
        var rawDataStreamOwner = Assert.Single(Nodes(graph, GraphNodeKind.Window, "raw-data-streams"),
            node => node.Id == rawDataStreamOwnerId);
        Assert.Equal("RawWindow", Property(rawDataStreamOwner, "nativeWindowType"));
    }

    [Fact]
    public void DurableControlIdentityCoalescesAcrossDpiAndLabelChanges()
    {
        using var temp = new TempDirectory();
        var graph = new RecordingGraphBuilder().Build(SyntheticBundleFactory.Create(temp.Path));
        var rawSave = Assert.Single(Nodes(graph, GraphNodeKind.Control, "raw-world"),
            node => Property(node, "automationId") == "save");

        Assert.Equal(2, rawSave.Evidence.Select(evidence => evidence.FrameSequence).Distinct().Count());
        Assert.Contains(rawSave.Properties, property => property.Name == "name" && property.Value == "Save");
        Assert.Contains(rawSave.Properties, property => property.Name == "name" && property.Value == "Save As");
        Assert.Equal(2, Nodes(graph, GraphNodeKind.Control, "raw-world")
            .Count(node => node.Properties.Any(property => property.Name == "name" && property.Value == "Save")));
    }

    [Fact]
    public void VisualFallbackReachesSemanticWorldWithScaleIndependentIdentityAndEvidence()
    {
        using var temp = new TempDirectory();
        var graph = new RecordingGraphBuilder().Build(SyntheticBundleFactory.Create(temp.Path, visualFallback: true));
        var raw = Assert.Single(Nodes(graph, GraphNodeKind.Control, "raw-world"),
            node => Property(node, "className") == "UiAtlas.VisualControlRegion");
        var semantic = Assert.Single(Nodes(graph, GraphNodeKind.Control, "semantic-world"),
            node => Property(node, "sourceRawControlId") == raw.Id);

        Assert.Equal(2, raw.Evidence.Select(item => item.FrameSequence).Distinct().Count());
        Assert.Equal("visual-semantic-v3", Property(raw, "identityBasis"));
        Assert.Equal("button", Property(raw, "visualRole"));
        Assert.Equal("Save", Property(raw, "ocrText"));
        Assert.Equal("Button", Property(semantic, "semanticControlKind"));
        Assert.Equal("visual-semantic-v3", Property(semantic, "identityBasis"));
        Assert.Equal("button", Property(semantic, "visualRole"));
        Assert.Equal("Save", Property(semantic, "ocrText"));
        Assert.Equal("0123456789abcdef", Property(semantic, "visualFingerprint"));
        Assert.Equal(bool.TrueString, Property(semantic, "coordinateInvariant"));
        Assert.Equal(bool.TrueString, Property(semantic, "scaleInvariant"));
        Assert.Contains(semantic.Properties, property => property.Name == "evidenceSource" && property.Value == "Visual");
        Assert.Equal("0.9100", Property(semantic, "extractionConfidence"));
        Assert.Equal(2, semantic.Properties.Count(property => property.Name == "extractionCandidateId"));
        Assert.Equal(2, semantic.Properties.Count(property => property.Name == "evidenceId"));
    }

    [Fact]
    public void VisualOnlyFrameControlsReachRawAndSemanticWorldAndItsState()
    {
        using var temp = new TempDirectory();
        var graph = new RecordingGraphBuilder().Build(SyntheticBundleFactory.Create(
            temp.Path,
            visualFallback: true,
            representativeFrames: [1],
            firstTrigger: "adaptive-root-change",
            firstAutomationStatus: "visual-only"));

        var raw = Assert.Single(Nodes(graph, GraphNodeKind.Control, "raw-world"),
            node => Property(node, "className") == "UiAtlas.VisualControlRegion");
        Assert.Contains(Nodes(graph, GraphNodeKind.Control, "semantic-world"),
            node => Property(node, "sourceRawControlId") == raw.Id);
        Assert.Contains(graph.Nodes.Where(node => node.Kind == GraphNodeKind.State),
            state => int.TryParse(Property(state, "controlCount"), out var count) && count > 0);
    }

    [Fact]
    public void NodeIdentitiesDoNotDependOnSessionOrWindowTitle()
    {
        using var temp = new TempDirectory();
        var first = new RecordingGraphBuilder().Build(SyntheticBundleFactory.Create(temp.Path, "first.mlrec", sessionId: "session-a", windowTitle: "First document"));
        var second = new RecordingGraphBuilder().Build(SyntheticBundleFactory.Create(temp.Path, "second.mlrec", sessionId: "session-b", windowTitle: "Another document"));

        Assert.Equal(first.Nodes.Select(node => node.Id), second.Nodes.Select(node => node.Id));
    }

    [Fact]
    public void BuilderMaterializesOnlyStatebookRepresentativeFrames()
    {
        using var temp = new TempDirectory();
        var graph = new RecordingGraphBuilder().Build(SyntheticBundleFactory.Create(temp.Path, representativeFrames: [2]));

        var evidenceFrames = graph.Nodes.SelectMany(node => node.Evidence).Select(item => item.FrameSequence).Distinct().ToArray();
        Assert.Equal([2], evidenceFrames);
        Assert.DoesNotContain(Nodes(graph, GraphNodeKind.Control, "raw-world"),
            node => Property(node, "automationId") == "save" && node.Properties.Any(property => property.Value == "Save"));
    }

    [Fact]
    public void LegacyGraphMigrationIsExplicitAndValidated()
    {
        var node = new GraphNode("app_000000000000000000000000", GraphNodeKind.Application, "", "legacy", "Legacy", [], []);
        var hash = GraphSemantics.ComputeHash([node], []);
        var legacy = new UiKnowledgeGraph(new(FormatVersions.LegacyGraph, FormatVersions.Tool, "graph_000000000000000000000000",
            DateTimeOffset.UnixEpoch, "bundle", hash, FormatVersions.FullEvidenceProfile), [node], []);

        var migrated = GraphMigration.UpgradeToCurrent(legacy);

        Assert.Equal(FormatVersions.Graph, migrated.Metadata.FormatVersion);
        Assert.Equal("legacy-observed", Property(migrated.Nodes[0], "layer"));
        Assert.True(GraphValidator.Validate(migrated).IsValid);
    }

    private static IReadOnlyList<GraphNode> Nodes(UiKnowledgeGraph graph, GraphNodeKind kind, string layer) => graph.Nodes
        .Where(node => node.Kind == kind && Property(node, "layer") == layer).ToArray();
    private static string? Property(GraphNode node, string name) => node.Properties.FirstOrDefault(property => property.Name == name)?.Value;
}
