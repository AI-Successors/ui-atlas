using UiAtlas.Core.Build;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Reader;

namespace UiAtlas.Core.Tests;

public sealed class UiMappingReadModelTests
{
    [Fact]
    public void BuilderPreservesRawDataStreamsBeforeRawAndSemanticWorlds()
    {
        using var temp = new TempDirectory();
        var graph = new RecordingGraphBuilder().Build(SyntheticBundleFactory.Create(temp.Path));
        var model = new UiMappingReadModel(graph);

        var streams = model.LayerFor(UiUnderstandingLevel.RawDataStreams);
        var raw = model.LayerFor(UiUnderstandingLevel.RawWorld);
        var semantic = model.LayerFor(UiUnderstandingLevel.SemanticWorld);

        Assert.NotEmpty(streams.Surfaces);
        Assert.NotEmpty(streams.Controls);
        Assert.NotEmpty(raw.Surfaces);
        Assert.NotEmpty(semantic.Surfaces);
        Assert.All(raw.Surfaces, surface =>
            Assert.NotEmpty(UiMappingReadModel.Properties(surface.Source, "sourceRawDataStreamSurfaceId")));
        Assert.All(semantic.Surfaces, surface =>
            Assert.NotEmpty(UiMappingReadModel.Properties(surface.Source, "sourceRawSurfaceId")));
    }

    [Fact]
    public void UnderstandingPipelineUsesProgressiveHorizons()
    {
        using var temp = new TempDirectory();
        var model = new UiMappingReadModel(new RecordingGraphBuilder().Build(SyntheticBundleFactory.Create(temp.Path)));

        var streams = model.BuildPipeline(UiUnderstandingLevel.RawDataStreams);
        var raw = model.BuildPipeline(UiUnderstandingLevel.RawWorld);
        var semantic = model.BuildPipeline(UiUnderstandingLevel.SemanticWorld);

        Assert.DoesNotContain(streams.Nodes, node => node.Kind is UiPipelineNodeKind.RawSurface or UiPipelineNodeKind.SemanticSurface);
        Assert.Contains(raw.Nodes, node => node.Kind == UiPipelineNodeKind.RawSurface);
        Assert.DoesNotContain(raw.Nodes, node => node.Kind == UiPipelineNodeKind.SemanticSurface);
        Assert.Contains(semantic.Nodes, node => node.Kind == UiPipelineNodeKind.SemanticSurface);
        Assert.Contains(semantic.Edges, edge => edge.DisplayName == "Semantic World");
    }

    [Fact]
    public void UnderstandingPipelineKeepsPrimaryWindowLineageOnRowZero()
    {
        using var temp = new TempDirectory();
        var model = new UiMappingReadModel(new RecordingGraphBuilder().Build(SyntheticBundleFactory.Create(temp.Path)));
        var topology = model.BuildPipeline(UiUnderstandingLevel.SemanticWorld);
        var rawWindow = Assert.Single(model.LayerFor(UiUnderstandingLevel.RawWorld).Surfaces,
            surface => surface.SurfaceKind == "RawWindow");
        var semanticWindow = Assert.Single(model.LayerFor(UiUnderstandingLevel.SemanticWorld).Surfaces,
            surface => UiMappingReadModel.Property(surface.Source, "semanticClass") == "SemanticWindow");

        Assert.Equal(0, Assert.Single(topology.Nodes, node => node.Kind == UiPipelineNodeKind.Application).Row);
        Assert.Equal(0, Assert.Single(topology.Nodes, node => node.Kind == UiPipelineNodeKind.Process).Row);
        Assert.Equal(0, Assert.Single(topology.Nodes, node =>
            node.Kind == UiPipelineNodeKind.NativeSurface && node.DisplayName == "Main Window").Row);
        Assert.Equal(0, Assert.Single(topology.Nodes, node => node.SurfaceId == rawWindow.Id).Row);
        Assert.Equal(0, Assert.Single(topology.Nodes, node => node.SurfaceId == semanticWindow.Id).Row);
        Assert.Equal([0, 1, 2, 3, 4], topology.Nodes
            .Where(node => node.Row == 0)
            .OrderBy(node => node.Column)
            .Select(node => node.Column));
        Assert.All(topology.Nodes.Where(node => node.Kind == UiPipelineNodeKind.RawSurface && node.SurfaceId != rawWindow.Id),
            node => Assert.True(node.Row > 0));
    }

    [Fact]
    public void RawDataStreamControlsRetainVariantSpecificEvidence()
    {
        using var temp = new TempDirectory();
        var model = new UiMappingReadModel(new RecordingGraphBuilder().Build(SyntheticBundleFactory.Create(temp.Path)));
        var streams = model.LayerFor(UiUnderstandingLevel.RawDataStreams);

        Assert.All(streams.Controls, control => Assert.Single(control.Evidence.Select(item => item.FrameSequence).Distinct()));
        Assert.True(streams.Surfaces.SelectMany(surface => surface.Evidence).Select(item => item.FrameSequence).Distinct().Count() >= 2);
        var firstFrame = Assert.Single(streams.Surfaces.SelectMany(surface => surface.Variants),
            variant => variant.FrameSequence == 1);
        Assert.Equal(2, firstFrame.ControlCount);
    }

    [Fact]
    public void SemanticHorizonResolvesSelectableSurfacesFromEveryVisibleColumn()
    {
        using var temp = new TempDirectory();
        var model = new UiMappingReadModel(new RecordingGraphBuilder().Build(SyntheticBundleFactory.Create(temp.Path)));
        var topology = model.BuildPipeline(UiUnderstandingLevel.SemanticWorld);

        foreach (var node in topology.Nodes.Where(node => node.InspectionLevel is not null))
        {
            var surfaces = model.ResolvePipelineSurfaces(node);
            Assert.NotEmpty(surfaces);
            Assert.All(surfaces, surface => Assert.Equal(node.InspectionLevel, surface.Level));
        }
    }

    [Fact]
    public void ObservedVariantsAreFrameBasedAndCarryFrameControlMembership()
    {
        using var temp = new TempDirectory();
        var model = new UiMappingReadModel(new RecordingGraphBuilder().Build(SyntheticBundleFactory.Create(temp.Path)));
        var rawWindow = Assert.Single(model.LayerFor(UiUnderstandingLevel.RawWorld).Surfaces,
            surface => surface.SurfaceKind == "RawWindow");

        Assert.Equal([1L, 2L], rawWindow.Variants.Select(variant => variant.FrameSequence));
        Assert.All(rawWindow.Variants, variant => Assert.Equal(
            model.LayerFor(UiUnderstandingLevel.RawWorld).ControlsForSurface(rawWindow.Id, variant.FrameSequence)
                .Select(control => control.Id).OrderBy(id => id, StringComparer.Ordinal),
            variant.ControlIds));
        Assert.Equal([1L], model.VariantsFor([rawWindow]).Select(variant => variant.FrameSequence));
        Assert.Equal("popup_effective_owner_frame", rawWindow.Variants.Single(variant => variant.FrameSequence == 2).Reason);
        var frameTwoControl = model.LayerFor(UiUnderstandingLevel.RawWorld).ControlsForSurface(rawWindow.Id, 2)
            .First(control => !control.Evidence.Any(evidence => evidence.FrameSequence == 1));
        Assert.Equal([1L, 2L], model.VariantsFor([rawWindow], frameTwoControl)
            .Select(variant => variant.FrameSequence));
    }

    [Fact]
    public void VariantsRemainBundleScopedAcrossMergedResumeSessions()
    {
        using var temp = new TempDirectory();
        var first = SyntheticBundleFactory.Create(temp.Path, "first.mlrec", sessionId: "session-a");
        var second = SyntheticBundleFactory.Create(temp.Path, "second.mlrec", sessionId: "session-b");
        var model = new UiMappingReadModel(new RecordingGraphBuilder().Build([first, second], logicalMapId: "logical-map"));
        var rawWindow = Assert.Single(model.LayerFor(UiUnderstandingLevel.RawWorld).Surfaces,
            surface => surface.SurfaceKind == "RawWindow");

        Assert.Contains(rawWindow.Variants, variant => variant.BundleId == "session-a" && variant.FrameSequence == 1);
        Assert.Contains(rawWindow.Variants, variant => variant.BundleId == "session-b" && variant.FrameSequence == 1);
        Assert.All(rawWindow.Variants, variant => Assert.Equal(
            model.LayerFor(UiUnderstandingLevel.RawWorld).ControlsForSurface(rawWindow.Id, variant.FrameSequence, variant.BundleId)
                .Select(control => control.Id).OrderBy(id => id, StringComparer.Ordinal),
            variant.ControlIds));
    }

    [Fact]
    public void AppMapProjectionPoliciesMatchCanonicalFiveModeComposition()
    {
        Assert.False(UiMapPresentation.ShouldCropSceneToSurface(UiMapProjectionMode.Window));
        Assert.True(UiMapPresentation.ShouldCropSceneToSurface(UiMapProjectionMode.Overlay));
        Assert.False(UiMapPresentation.ShouldCropSceneToSurface(UiMapProjectionMode.Trace));
        Assert.Equal(new(true, 1.0, false, false, false, 0.0), UiMapPresentation.PolicyFor(UiMapProjectionMode.Window));
        Assert.Equal(new(false, 0.0, true, true, false, 0.96), UiMapPresentation.PolicyFor(UiMapProjectionMode.Controls));
        Assert.Equal(new(true, 0.68, false, true, false, 0.0), UiMapPresentation.PolicyFor(UiMapProjectionMode.Overlay));
        Assert.Equal(new(false, 0.0, false, true, true, 0.94), UiMapPresentation.PolicyFor(UiMapProjectionMode.Structure));
        Assert.Equal(new(true, 0.68, false, true, false, 0.20), UiMapPresentation.PolicyFor(UiMapProjectionMode.StructureOverlay));
        Assert.Equal(new(true, 1.0, false, false, false, 0.0), UiMapPresentation.PolicyFor(UiMapProjectionMode.Trace));
        Assert.True(UiMapPresentation.ShouldShowModeLabels(700, 600));
        Assert.False(UiMapPresentation.ShouldShowModeLabels(599, 600));
    }

    [Fact]
    public void AppMapControlCompositionKeepsStructuralFramesBehindInteractiveCrops()
    {
        using var temp = new TempDirectory();
        var model = new UiMappingReadModel(new RecordingGraphBuilder().Build(SyntheticBundleFactory.Create(temp.Path)));
        var layer = model.LayerFor(UiUnderstandingLevel.RawWorld);
        var surface = Assert.Single(layer.Surfaces, item => item.SurfaceKind == "RawWindow");
        var controls = layer.ControlsForSurface(surface.Id, 1);
        var document = Assert.Single(controls, control => control.CanonicalKind.Contains("Pane", StringComparison.OrdinalIgnoreCase));
        var save = Assert.Single(controls, control => UiMappingReadModel.Property(control.Source, "automationId") == "save");

        Assert.True(UiMapPresentation.IsLargeStructuralControl(document, surface));
        Assert.False(UiMapPresentation.ShouldUseControlCrop(document, surface));
        Assert.False(UiMapPresentation.ShouldRenderControl(document, surface, UiMapProjectionMode.Controls));
        Assert.Equal(0, UiMapPresentation.ControlRenderPriority(document, surface));
        Assert.False(UiMapPresentation.IsLargeStructuralControl(save, surface));
        Assert.True(UiMapPresentation.ShouldUseControlCrop(save, surface));
        Assert.Equal(20, UiMapPresentation.ControlRenderPriority(save, surface));
        Assert.True(UiMapPresentation.PolicyFor(UiMapProjectionMode.Controls).ShowsControlCrops);
        Assert.False(UiMapPresentation.PolicyFor(UiMapProjectionMode.Overlay).ShowsControlCrops);
    }

    [Fact]
    public void ControlsModeIncludesWorksheetCellsAndHeadersButNotTheWholeGridContainer()
    {
        using var temp = new TempDirectory();
        var model = new UiMappingReadModel(new RecordingGraphBuilder().Build(
            SyntheticBundleFactory.Create(temp.Path, worksheetControls: true)));
        var layer = model.LayerFor(UiUnderstandingLevel.RawWorld);
        var surface = Assert.Single(layer.Surfaces, item => item.SurfaceKind == "RawWindow");
        var cell = Assert.Single(layer.Controls, control =>
            control.CanonicalKind == "DataItem" && control.DisplayName == "A1");
        var columnHeader = Assert.Single(layer.Controls, control =>
            control.CanonicalKind == "DataItem" && control.DisplayName == "A");
        var gridContainer = Assert.Single(layer.Controls, control => control.CanonicalKind == "Pane");

        Assert.True(UiMapPresentation.ShouldRenderControl(cell, surface, UiMapProjectionMode.Controls));
        Assert.True(UiMapPresentation.ShouldUseControlCrop(cell, surface));
        Assert.True(UiMapPresentation.ShouldRenderControl(columnHeader, surface, UiMapProjectionMode.Controls));
        Assert.False(UiMapPresentation.ShouldRenderControl(gridContainer, surface, UiMapProjectionMode.Controls));
        Assert.True(UiMapPresentation.ShouldRenderControl(gridContainer, surface, UiMapProjectionMode.Structure));
    }

    [Fact]
    public void AppMapDoesNotDrawOffscreenRawProviderControls()
    {
        using var temp = new TempDirectory();
        var model = new UiMappingReadModel(new RecordingGraphBuilder().Build(SyntheticBundleFactory.Create(temp.Path)));
        var layer = model.LayerFor(UiUnderstandingLevel.RawDataStreams);
        var hidden = Assert.Single(layer.Controls, control =>
            UiMappingReadModel.Property(control.Source, "offscreen") == "True");
        var surface = Assert.Single(layer.Surfaces, candidate => candidate.Id == hidden.OwnerSurfaceId);

        Assert.False(UiMapPresentation.ShouldRenderControl(hidden, surface, UiMapProjectionMode.Controls));
        Assert.False(UiMapPresentation.ShouldRenderControl(hidden, surface, UiMapProjectionMode.Overlay));
    }

    [Fact]
    public void AppMapHidesOffscreenCachedWorksheetCellsButKeepsOtherCachedHints()
    {
        var surfaceNode = new GraphNode("surface", GraphNodeKind.Surface, "app", "surface", "Excel",
            [new GraphProperty("surfaceClass", "RawWindow")], []);
        var surface = new UiMapSurfaceView("surface", UiUnderstandingLevel.RawWorld, "Excel",
            "RawWindow", "app", new RectI(100, 100, 1200, 800), 2, [], [], surfaceNode);
        var cell = CachedControl("cell", "A1", "DataItem", new RectI(240, 300, 80, 22));
        var button = CachedControl("button", "Ready", "Button", new RectI(110, 875, 90, 20));

        Assert.False(UiMapPresentation.ShouldRenderControl(cell, surface, UiMapProjectionMode.Controls));
        Assert.False(UiMapPresentation.ShouldRenderControl(cell, surface, UiMapProjectionMode.Overlay));
        Assert.True(UiMapPresentation.ShouldRenderControl(button, surface, UiMapProjectionMode.Controls));
        Assert.True(UiMapPresentation.ShouldRenderControl(button, surface, UiMapProjectionMode.Overlay));
    }

    [Fact]
    public void AppMapStillHidesCachedControlsWithoutUsableSurfaceGeometry()
    {
        var surfaceNode = new GraphNode("surface", GraphNodeKind.Surface, "app", "surface", "Excel",
            [new GraphProperty("surfaceClass", "RawWindow")], []);
        var surface = new UiMapSurfaceView("surface", UiUnderstandingLevel.RawWorld, "Excel",
            "RawWindow", "app", new RectI(100, 100, 1200, 800), 2, [], [], surfaceNode);
        var empty = CachedControl("empty", "Empty", "Button", new RectI(200, 200, 0, 20));
        var outside = CachedControl("outside", "Outside", "DataItem", new RectI(1400, 100, 80, 22));

        Assert.False(UiMapPresentation.ShouldRenderControl(empty, surface, UiMapProjectionMode.Controls));
        Assert.False(UiMapPresentation.ShouldRenderControl(outside, surface, UiMapProjectionMode.Overlay));
    }

    private static UiMapControlView CachedControl(string id, string name, string kind, RectI bounds)
    {
        var node = new GraphNode(id, GraphNodeKind.Control, "surface", id, name,
            [
                new GraphProperty("controlType", kind),
                new GraphProperty("frameworkId", "UiAtlas.Cached"),
                new GraphProperty("className", kind == "DataItem" ? "XLSpreadsheetCell" : "CachedButton"),
                new GraphProperty("effectivelyVisible", "False"),
                new GraphProperty("offscreen", "True"),
                new GraphProperty("verificationStatus", "Unverified")
            ], []);
        return new UiMapControlView(id, UiUnderstandingLevel.RawWorld, name, kind,
            "surface", "", bounds, [], node);
    }

    [Fact]
    public void RawFrameVariantCountsAndDrawsUnverifiedVisualCandidates()
    {
        using var temp = new TempDirectory();
        var model = new UiMappingReadModel(new RecordingGraphBuilder().Build(SyntheticBundleFactory.Create(
            temp.Path,
            firstTrigger: "adaptive-root-change",
            firstAutomationTimedOut: true,
            firstAutomationStatus: "timeout",
            visualFallback: true)));
        var layer = model.LayerFor(UiUnderstandingLevel.RawDataStreams);
        var visual = Assert.Single(layer.Controls, control =>
            UiMappingReadModel.Property(control.Source, "className") == "UiAtlas.VisualControlRegion" &&
            control.Evidence.Any(evidence => evidence.FrameSequence == 1));
        var surface = Assert.Single(layer.Surfaces, candidate => candidate.Id == visual.OwnerSurfaceId);
        var variant = Assert.Single(surface.Variants, candidate => candidate.FrameSequence == 1);

        Assert.Contains(visual.Id, variant.ControlIds);
        Assert.True(variant.ControlCount > 0);
        Assert.True(UiMapPresentation.ShouldRenderControl(visual, surface, UiMapProjectionMode.Overlay));
    }

    [Fact]
    public void AppMapDoesNotDrawEstimatedOwnerDrawnRowsOverScreenshot()
    {
        var surfaceNode = new GraphNode("surface", GraphNodeKind.Surface, "app", "surface", "Revit",
            [new GraphProperty("surfaceClass", "RawWindow")], []);
        var surface = new UiMapSurfaceView("surface", UiUnderstandingLevel.RawWorld, "Revit",
            "RawWindow", "app", new RectI(0, 0, 1200, 800), 1, [], [], surfaceNode);
        var rowNode = new GraphNode("row", GraphNodeKind.Control, "surface", "row", "Property row 1",
            [
                new GraphProperty("controlType", "DataItem"),
                new GraphProperty("effectivelyVisible", "True"),
                new GraphProperty("frameworkId", "Win32"),
                new GraphProperty("className", "RevitPropertyGridRow")
            ], []);
        var row = new UiMapControlView("row", UiUnderstandingLevel.RawWorld,
            "Property row 1", "DataItem", "surface", "", new RectI(10, 300, 300, 25), [], rowNode);

        Assert.False(UiMapPresentation.ShouldRenderControl(row, surface, UiMapProjectionMode.Controls));
        Assert.False(UiMapPresentation.ShouldRenderControl(row, surface, UiMapProjectionMode.Overlay));
        Assert.False(UiMapPresentation.ShouldRenderControl(row, surface, UiMapProjectionMode.StructureOverlay));
    }

    [Fact]
    public void AppMapRendersOwnerDrawnAccountActionsButNotTheirTextLabels()
    {
        var surfaceNode = new GraphNode("surface", GraphNodeKind.Surface, "app", "surface", "Account",
            [new GraphProperty("surfaceClass", "RawPopupWindow")], []);
        var surface = new UiMapSurfaceView("surface", UiUnderstandingLevel.RawWorld, "Account",
            "RawPopupWindow", "app", new RectI(100, 100, 500, 600), 2, [], [], surfaceNode);
        var actionNode = new GraphNode("action", GraphNodeKind.Control, "surface", "action",
            "Sign out of this account",
            [new GraphProperty("controlType", "Custom"), new GraphProperty("effectivelyVisible", "True")], []);
        var labelNode = new GraphNode("label", GraphNodeKind.Control, "action", "label", "Sign Out",
            [new GraphProperty("controlType", "Text"), new GraphProperty("effectivelyVisible", "True")], []);
        var action = new UiMapControlView("action", UiUnderstandingLevel.RawWorld,
            "Sign out of this account", "Custom", "surface", "", new RectI(400, 110, 100, 60), [], actionNode);
        var label = new UiMapControlView("label", UiUnderstandingLevel.RawWorld,
            "Sign Out", "Text", "surface", "action", new RectI(420, 130, 60, 20), [], labelNode);

        Assert.True(UiMapPresentation.ShouldRenderControl(action, surface, UiMapProjectionMode.Controls));
        Assert.True(UiMapPresentation.ShouldUseControlCrop(action, surface));
        Assert.False(UiMapPresentation.ShouldRenderControl(label, surface, UiMapProjectionMode.Controls));
    }

    [Fact]
    public void AppMapRendersPointerObservedCanvasTargetsAsInteractiveRegions()
    {
        var surfaceNode = new GraphNode("surface", GraphNodeKind.Surface, "app", "surface", "Canvas",
            [new GraphProperty("surfaceClass", "RawWindow")], []);
        var surface = new UiMapSurfaceView("surface", UiUnderstandingLevel.RawWorld, "Canvas",
            "RawWindow", "app", new RectI(100, 100, 800, 600), 1, [], [], surfaceNode);
        var targetNode = new GraphNode("target", GraphNodeKind.Control, "surface", "target", "Observed canvas target",
            [new GraphProperty("controlType", "CanvasItem"), new GraphProperty("effectivelyVisible", "True")], []);
        var target = new UiMapControlView("target", UiUnderstandingLevel.RawWorld,
            "Observed canvas target", "CanvasItem", "surface", "", new RectI(320, 440, 18, 18), [], targetNode);

        Assert.True(UiMapPresentation.ShouldRenderControl(target, surface, UiMapProjectionMode.Controls));
        Assert.True(UiMapPresentation.ShouldRenderControl(target, surface, UiMapProjectionMode.Overlay));
        Assert.True(UiMapPresentation.ShouldUseControlCrop(target, surface));
    }

    [Fact]
    public void AppMapEmphasizesEveryCompactPopupListRowAsAnAction()
    {
        var surfaceNode = new GraphNode("surface", GraphNodeKind.Surface, "app", "surface", "Undo_Dropdown",
            [new GraphProperty("surfaceClass", "RawPopupWindow")], []);
        var rowNode = new GraphNode("row", GraphNodeKind.Control, "surface", "row", "Zoom",
            [new GraphProperty("controlType", "ListItem"), new GraphProperty("effectivelyVisible", "True")], []);
        var surface = new UiMapSurfaceView("surface", UiUnderstandingLevel.SemanticWorld, "Undo_Dropdown",
            "RawPopupWindow", "app", new RectI(223, 47, 133, 663), 1, [], [], surfaceNode);
        var row = new UiMapControlView("row", UiUnderstandingLevel.SemanticWorld,
            "Zoom", "ListItem", "surface", "list", new RectI(224, 79, 131, 30), [], rowNode);

        Assert.True(UiMapPresentation.IsCompactPopupAction(row, surface));
        Assert.True(UiMapPresentation.ShouldRenderControl(row, surface, UiMapProjectionMode.Overlay));
    }

    [Fact]
    public void AppMapSuppressesEditThatDuplicatesACompactPopupListRow()
    {
        var surfaceNode = new GraphNode("surface", GraphNodeKind.Surface, "app", "surface", "Undo_Dropdown",
            [new GraphProperty("surfaceClass", "RawPopupWindow")], []);
        var rowNode = new GraphNode("row", GraphNodeKind.Control, "surface", "row", "Cancel",
            [new GraphProperty("controlType", "ListItem"), new GraphProperty("effectivelyVisible", "True")], []);
        var editNode = new GraphNode("edit", GraphNodeKind.Control, "surface", "edit", "Zoom",
            [new GraphProperty("controlType", "Edit"), new GraphProperty("effectivelyVisible", "True")], []);
        var surface = new UiMapSurfaceView("surface", UiUnderstandingLevel.SemanticWorld, "Undo_Dropdown",
            "RawPopupWindow", "app", new RectI(223, 47, 133, 663), 2, [], [], surfaceNode);
        var row = new UiMapControlView("row", UiUnderstandingLevel.SemanticWorld,
            "Cancel", "ListItem", "surface", "list", new RectI(224, 679, 131, 30),
            [new EvidenceRef("session", 6, "frame-6.json", new RectI(224, 679, 131, 30))], rowNode);
        var edit = new UiMapControlView("edit", UiUnderstandingLevel.SemanticWorld,
            "Zoom", "Edit", "surface", "list", new RectI(225, 679, 129, 31),
            [new EvidenceRef("session", 6, "frame-6.json", new RectI(225, 679, 129, 31))], editNode);

        Assert.True(UiMapPresentation.IsRedundantPopupEditor(edit, surface, 6, "session", [row, edit]));
        Assert.False(UiMapPresentation.IsRedundantPopupEditor(row, surface, 6, "session", [row, edit]));
    }

    [Fact]
    public void AppMapDoesNotDrawAggregateRangeBoundaryOverItsThreeControls()
    {
        static EvidenceRef Evidence(RectI bounds) => new("session", 2, "frame-2.json", bounds);
        static GraphNode Node(string id, string kind, string className) => new(
            id, GraphNodeKind.Control, "surface", id, id,
            [
                new GraphProperty("controlType", kind),
                new GraphProperty("className", className),
                new GraphProperty("effectivelyVisible", "True")
            ], []);

        var pane = new UiMapControlView("pane", UiUnderstandingLevel.SemanticWorld, "Pane", "Pane",
            "surface", "", new RectI(100, 700, 600, 22), [Evidence(new(100, 700, 600, 22))], Node("pane", "Pane", "NetUIHWNDElement"));
        var scrollBar = new UiMapControlView("scroll", UiUnderstandingLevel.SemanticWorld, "Horizontal", "ScrollBar",
            "surface", "pane", new RectI(100, 700, 600, 22), [Evidence(new(100, 700, 600, 22))], Node("scroll", "ScrollBar", "NetUIScrollBar"));
        var left = new UiMapControlView("left", UiUnderstandingLevel.SemanticWorld, "Column left", "Button",
            "surface", "scroll", new RectI(100, 700, 22, 22), [Evidence(new(100, 700, 22, 22))], Node("left", "Button", "NetUIRepeatButton"));
        var thumb = new UiMapControlView("thumb", UiUnderstandingLevel.SemanticWorld, "Thumb", "Thumb",
            "surface", "scroll", new RectI(122, 700, 530, 22), [Evidence(new(122, 700, 530, 22))], Node("thumb", "Thumb", "NetUIThumb"));
        var pageRight = new UiMapControlView("page", UiUnderstandingLevel.SemanticWorld, "Page right", "Button",
            "surface", "scroll", new RectI(652, 700, 25, 22), [Evidence(new(652, 700, 25, 22))], Node("page", "Button", "NetUIRepeatButton"));
        var right = new UiMapControlView("right", UiUnderstandingLevel.SemanticWorld, "Column right", "Button",
            "surface", "scroll", new RectI(677, 700, 23, 22), [Evidence(new(677, 700, 23, 22))], Node("right", "Button", "NetUIRepeatButton"));
        UiMapControlView[] controls = [pane, scrollBar, left, thumb, pageRight, right];

        Assert.True(UiMapPresentation.IsRedundantCompositeBoundary(pane, 2, "session", controls));
        Assert.True(UiMapPresentation.IsRedundantCompositeBoundary(scrollBar, 2, "session", controls));
        Assert.All(new[] { left, thumb, pageRight, right }, control =>
            Assert.False(UiMapPresentation.IsRedundantCompositeBoundary(control, 2, "session", controls)));
    }

    [Fact]
    public void AppMapDrawsPageSetupTabsIndividuallyWithoutTheWideTabStripBoundary()
    {
        static EvidenceRef Evidence(RectI bounds) => new("session", 63, "frame-63.json", bounds);
        static GraphNode Node(string id, string kind) => new(
            id, GraphNodeKind.Control, "surface", id, id,
            [
                new GraphProperty("controlType", kind),
                new GraphProperty("className", kind == "Tab" ? "MSAA.Role60" : "MSAA.Role37"),
                new GraphProperty("effectivelyVisible", "True")
            ], []);

        var strip = new UiMapControlView("tabs", UiUnderstandingLevel.SemanticWorld, "Tab", "Tab",
            "surface", "", new RectI(593, 375, 621, 28), [Evidence(new(593, 375, 621, 28))], Node("tabs", "Tab"));
        var page = new UiMapControlView("page", UiUnderstandingLevel.SemanticWorld, "Page", "TabItem",
            "surface", "tabs", new RectI(593, 377, 81, 25), [Evidence(new(593, 377, 81, 25))], Node("page", "TabItem"));
        var margins = new UiMapControlView("margins", UiUnderstandingLevel.SemanticWorld, "Margins", "TabItem",
            "surface", "tabs", new RectI(674, 377, 81, 25), [Evidence(new(674, 377, 81, 25))], Node("margins", "TabItem"));
        var header = new UiMapControlView("header", UiUnderstandingLevel.SemanticWorld, "Header/Footer", "TabItem",
            "surface", "tabs", new RectI(755, 377, 113, 25), [Evidence(new(755, 377, 113, 25))], Node("header", "TabItem"));
        var sheet = new UiMapControlView("sheet", UiUnderstandingLevel.SemanticWorld, "Sheet", "TabItem",
            "surface", "tabs", new RectI(868, 377, 81, 25), [Evidence(new(868, 377, 81, 25))], Node("sheet", "TabItem"));
        UiMapControlView[] controls = [strip, page, margins, header, sheet];

        Assert.True(UiMapPresentation.IsRedundantCompositeBoundary(strip, 63, "session", controls));
        Assert.All(new[] { page, margins, header, sheet }, control =>
            Assert.False(UiMapPresentation.IsRedundantCompositeBoundary(control, 63, "session", controls)));
    }

    [Fact]
    public void SurfaceProjectionClipsAndTranslatesAbsoluteBounds()
    {
        var surface = new RectI(100, 200, 300, 150);

        Assert.Equal(new RectI(20, 30, 40, 50), UiMapPresentation.ProjectToSurface(new(120, 230, 40, 50), surface));
        Assert.Equal(new RectI(0, 0, 20, 20), UiMapPresentation.ProjectToSurface(new(80, 180, 40, 40), surface));
        Assert.Null(UiMapPresentation.ProjectToSurface(new(0, 0, 10, 10), surface));
    }

    [Fact]
    public void AppMapUsesBoundsFromTheDisplayedEvidenceFrame()
    {
        var node = new GraphNode("control", GraphNodeKind.Control, "surface", "control", "Save", [], []);
        var control = new UiMapControlView(
            "control",
            UiUnderstandingLevel.SemanticWorld,
            "Save",
            "Button",
            "surface",
            "",
            new RectI(10, 20, 30, 40),
            [
                new EvidenceRef("session", 1, "frame-1.json", new RectI(10, 20, 30, 40)),
                new EvidenceRef("session", 2, "frame-2.json", new RectI(110, 120, 50, 60))
            ],
            node);

        Assert.Equal(new RectI(110, 120, 50, 60),
            UiMapPresentation.ResolveControlBounds(control, 2, "session"));
        Assert.Equal(control.Bounds,
            UiMapPresentation.ResolveControlBounds(control, 3, "session"));
    }

    [Fact]
    public void AppMapPreservesObservedCaptionButtonBounds()
    {
        var titleBarNode = new GraphNode("title", GraphNodeKind.Control, "surface", "title",
            "Legacy application", [new GraphProperty("controlType", "TitleBar")], []);
        var closeNode = new GraphNode("close", GraphNodeKind.Control, "title", "close", "Close",
            [
                new GraphProperty("controlType", "Button"),
                new GraphProperty("automationId", "Close")
            ], []);
        var saveNode = new GraphNode("save", GraphNodeKind.Control, "surface", "save", "Save",
            [
                new GraphProperty("controlType", "Button"),
                new GraphProperty("automationId", "save")
            ], []);
        var titleBar = new UiMapControlView("title", UiUnderstandingLevel.RawWorld,
            "Legacy application", "TitleBar", "surface", "", new RectI(0, 0, 1200, 29),
            [new EvidenceRef("session", 2, "frame-2.json", new RectI(0, 0, 1200, 29))], titleBarNode);
        var close = new UiMapControlView("close", UiUnderstandingLevel.RawWorld,
            "Close", "Button", "surface", "title", new RectI(1130, 10, 70, 18),
            [new EvidenceRef("session", 2, "frame-2.json", new RectI(1130, 10, 70, 18))], closeNode);
        var save = new UiMapControlView("save", UiUnderstandingLevel.RawWorld,
            "Save", "Button", "surface", "", new RectI(100, 80, 90, 24),
            [new EvidenceRef("session", 2, "frame-2.json", new RectI(100, 80, 90, 24))], saveNode);

        Assert.Equal(new RectI(1130, 10, 70, 18),
            UiMapPresentation.ResolveControlBounds(close, 2, "session", [titleBar, close, save]));
        Assert.Equal(new RectI(100, 80, 90, 24),
            UiMapPresentation.ResolveControlBounds(save, 2, "session", [titleBar, close, save]));
    }

    [Fact]
    public void AppMapAlignsLegacyOfficeSystemMenuToThePaintedApplicationIcon()
    {
        var captionNode = new GraphNode("caption", GraphNodeKind.Control, "surface", "caption",
            "Book1 - Excel",
            [
                new GraphProperty("controlType", "TitleBar"),
                new GraphProperty("className", "NetUIOfficeCaption")
            ], []);
        var systemNode = new GraphNode("system", GraphNodeKind.Control, "menu", "system", "System",
            [
                new GraphProperty("controlType", "MenuItem"),
                new GraphProperty("automationId", "Item 1")
            ], []);
        var caption = new UiMapControlView("caption", UiUnderstandingLevel.RawWorld,
            "Book1 - Excel", "TitleBar", "surface", "", new RectI(53, 0, 934, 60),
            [new EvidenceRef("session", 106, "frame-106.json", new RectI(53, 0, 934, 60))], captionNode);
        var system = new UiMapControlView("system", UiUnderstandingLevel.RawWorld,
            "System", "MenuItem", "surface", "menu", new RectI(0, 0, 28, 28),
            [new EvidenceRef("session", 106, "frame-106.json", new RectI(0, 0, 28, 28))], systemNode);

        Assert.Equal(new RectI(20, 20, 20, 20),
            UiMapPresentation.ResolveControlBounds(system, 106, "session", [caption, system]));
    }

    [Fact]
    public void AppMapPreservesAnAlreadyAlignedOfficeSystemMenu()
    {
        var captionNode = new GraphNode("caption", GraphNodeKind.Control, "surface", "caption",
            "Book1 - Excel",
            [
                new GraphProperty("controlType", "TitleBar"),
                new GraphProperty("className", "NetUIOfficeCaption")
            ], []);
        var systemNode = new GraphNode("system", GraphNodeKind.Control, "menu", "system", "System",
            [
                new GraphProperty("controlType", "MenuItem"),
                new GraphProperty("automationId", "Item 1")
            ], []);
        var caption = new UiMapControlView("caption", UiUnderstandingLevel.RawWorld,
            "Book1 - Excel", "TitleBar", "surface", "", new RectI(53, 0, 934, 60), [], captionNode);
        var system = new UiMapControlView("system", UiUnderstandingLevel.RawWorld,
            "System", "MenuItem", "surface", "menu", new RectI(20, 20, 20, 20), [], systemNode);

        Assert.Equal(system.Bounds,
            UiMapPresentation.ResolveControlBounds(system, null, null, [caption, system]));
    }

    [Fact]
    public void AppMapHidesTheSmallerDuplicateCaptionProvider()
    {
        var preferredNode = new GraphNode("preferred", GraphNodeKind.Control, "surface", "preferred", "Close",
            [
                new GraphProperty("controlType", "Button"),
                new GraphProperty("className", "NetUIAppFrameHelper"),
                new GraphProperty("frameworkId", "Win32"),
                new GraphProperty("offscreen", "False")
            ], []);
        var duplicateNode = new GraphNode("duplicate", GraphNodeKind.Control, "surface", "duplicate", "Close",
            [
                new GraphProperty("controlType", "Button"),
                new GraphProperty("automationId", "Close"),
                new GraphProperty("frameworkId", "Win32"),
                new GraphProperty("offscreen", "False")
            ], []);
        var preferred = new UiMapControlView("preferred", UiUnderstandingLevel.SemanticWorld,
            "Close", "Button", "surface", "", new RectI(1860, 0, 60, 60),
            [new EvidenceRef("session", 2, "frame-2.json", new RectI(1860, 0, 60, 60))], preferredNode);
        var duplicate = new UiMapControlView("duplicate", UiUnderstandingLevel.SemanticWorld,
            "Close", "Button", "surface", "", new RectI(1891, -1, 30, 30),
            [new EvidenceRef("session", 2, "frame-2.json", new RectI(1891, -1, 30, 30))], duplicateNode);

        Assert.False(UiMapPresentation.IsRedundantCaptionButton(
            preferred, 2, "session", [preferred, duplicate]));
        Assert.True(UiMapPresentation.IsRedundantCaptionButton(
            duplicate, 2, "session", [preferred, duplicate]));
    }

    [Fact]
    public void ControlSelectionSwitchesToAFrameThatContainsItsEvidence()
    {
        var bounds = new RectI(40, 50, 80, 24);
        var node = new GraphNode("control", GraphNodeKind.Control, "surface", "control", "Save", [], []);
        var evidence = new EvidenceRef("session", 7, "frame-7.json", bounds, "frame-7.png");
        var control = new UiMapControlView(
            "control", UiUnderstandingLevel.SemanticWorld, "Save", "Button", "surface", "",
            bounds, [evidence], node);
        var unrelated = new UiMapVariantView("frame-6", "Frame 6", "session", 6, 2,
            new EvidenceRef("session", 6, "frame-6.json", new RectI(0, 0, 800, 600), "frame-6.png"), []);
        var matching = new UiMapVariantView("frame-7", "Frame 7", "session", 7, 3,
            new EvidenceRef("session", 7, "frame-7.json", new RectI(0, 0, 800, 600), "frame-7.png"), ["control"]);

        Assert.Equal(matching, UiMapPresentation.ResolveControlVariant(control, [unrelated, matching], unrelated));
        Assert.Equal(matching, UiMapPresentation.ResolveControlVariant(control, [unrelated, matching], matching));
    }

    [Fact]
    public void HierarchyProjectionContainsOnlySurfaceInstances()
    {
        using var temp = new TempDirectory();
        var model = new UiMappingReadModel(new RecordingGraphBuilder().Build(SyntheticBundleFactory.Create(temp.Path)));

        var groups = model.BuildHierarchy(UiUnderstandingLevel.SemanticWorld);

        Assert.Equal(3, groups.Count);
        Assert.All(groups, group => Assert.All(group.Surfaces, surface =>
            Assert.Contains(surface.Source.Kind, new[] { GraphNodeKind.Window, GraphNodeKind.Surface })));
    }
}
