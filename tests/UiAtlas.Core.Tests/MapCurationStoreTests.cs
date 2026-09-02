using System.Text.Json;
using UiAtlas.Core.Build;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Storage;

namespace UiAtlas.Core.Tests;

public sealed class MapCurationStoreTests
{
    [Fact]
    public void ImportUsesSourceSidecarAndProjectsItIntoTheImportedGraph()
    {
        using var temp = new TempDirectory();
        var sourceBundle = SyntheticBundleFactory.Create(temp.Path, sessionId: "curation-import-source");
        var sourceGraph = new RecordingGraphBuilder().Build([sourceBundle]);
        var sourceMap = Path.Combine(temp.Path, "source.db");
        var targetMap = Path.Combine(temp.Path, "target.db");
        SqliteGraphStore.Save(sourceGraph, sourceMap);

        var surface = sourceGraph.Nodes.First(node => node.Kind == GraphNodeKind.Surface &&
                                                       Property(node, "layer") == "semantic-world");
        var annotation = new ManualControlAnnotation(
            "manual-import", surface.StableKey, surface.Id, "Imported Button", "Button",
            new(.1, .2, .3, .1), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var document = MapCurationStore.UpsertManualControl(
            MapCurationDocument.Empty(sourceGraph.Metadata.EffectiveLogicalMapId), annotation);
        MapCurationStore.Save(sourceMap, document);

        var resolved = MapCurationStore.ResolveForImport(sourceMap, sourceGraph, targetMap);
        Assert.NotNull(resolved);
        SqliteGraphStore.SaveImported(sourceGraph, targetMap, resolved);
        var imported = SqliteGraphStore.Load(targetMap);

        Assert.Contains(imported.Nodes, node => node.Kind == GraphNodeKind.Control &&
                                                Property(node, "manualAnnotationId") == annotation.Id);
        Assert.Contains(MapCurationStore.Load(targetMap, sourceGraph.Metadata.EffectiveLogicalMapId).ManualControls,
            item => item.Id == annotation.Id);
    }

    [Fact]
    public void ImportPreservesCatalogCurationForARebuiltCopyOfTheSameLogicalMap()
    {
        using var temp = new TempDirectory();
        var bundle = SyntheticBundleFactory.Create(temp.Path, sessionId: "curation-import-rebuild");
        var graph = new RecordingGraphBuilder().Build([bundle]);
        var sourceMap = Path.Combine(temp.Path, "rebuilt.db");
        var targetMap = Path.Combine(temp.Path, "catalog.db");
        SqliteGraphStore.Save(graph, sourceMap);
        SqliteGraphStore.Save(graph, targetMap);

        var surface = graph.Nodes.First(node => node.Kind == GraphNodeKind.Surface &&
                                                 Property(node, "layer") == "semantic-world");
        var annotation = new ManualControlAnnotation(
            "manual-preserved", surface.StableKey, surface.Id, "Preserved Button", "Button",
            new(.2, .3, .2, .08), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var document = MapCurationStore.UpsertManualControl(
            MapCurationDocument.Empty(graph.Metadata.EffectiveLogicalMapId), annotation);
        MapCurationStore.Save(targetMap, document);

        var resolved = MapCurationStore.ResolveForImport(sourceMap, graph, targetMap);

        Assert.NotNull(resolved);
        Assert.Contains(resolved!.ManualControls, item => item.Id == annotation.Id);
    }

    [Fact]
    public void ImportedMapReplacesAnIncompatibleStaleSidecarWithoutApplyingIt()
    {
        using var temp = new TempDirectory();
        var sourceBundle = SyntheticBundleFactory.Create(temp.Path, "source.mlrec", sessionId: "new-logical-map");
        var oldBundle = SyntheticBundleFactory.Create(temp.Path, "old.mlrec", sessionId: "old-logical-map");
        var sourceGraph = new RecordingGraphBuilder().Build([sourceBundle], "new-logical-map");
        var oldGraph = new RecordingGraphBuilder().Build([oldBundle], "old-logical-map");
        var targetMap = Path.Combine(temp.Path, "catalog.db");
        SqliteGraphStore.Save(oldGraph, targetMap);
        MapCurationStore.Save(targetMap, MapCurationDocument.Empty("old-logical-map"));

        var resolved = MapCurationStore.ResolveForImport(
            Path.Combine(temp.Path, "external.db"), sourceGraph, targetMap);
        SqliteGraphStore.SaveImported(sourceGraph, targetMap, resolved);

        Assert.Null(resolved);
        Assert.False(File.Exists(MapCurationStore.PathForMap(targetMap)));
        Assert.Equal("new-logical-map", SqliteGraphStore.Load(targetMap).Metadata.EffectiveLogicalMapId);
    }

    [Fact]
    public void NormalizedGeometrySurvivesDpiAndWindowSizeChanges()
    {
        var normalized = MapCurationStore.NormalizeBounds(
            new RectI(300, 260, 240, 80),
            new RectI(100, 100, 1_000, 800));

        Assert.NotNull(normalized);
        Assert.Equal(new RectI(500, 340, 480, 160),
            MapCurationStore.ProjectBounds(normalized!, new RectI(100, 20, 2_000, 1_600)));
    }

    [Fact]
    public void ManualButtonProjectsIntoRawAndSemanticWorldAndSurvivesRebuild()
    {
        using var temp = new TempDirectory();
        const string logicalMapId = "curated-map";
        var bundle = SyntheticBundleFactory.Create(temp.Path, sessionId: "curation-session");
        var graph = new RecordingGraphBuilder().Build([bundle], logicalMapId);
        var surface = SemanticSurface(graph);
        var now = new DateTimeOffset(2026, 8, 30, 1, 2, 3, TimeSpan.Zero);
        var annotation = new ManualControlAnnotation(
            "manual-checkout", surface.StableKey, surface.Id, "Preview", "Button",
            new(.25, .30, .20, .08), now, now);
        var document = MapCurationStore.UpsertManualControl(
            MapCurationDocument.Empty(logicalMapId), annotation);

        var curated = MapCurationStore.Apply(graph, document);

        var manual = curated.Nodes.Where(node => node.Kind == GraphNodeKind.Control &&
            Property(node, "manualAnnotationId") == annotation.Id).ToArray();
        Assert.Equal(2, manual.Length);
        Assert.Contains(manual, node => Property(node, "layer") == "raw-world" &&
                                        Property(node, "frameworkId") == "UiAtlas.UserAnnotation");
        var semantic = Assert.Single(manual, node => Property(node, "layer") == "semantic-world");
        Assert.Equal("Button", Property(semantic, "controlType"));
        Assert.Equal("Button", Property(semantic, "semanticControlKind"));
        Assert.Equal("button", Property(semantic, "role"));
        Assert.Equal("Confirmed", Property(semantic, "verificationStatus"));
        Assert.Equal("User", Property(semantic, "confirmationSource"));
        Assert.Equal("UserDrawn", Property(semantic, "geometrySource"));
        Assert.Equal("VisualCoordinate", Property(semantic, "interactionMethod"));
        Assert.Equal("Unobserved", Property(semantic, "actionVerificationStatus"));
        Assert.Equal("False", Property(semantic, "safeForAutoExplore"));
        Assert.DoesNotContain(semantic.Properties, property => property.Name == "supportedPattern");
        Assert.True(GraphValidator.Validate(curated).IsValid);

        var mapPath = Path.Combine(temp.Path, "curated.db");
        MapCurationStore.Save(mapPath, document);
        SqliteGraphStore.Save(graph, mapPath);
        var reopened = SqliteGraphStore.Load(mapPath);
        Assert.Contains(reopened.Nodes, node => Property(node, "manualAnnotationId") == annotation.Id);

        var rebuilt = MapCurationStore.Apply(graph, MapCurationStore.Load(mapPath, logicalMapId));
        Assert.Contains(rebuilt.Nodes, node => Property(node, "manualAnnotationId") == annotation.Id);
    }

    [Fact]
    public void HumanReadableAndVNextExportsDescribeManualButtonWithoutNativeInvokePattern()
    {
        using var temp = new TempDirectory();
        const string logicalMapId = "export-curation";
        var bundle = SyntheticBundleFactory.Create(temp.Path, sessionId: "export-session");
        var graph = new RecordingGraphBuilder().Build([bundle], logicalMapId);
        var surface = SemanticSurface(graph);
        var now = DateTimeOffset.UtcNow;
        var document = new MapCurationDocument("map-curation/1", logicalMapId,
        [
            new("manual-export", surface.StableKey, surface.Id, "Help", "Button",
                new(.60, .75, .15, .08), now, now)
        ], []);
        var curated = MapCurationStore.Apply(graph, document);

        var humanPath = Path.Combine(temp.Path, "human.json");
        HumanReadableMapExporter.Publish(curated, humanPath, true);
        using var human = JsonDocument.Parse(File.ReadAllBytes(humanPath));
        var humanText = human.RootElement.GetRawText();
        Assert.Contains("UiAtlas.UserAnnotation", humanText, StringComparison.Ordinal);
        Assert.Contains("VisualCoordinate", humanText, StringComparison.Ordinal);
        Assert.Contains("safeForAutoExplore", humanText, StringComparison.Ordinal);
        Assert.Contains("False", humanText, StringComparison.Ordinal);

        var vNextPath = Path.Combine(temp.Path, UiAtlasVNextCompatibilityExporter.RequiredFileName);
        UiAtlasVNextCompatibilityExporter.Publish(curated, vNextPath, "curated-export", true);
        using var vNext = JsonDocument.Parse(File.ReadAllBytes(vNextPath));
        var exported = vNext.RootElement.GetProperty("authoring").GetProperty("controls")
            .EnumerateArray().Single(control => control.GetProperty("label").GetString() == "Help");
        Assert.Equal("Button", exported.GetProperty("controlType").GetString());
        Assert.Equal("button", exported.GetProperty("role").GetString());
        Assert.Equal("User", exported.GetProperty("confirmationSource").GetString());
        Assert.Equal("UserDrawn", exported.GetProperty("geometrySource").GetString());
        Assert.Equal("VisualCoordinate", exported.GetProperty("interactionMethod").GetString());
        Assert.Equal("Unobserved", exported.GetProperty("actionVerificationStatus").GetString());
        Assert.False(exported.GetProperty("safeForAutoExplore").GetBoolean());
        Assert.Empty(exported.GetProperty("supportedPatterns").EnumerateArray());
    }

    [Fact]
    public void ManualButtonCanBeRenamedResizedAndDeletedWithoutLeavingProjectedNodes()
    {
        using var temp = new TempDirectory();
        const string logicalMapId = "edited-curation";
        var graph = new RecordingGraphBuilder().Build(
            [SyntheticBundleFactory.Create(temp.Path, sessionId: "edited-session")], logicalMapId);
        var surface = SemanticSurface(graph);
        var now = DateTimeOffset.UtcNow;
        var original = new ManualControlAnnotation("manual-edit", surface.StableKey, surface.Id,
            "First", "Button", new(.1, .1, .2, .1), now, now);
        var document = MapCurationStore.UpsertManualControl(MapCurationDocument.Empty(logicalMapId), original);
        var first = MapCurationStore.Apply(graph, document);
        var edited = original with { Label = "Renamed", Bounds = new(.4, .5, .25, .12), UpdatedUtc = now.AddMinutes(1) };
        document = MapCurationStore.UpsertManualControl(document, edited);

        var reapplied = MapCurationStore.Reapply(first, document);

        var semantic = Assert.Single(reapplied.Nodes, node =>
            Property(node, "manualAnnotationId") == edited.Id && Property(node, "layer") == "semantic-world");
        Assert.Equal("Renamed", semantic.Label);
        var expected = MapCurationStore.ProjectBounds(edited.Bounds, surface.Evidence[0].Bounds!);
        Assert.Contains(semantic.Evidence, evidence => evidence.Bounds == expected);

        var deleted = MapCurationStore.Reapply(reapplied,
            MapCurationStore.RemoveManualControl(document, edited.Id));
        Assert.DoesNotContain(deleted.Nodes, node => Property(node, "manualAnnotationId") == edited.Id);
    }

    [Fact]
    public void SuccessfulRecordedUserClickVerifiesCoordinateActionAndLinksInteraction()
    {
        using var temp = new TempDirectory();
        const string logicalMapId = "verified-curation";
        var graph = new RecordingGraphBuilder().Build(
            [SyntheticBundleFactory.Create(temp.Path, sessionId: "verified-session", interactionTrace: true)],
            logicalMapId);
        var edge = graph.Edges.First(candidate => candidate.Kind == "interaction" &&
            candidate.Properties.Any(property => property is { Name: "outcome", Value: "Succeeded" }) &&
            candidate.Properties.Any(property => property is { Name: "actor", Value: "User" }) &&
            candidate.Properties.Any(property => property is { Name: "gesture", Value: "Click" }));
        var sourceId = edge.Properties.Single(property => property.Name == "sourceControlId").Value;
        var source = graph.Nodes.Single(node => node.Id == sourceId);
        var rawSurfaceId = Property(source, "rawSurfaceId")!;
        var rawSurface = graph.Nodes.Single(node => node.Id == rawSurfaceId);
        var surface = graph.Nodes.Single(node => node.Kind == GraphNodeKind.Surface &&
            Property(node, "layer") == "semantic-world" && Property(node, "sourceRawSurfaceId") == rawSurfaceId);
        var sourceEvidence = source.Evidence[0];
        var normalized = MapCurationStore.NormalizeBounds(sourceEvidence.Bounds!, rawSurface.Evidence[0].Bounds!);
        Assert.NotNull(normalized);
        var now = DateTimeOffset.UtcNow;
        var annotation = new ManualControlAnnotation("manual-verified", surface.StableKey, surface.Id,
            "User Action", "Button", normalized!, now, now);

        var curated = MapCurationStore.Apply(graph,
            new("map-curation/1", logicalMapId, [annotation], []));

        var semantic = Assert.Single(curated.Nodes, node =>
            Property(node, "manualAnnotationId") == annotation.Id && Property(node, "layer") == "semantic-world");
        var raw = Assert.Single(curated.Nodes, node =>
            Property(node, "manualAnnotationId") == annotation.Id && Property(node, "layer") == "raw-world");
        Assert.Equal("Observed", Property(semantic, "actionVerificationStatus"));
        Assert.Equal("True", Property(semantic, "safeForAutoExplore"));
        Assert.Contains(curated.Edges, candidate => candidate.Id == edge.Id &&
            candidate.Properties.Any(property => property.Name == "sourceControlId" && property.Value == raw.Id) &&
            candidate.Properties.Any(property => property.Name == "manualAnnotationId" && property.Value == annotation.Id));
        Assert.True(GraphValidator.Validate(curated).IsValid);
    }

    [Fact]
    public void CandidateConfirmationAndSuppressionRulesReapplyAfterGraphRebuild()
    {
        using var temp = new TempDirectory();
        const string logicalMapId = "candidate-curation";
        var bundle = SyntheticBundleFactory.Create(temp.Path, visualFallback: true);
        var graph = new RecordingGraphBuilder().Build([bundle], logicalMapId);
        var candidate = graph.Nodes.First(node => node.Kind == GraphNodeKind.Control &&
            Property(node, "layer") == "semantic-world" &&
            Property(node, "className") == "UiAtlas.VisualControlRegion" &&
            Property(node, "controlType")?.Contains("Button", StringComparison.OrdinalIgnoreCase) == true);
        graph = WithVerification(graph, candidate, "Unverified");
        candidate = graph.Nodes.Single(node => node.Id == candidate.Id);
        var now = DateTimeOffset.UtcNow;

        var confirmed = MapCurationStore.Apply(graph,
            MapCurationStore.UpsertRule(MapCurationDocument.Empty(logicalMapId), candidate.StableKey, "Confirm", now));
        Assert.Equal("Confirmed", Property(confirmed.Nodes.Single(node => node.Id == candidate.Id), "verificationStatus"));

        var suppressed = MapCurationStore.Apply(graph,
            MapCurationStore.UpsertRule(MapCurationDocument.Empty(logicalMapId), candidate.StableKey, "Suppress", now));
        Assert.DoesNotContain(suppressed.Nodes, node => node.Id == candidate.Id);
    }

    [Fact]
    public void LaterNativeButtonMergesWithManualAnnotationWithoutDuplicate()
    {
        using var temp = new TempDirectory();
        const string logicalMapId = "native-merge";
        var graph = new RecordingGraphBuilder().Build(
            [SyntheticBundleFactory.Create(temp.Path, sessionId: "native-session")], logicalMapId);
        var native = graph.Nodes.First(node => node.Kind == GraphNodeKind.Control &&
            Property(node, "layer") == "semantic-world" &&
            Property(node, "controlType")?.Contains("Button", StringComparison.OrdinalIgnoreCase) == true &&
            node.Evidence.Any(evidence => evidence.Bounds is { Width: > 0, Height: > 0 }));
        var surfaceId = Property(native, "semanticSurfaceId")!;
        var surface = graph.Nodes.Single(node => node.Id == surfaceId);
        var evidence = native.Evidence.First(item => item.Bounds is { Width: > 0, Height: > 0 });
        var surfaceBounds = surface.Evidence.First(item => item.BundleId == evidence.BundleId &&
            item.FrameSequence == evidence.FrameSequence).Bounds!;
        var normalized = MapCurationStore.NormalizeBounds(evidence.Bounds!, surfaceBounds);
        Assert.NotNull(normalized);
        var now = DateTimeOffset.UtcNow;
        var annotation = new ManualControlAnnotation("manual-native", surface.StableKey, surface.Id,
            native.Label, "Button", normalized!, now, now);

        var merged = MapCurationStore.Apply(graph,
            new("map-curation/1", logicalMapId, [annotation], []));

        Assert.Equal(graph.Nodes.Count, merged.Nodes.Count);
        var updated = merged.Nodes.Single(node => node.Id == native.Id);
        Assert.Equal(annotation.Id, Property(updated, "manualAnnotationId"));
        Assert.Equal("User", Property(updated, "confirmationSource"));
    }

    private static GraphNode SemanticSurface(UiKnowledgeGraph graph) => graph.Nodes.First(node =>
        node.Kind == GraphNodeKind.Surface && Property(node, "layer") == "semantic-world" &&
        Property(node, "semanticSurfaceKind") != "PopupFamily" &&
        node.Evidence.Any(evidence => evidence.Bounds is { Width: > 0, Height: > 0 }));

    private static string? Property(GraphNode node, string name) =>
        node.Properties.FirstOrDefault(property => property.Name == name)?.Value;

    private static UiKnowledgeGraph WithVerification(UiKnowledgeGraph graph, GraphNode target, string status)
    {
        var rawId = Property(target, "sourceRawControlId");
        var nodes = graph.Nodes.Select(node => node.Id == target.Id || node.Id == rawId
            ? node with
            {
                Properties = node.Properties.Where(property => property.Name != "verificationStatus")
                    .Append(new("verificationStatus", status)).ToArray()
            }
            : node).OrderBy(node => node.Id, StringComparer.Ordinal).ToArray();
        return graph with
        {
            Nodes = nodes,
            Metadata = graph.Metadata with { SemanticHash = GraphSemantics.ComputeHash(nodes, graph.Edges) }
        };
    }
}
