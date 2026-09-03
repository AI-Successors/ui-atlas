using UiAtlas.Core.Build;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Reader;
using UiAtlas.Core.Recording;
using UiAtlas.Core.Storage;
using Microsoft.Data.Sqlite;

namespace UiAtlas.Core.Tests;

public sealed class GraphPipelineTests
{
    [Fact]
    public void AdaptiveExtractionEvidenceIsProjectedIntoControlAndSurfaceNodes()
    {
        var now = new DateTimeOffset(2026, 8, 25, 0, 0, 0, TimeSpan.Zero);
        var target = new TargetScope(1, 1, 7, "Synthetic", now.AddHours(-1));
        var manifest = new RecordingManifest(FormatVersions.RecordingBundle, FormatVersions.Tool, "adaptive",
            now, now.AddSeconds(1), RecordingOutcome.Complete, target, new(), new(), true, 0, 1);
        var window = new WindowObservation(1, 1, 7, "Root", "App", new(0, 0, 800, 600), true, true, false, false, 96);
        var control = new AutomationObservation("1.1", "", "save", "Save", "ControlType.Button", "Button",
            new(10, 10, 80, 24), true, false, "Synthetic", 1, ["Invoke"]);
        var evidence = new ControlEvidenceObservation("evidence-1", ControlEvidenceSource.UiaRaw,
            "surface-1", control, .96);
        var candidate = new MergedControlCandidate("candidate-1", "surface-1", control, [evidence.EvidenceId],
            [ControlEvidenceSource.UiaRaw], .96, ExtractionCoverageStatus.Observed);
        var extraction = new AdaptiveExtractionSnapshot("adaptive-extraction/1",
            [new(ControlEvidenceSource.UiaRaw, "surface-1", [evidence], "ok", 4)], [candidate], [],
            ExtractionCoverageStatus.Observed, "coverage-complete", 4, 0);
        var frame = new FrameObservation(1, now, "", window, [control], false, "ok", "quick-map:manual",
            [window], Extraction: extraction);

        var graph = new RecordingGraphBuilder().Build(new RecordingGraphInput(manifest, [frame], []));

        var graphControl = Assert.Single(graph.Nodes, node => node.Kind == GraphNodeKind.Control &&
            node.Properties.Any(property => property.Name == "layer" && property.Value == "raw-world") &&
            node.Properties.Any(property => property.Name == "automationId" && property.Value == "save"));
        Assert.Contains(graphControl.Properties, property => property.Name == "evidenceSource" && property.Value == "UiaRaw");
        Assert.Contains(graphControl.Properties, property => property.Name == "coverageStatus" && property.Value == "Observed");
        Assert.Contains(graph.Nodes, node => node.Kind == GraphNodeKind.Surface &&
            node.Properties.Any(property => property.Name == "extractionCoverageStatus" && property.Value == "Observed"));
    }

    [Fact]
    public void OffscreenProviderEvidenceStaysRawButIsNotPromotedToTheUiWorlds()
    {
        using var temp = new TempDirectory();
        var graph = new RecordingGraphBuilder().Build(SyntheticBundleFactory.Create(temp.Path));

        Assert.Contains(graph.Nodes, node =>
            node.Kind == GraphNodeKind.Control &&
            node.Properties.Any(property => property.Name == "layer" && property.Value == "raw-data-streams") &&
            node.Properties.Any(property => property.Name == "offscreen" && property.Value == "True"));
        Assert.Contains(graph.Nodes, node =>
            node.Kind == GraphNodeKind.Control &&
            node.Properties.Any(property => property.Name == "layer" && property.Value == "raw-data-streams") &&
            node.Properties.Any(property => property.Name == "effectivelyVisible" && property.Value == "False"));
        Assert.DoesNotContain(graph.Nodes, node =>
            node.Kind == GraphNodeKind.Control &&
            node.Properties.Any(property => property.Name == "layer" && property.Value is "raw-world" or "semantic-world") &&
            node.Evidence.Any(evidence => evidence.FrameSequence == 1 && evidence.Bounds == new RectI(110, 10, 80, 24)));
    }

    [Fact]
    public void TimedOutAdaptiveFrameStillPromotesIndependentVisualEvidence()
    {
        using var temp = new TempDirectory();
        var graph = new RecordingGraphBuilder().Build(SyntheticBundleFactory.Create(
            temp.Path,
            firstTrigger: "adaptive-root-change",
            firstAutomationTimedOut: true,
            firstAutomationStatus: "timeout",
            visualFallback: true));

        var visual = Assert.Single(graph.Nodes, node =>
            node.Kind == GraphNodeKind.Control &&
            node.Properties.Any(property => property.Name == "layer" && property.Value == "raw-world") &&
            node.Properties.Any(property => property.Name == "className" && property.Value == "UiAtlas.VisualControlRegion"));
        Assert.Contains(visual.Evidence, evidence => evidence.FrameSequence == 1);

        Assert.DoesNotContain(graph.Nodes, node =>
            node.Kind == GraphNodeKind.Control &&
            node.Properties.Any(property => property.Name == "layer" && property.Value == "raw-world") &&
            node.Properties.Any(property => property.Name == "automationId" && property.Value == "root") &&
            node.Evidence.Any(evidence => evidence.FrameSequence == 1));
    }

    [Fact]
    public void QuickMapPromotesHiddenControlsAsUnverifiedCandidates()
    {
        using var temp = new TempDirectory();
        var bundle = SyntheticBundleFactory.Create(
            temp.Path,
            firstTrigger: "quick-map:standalone",
            firstAutomationTimedOut: true,
            firstAutomationStatus: "partial",
            representativeFrames: [1],
            outcome: RecordingOutcome.Partial);

        var graph = new RecordingGraphBuilder().Build(bundle);

        var semanticControls = graph.Nodes.Where(node =>
            node.Kind == GraphNodeKind.Control &&
            node.Properties.Any(property => property.Name == "layer" && property.Value == "semantic-world")).ToArray();
        Assert.True(semanticControls.Any(node =>
            node.Kind == GraphNodeKind.Control &&
            node.Evidence.Any(evidence => evidence.FrameSequence == 1 && evidence.Bounds == new RectI(110, 10, 80, 24)) &&
            node.Properties.Any(property => property.Name == "verificationStatus" && property.Value == "Unverified")),
            string.Join(Environment.NewLine, semanticControls.Select(node =>
                $"{node.Label}: {string.Join(',', node.Properties.Where(property => property.Name is "verificationStatus" or "offscreen").Select(property => property.Name + "=" + property.Value))}")));
        Assert.Contains(graph.Nodes, node =>
            node.Kind == GraphNodeKind.Control &&
            node.Properties.Any(property => property.Name == "layer" && property.Value == "semantic-world") &&
            node.Label == "Save" &&
            node.Properties.Any(property => property.Name == "verificationStatus" && property.Value == "Observed"));
    }

    [Fact]
    public void QuickMapPromotesControlsFromVisibleFormOwnedByHiddenApplicationWindow()
    {
        var now = new DateTimeOffset(2026, 8, 26, 0, 0, 0, TimeSpan.Zero);
        var target = new TargetScope(2, 1, 7, "LegacyApp", now.AddHours(-1));
        var manifest = new RecordingManifest(FormatVersions.RecordingBundle, FormatVersions.Tool, "legacy-owned-form",
            now, now.AddSeconds(1), RecordingOutcome.Partial, target, new(), new(), true, 0, 1);
        var hiddenRoot = new WindowObservation(1, 1, 7, "TApplication", "Legacy application",
            new(400, 300, 0, 0), true, true, false, false, 96);
        var visibleForm = new WindowObservation(2, 1, 7, "TfrmMain", "Legacy application",
            new(0, 0, 1200, 800), true, true, false, false, 96, OwnerHwnd: 1);
        var formControl = new AutomationObservation("2.1", "", "", "Legacy application",
            "ControlType.Window", "TfrmMain", visibleForm.Bounds, true, false,
            "Win32", WindowHwnd: 2);
        var button = new AutomationObservation("2.10", "2.1", "", "Reservations...",
            "ControlType.Pane", "TAbacreButton", new(8, 108, 100, 56), true, false,
            "Win32", WindowHwnd: 2);
        var frame = new FrameObservation(1, now, "", hiddenRoot, [formControl, button], false, "partial",
            "quick-map:auto-tabs-initial-surface", [hiddenRoot, visibleForm],
            ObservationScope: "control-delta", ObservedWindowHwnds: [1]);

        var graph = new RecordingGraphBuilder().Build(new RecordingGraphInput(manifest, [frame], []));

        var promoted = Assert.Single(graph.Nodes, node =>
            node.Kind == GraphNodeKind.Control &&
            node.Label == "Reservations..." &&
            node.Properties.Any(property => property.Name == "layer" && property.Value == "raw-world"));
        Assert.Contains(promoted.Properties, property =>
            property.Name == "controlType" && property.Value == "Button");
    }

    [Fact]
    public void RebuildSuppressesRecordedOcrControlInsideNativeLegacyButtonAcrossFrames()
    {
        var now = new DateTimeOffset(2026, 8, 28, 0, 0, 0, TimeSpan.Zero);
        var target = new TargetScope(2, 2, 7, "LegacyApp", now.AddHours(-1));
        var manifest = new RecordingManifest(FormatVersions.RecordingBundle, FormatVersions.Tool, "legacy-ocr-duplicate",
            now, now.AddSeconds(2), RecordingOutcome.Partial, target, new(), new(), true, 0, 2);
        var window = new WindowObservation(2, 2, 7, "TfrmMain", "Legacy application",
            new(0, 0, 1200, 800), true, true, false, false, 96);
        var nativeButton = new AutomationObservation("2.10", "", "198270", "Rooms Calendar",
            "ControlType.Pane", "TAbacreButton", new(218, 108, 100, 56), true, false,
            "Win32", WindowHwnd: 2);
        var falseOcrButton = new AutomationObservation(
            "visual:v3:585f6c281222de3bdf8fd59d", "", "visual:v3:585f6c281222de3bdf8fd59d", "Rooms",
            "ControlType.Button", "UiAtlas.VisualControlRegion", new(259, 126, 27, 16),
            false, true, "UiAtlas.Visual.Ocr", 2, ["Invoke"], VisualRole: "button", OcrText: "Rooms");
        var nativeFrame = new FrameObservation(1, now, "", window, [nativeButton, falseOcrButton],
            false, "ok", "quick-map:initial", [window]);
        var visualOnlyFrame = new FrameObservation(2, now.AddSeconds(1), "", window, [falseOcrButton],
            false, "visual-only", "adaptive-root-change", [window]);

        var graph = new RecordingGraphBuilder().Build(
            new RecordingGraphInput(manifest, [nativeFrame, visualOnlyFrame], []));

        Assert.Contains(graph.Nodes, node =>
            node.Kind == GraphNodeKind.Control && node.Label == "Rooms Calendar" &&
            node.Properties.Any(property => property is { Name: "className", Value: "TAbacreButton" }));
        Assert.DoesNotContain(graph.Nodes, node =>
            node.Kind == GraphNodeKind.Control &&
            node.Properties.Any(property => property is
                { Name: "className", Value: "UiAtlas.VisualControlRegion" }));
        Assert.True(GraphValidator.Validate(graph).IsValid);
    }

    [Fact]
    public void RebuildKeepsVisualButtonsInsideOpaqueOfficeGallery()
    {
        var now = new DateTimeOffset(2026, 8, 31, 0, 0, 0, TimeSpan.Zero);
        var target = new TargetScope(2, 2, 7, "EXCEL", now.AddHours(-1));
        var manifest = new RecordingManifest(FormatVersions.RecordingBundle, FormatVersions.Tool, "excel-gallery",
            now, now.AddSeconds(1), RecordingOutcome.Complete, target, new(), new(), true, 0, 1);
        var window = new WindowObservation(2, 2, 7, "XLMAIN", "Book1 - Excel",
            new(0, 0, 1_920, 1_040), true, true, false, false, 96);
        var gallery = new AutomationObservation(
            "gallery", "ribbon", "OfficeScriptsGallery", "", "ControlType.MenuItem", "NetUIAnchor",
            new RectI(169, 112, 659, 78), true, false, "Win32", 2,
            ["ExpandCollapsePatternIdentifiers.Pattern"]);
        var script = new AutomationObservation(
            "visual:v3:1234567890abcdef12345678", gallery.RuntimeId,
            "visual:v3:1234567890abcdef12345678", "Freeze Selection",
            "ControlType.Button", "UiAtlas.VisualControlRegion", new RectI(390, 118, 180, 30),
            false, true, "UiAtlas.Visual.Ocr", 2, ["Invoke"], VisualRole: "button",
            OcrText: "Freeze Selection", VisualGroupId: gallery.RuntimeId);
        var frame = new FrameObservation(1, now, "", window, [gallery, script], false, "partial",
            "adaptive-root-change", [window]);

        var graph = new RecordingGraphBuilder().Build(new RecordingGraphInput(manifest, [frame], []));

        Assert.Contains(graph.Nodes, node =>
            node.Kind == GraphNodeKind.Control && node.Label == "Freeze Selection" &&
            node.Properties.Any(property => property is
                { Name: "className", Value: "UiAtlas.VisualControlRegion" }));
        Assert.True(GraphValidator.Validate(graph).IsValid);
    }

    [Fact]
    public void VisualOnlyFrameKeepsStableNativeToolbarAndMenuFromTheSameWindow()
    {
        var now = new DateTimeOffset(2026, 8, 29, 0, 0, 0, TimeSpan.Zero);
        var target = new TargetScope(2, 2, 7, "LegacyApp", now.AddHours(-1));
        var manifest = new RecordingManifest(FormatVersions.RecordingBundle, FormatVersions.Tool, "stable-chrome",
            now, now.AddSeconds(2), RecordingOutcome.Partial, target, new(), new(), true, 0, 2);
        var window = new WindowObservation(2, 2, 7, "TfrmMain", "Legacy application",
            new(0, 0, 1200, 800), true, true, false, false, 96);
        var root = new AutomationObservation("2.1", "", "", "Legacy application",
            "ControlType.Window", "TfrmMain", window.Bounds, true, false, "Win32", 2);
        var menuBar = new AutomationObservation("2.2", "2.1", "MenuBar", "Application",
            "ControlType.MenuBar", "", new(0, 29, 1200, 24), true, false, "Win32", 2);
        var menuItem = new AutomationObservation("2.3", "2.2", "Item 1", "",
            "ControlType.MenuItem", "", new(0, 29, 61, 24), true, false, "Win32", 2);
        var toolbar = new AutomationObservation("2.4", "2.1", "toolbar", "",
            "ControlType.Pane", "TAbacrePanel", new(3, 108, 1000, 58), true, false, "Win32", 2);
        var nativeButton = new AutomationObservation("2.5", "2.4", "reservations", "Reservations...",
            "ControlType.Pane", "TAbacreButton", new(8, 108, 100, 56), true, false, "Win32", 2);
        var pageOnlyEdit = new AutomationObservation("2.6", "2.1", "search", "Search",
            "ControlType.Edit", "TEdit", new(400, 300, 160, 24), true, false, "Win32", 2);
        var visualDuplicate = new AutomationObservation(
            "visual:v3:0123456789abcdef01234567", "", "visual:v3:0123456789abcdef01234567", "Reservations...",
            "ControlType.Button", "UiAtlas.VisualControlRegion", new(8, 108, 100, 56),
            false, true, "UiAtlas.Visual.Ocr", 2, ["Invoke"], VisualRole: "button", OcrText: "Reservations...");
        var nativeFrame = new FrameObservation(1, now, "", window,
            [root, menuBar, menuItem, toolbar, nativeButton, pageOnlyEdit], false, "ok", "adaptive-root-change", [window]);
        var visualOnlyFrame = new FrameObservation(2, now.AddSeconds(1), "", window,
            [visualDuplicate], false, "visual-only", "adaptive-root-change", [window]);

        var graph = new RecordingGraphBuilder().Build(
            new RecordingGraphInput(manifest, [nativeFrame, visualOnlyFrame], []));

        var button = Assert.Single(graph.Nodes, node =>
            node.Kind == GraphNodeKind.Control && node.Label == "Reservations..." &&
            node.Properties.Any(property => property is { Name: "layer", Value: "raw-world" }));
        Assert.Equal([1L, 2L], button.Evidence.Select(evidence => evidence.FrameSequence).Distinct().Order().ToArray());
        var item = Assert.Single(graph.Nodes, node =>
            node.Kind == GraphNodeKind.Control && node.Label == "Item 1" &&
            node.Properties.Any(property => property is { Name: "controlType", Value: "MenuItem" }) &&
            node.Properties.Any(property => property is { Name: "layer", Value: "raw-world" }));
        Assert.Equal([1L, 2L], item.Evidence.Select(evidence => evidence.FrameSequence).Distinct().Order().ToArray());
        var edit = Assert.Single(graph.Nodes, node =>
            node.Kind == GraphNodeKind.Control && node.Label == "Search" &&
            node.Properties.Any(property => property is { Name: "layer", Value: "raw-world" }));
        Assert.Equal([1L], edit.Evidence.Select(evidence => evidence.FrameSequence).Distinct().ToArray());
        Assert.DoesNotContain(graph.Nodes, node =>
            node.Kind == GraphNodeKind.Control &&
            node.Properties.Any(property => property is { Name: "className", Value: "UiAtlas.VisualControlRegion" }));
        Assert.True(GraphValidator.Validate(graph).IsValid);
    }

    [Fact]
    public void SuccessfulControlDeltaStillKeepsAllStableToolbarButtons()
    {
        var now = new DateTimeOffset(2026, 8, 30, 0, 0, 0, TimeSpan.Zero);
        var target = new TargetScope(2, 2, 7, "LegacyApp", now.AddHours(-1));
        var manifest = new RecordingManifest(FormatVersions.RecordingBundle, FormatVersions.Tool, "control-delta-chrome",
            now, now.AddSeconds(2), RecordingOutcome.Partial, target, new(), new(), true, 0, 2);
        var window = new WindowObservation(2, 2, 7, "TfrmMain", "Legacy application",
            new(0, 0, 1200, 800), true, true, false, false, 96);
        var root = new AutomationObservation("2.1", "", "", "Legacy application",
            "ControlType.Window", "TfrmMain", window.Bounds, true, false, "Win32", 2);
        var toolbar = new AutomationObservation("2.2", "2.1", "toolbar", "",
            "ControlType.Pane", "TAbacrePanel", new(3, 108, 1000, 58), true, false, "Win32", 2);
        var reservations = new AutomationObservation("2.3", "2.2", "reservations", "Reservations...",
            "ControlType.Pane", "TAbacreButton", new(8, 108, 100, 56), true, false, "Win32", 2);
        var stays = new AutomationObservation("2.4", "2.2", "stays", "Stays...",
            "ControlType.Pane", "TAbacreButton", new(113, 108, 100, 56), true, false, "Win32", 2);
        var pageField = new AutomationObservation("2.5", "2.1", "search", "Search",
            "ControlType.Edit", "TEdit", new(400, 300, 160, 24), true, false, "Win32", 2);
        var observedCanvasTarget = new AutomationObservation(
            "ui-atlas:pointer:700:500", "", "", "Observed canvas target",
            "CanvasItem", "UiAtlas.ObservedCanvasTarget", new(691, 491, 18, 18),
            true, false, "UiAtlas.Pointer", 2, ["SelectionItem"]);
        var baseline = new FrameObservation(1, now, "", window,
            [root, toolbar, reservations, stays, pageField], false, "ok", "adaptive-root-change", [window]);
        var clickDelta = new FrameObservation(2, now.AddSeconds(1), "", window,
            [root, toolbar, stays, observedCanvasTarget], false, "ok", "adaptive-control", [window],
            ObservationScope: "control-delta", ObservedWindowHwnds: [window.Hwnd], BaseFrameSequence: 1);

        var graph = new RecordingGraphBuilder().Build(
            new RecordingGraphInput(manifest, [baseline, clickDelta], []));

        var carried = Assert.Single(graph.Nodes, node =>
            node.Kind == GraphNodeKind.Control && node.Label == "Reservations..." &&
            node.Properties.Any(property => property is { Name: "layer", Value: "raw-world" }));
        Assert.Contains(carried.Evidence, evidence => evidence.FrameSequence == 2);
        var clicked = Assert.Single(graph.Nodes, node =>
            node.Kind == GraphNodeKind.Control && node.Label == "Stays..." &&
            node.Properties.Any(property => property is { Name: "layer", Value: "raw-world" }));
        Assert.Contains(clicked.Evidence, evidence => evidence.FrameSequence == 2);
        var field = Assert.Single(graph.Nodes, node =>
            node.Kind == GraphNodeKind.Control && node.Label == "Search" &&
            node.Properties.Any(property => property is { Name: "layer", Value: "raw-world" }));
        Assert.DoesNotContain(field.Evidence, evidence => evidence.FrameSequence == 2);

        var model = new UiMappingReadModel(graph);
        var semanticWindow = Assert.Single(model.LayerFor(UiUnderstandingLevel.SemanticWorld).Surfaces,
            surface => surface.SurfaceKind == "SemanticWindow");
        Assert.Equal([1L], model.VariantsFor([semanticWindow]).Select(variant => variant.FrameSequence));
        Assert.DoesNotContain(semanticWindow.Variants, variant => variant.FrameSequence == 2);
        Assert.True(GraphValidator.Validate(graph).IsValid);
    }

    [Fact]
    public void QuickMapPairIsOneScreenAndAdaptiveControlDeltasNeverBecomeVariants()
    {
        var now = new DateTimeOffset(2026, 8, 30, 0, 0, 0, TimeSpan.Zero);
        var target = new TargetScope(2, 2, 7, "LegacyApp", now.AddHours(-1));
        var manifest = new RecordingManifest(FormatVersions.RecordingBundle, FormatVersions.Tool, "screen-delta-filter",
            now, now.AddSeconds(3), RecordingOutcome.Partial, target, new(), new(), true, 0, 3);
        var window = new WindowObservation(2, 2, 7, "TfrmMain", "Legacy application",
            new(0, 0, 1200, 800), true, true, false, false, 96);
        var root = new AutomationObservation("root", "", "", "Legacy application",
            "ControlType.Window", "TfrmMain", window.Bounds, true, false, "Win32", 2);
        var button = new AutomationObservation("button", "root", "new", "New Order",
            "ControlType.Pane", "TAbacreButton", new(200, 90, 100, 56), true, false, "Win32", 2);
        var pointer = new AutomationObservation("ui-atlas:pointer:400:300", "", "", "Observed canvas target",
            "CanvasItem", "UiAtlas.ObservedCanvasTarget", new(391, 291, 18, 18), true, false,
            "UiAtlas.Pointer", 2, ["SelectionItem"]);
        var screenshot = new FrameObservation(1, now, "", window, [], false,
            "not-requested", "quick-map-screen:manual-initial-surface", [window],
            ObservationScope: "full-root");
        var initialControls = new FrameObservation(2, now.AddSeconds(1), "", window, [root, button], false,
            "partial", "quick-map:manual-initial-surface", [window],
            ObservationScope: "control-delta", BaseFrameSequence: 1);
        var clickDelta = new FrameObservation(3, now.AddSeconds(2), "", window, [root, pointer], false,
            "ok", "adaptive-control", [window], ObservationScope: "control-delta", BaseFrameSequence: 2);

        var graph = new RecordingGraphBuilder().Build(
            new RecordingGraphInput(manifest, [screenshot, initialControls, clickDelta], []));
        var model = new UiMappingReadModel(graph);
        var semanticWindow = Assert.Single(model.LayerFor(UiUnderstandingLevel.SemanticWorld).Surfaces,
            surface => surface.SurfaceKind == "SemanticWindow");

        Assert.Equal([2L], model.VariantsFor([semanticWindow]).Select(variant => variant.FrameSequence));
        Assert.True(GraphValidator.Validate(graph).IsValid);
    }

    [Fact]
    public void DuplicateNativeCaptionButtonsPreferTheReliableProviderBounds()
    {
        var now = new DateTimeOffset(2026, 8, 29, 0, 0, 0, TimeSpan.Zero);
        var target = new TargetScope(2, 2, 7, "LegacyApp", now.AddHours(-1));
        var manifest = new RecordingManifest(FormatVersions.RecordingBundle, FormatVersions.Tool, "caption-bounds",
            now, now.AddSeconds(1), RecordingOutcome.Complete, target, new(), new(), true, 0, 1);
        var window = new WindowObservation(2, 2, 7, "TfrmMain", "Legacy application",
            new(0, 0, 1200, 800), true, true, false, false, 96);
        var root = new AutomationObservation("2.1", "", "", "Legacy application",
            "ControlType.Window", "TfrmMain", window.Bounds, true, false, "Win32", 2);
        var titleBar = new AutomationObservation("2.2", "2.1", "TitleBar", "Legacy application",
            "ControlType.TitleBar", "", new(0, 0, 1200, 29), true, false, "Win32", 2);
        var minimize = new AutomationObservation("2.3", "2.2", "", "Minimize",
            "ControlType.Button", "NetUIAppFrameHelper", new(1010, 0, 60, 29), true, false, "Win32", 2);
        var close = new AutomationObservation("2.4", "2.2", "", "Close",
            "ControlType.Button", "NetUIAppFrameHelper", new(1130, 0, 70, 29), true, false, "Win32", 2);
        var duplicateClose = new AutomationObservation("2.5", "2.2", "Close", "Close",
            "ControlType.Button", "", new(1170, 8, 30, 18), true, false, "Win32", 2);
        var ordinary = new AutomationObservation("2.6", "2.1", "save", "Save",
            "ControlType.Button", "Button", new(100, 80, 90, 24), true, false, "Win32", 2);
        var frame = new FrameObservation(1, now, "", window, [root, titleBar, minimize, close, duplicateClose, ordinary],
            false, "ok", "adaptive-root-change", [window]);

        var graph = new RecordingGraphBuilder().Build(new RecordingGraphInput(manifest, [frame], []));
        var controls = graph.Nodes.Where(node =>
            node.Kind == GraphNodeKind.Control &&
            node.Properties.Any(property => property is { Name: "layer", Value: "raw-world" })).ToArray();

        Assert.Equal(new RectI(1010, 0, 60, 29),
            Assert.Single(controls, node => node.Label == "Minimize").Evidence.Single().Bounds);
        Assert.Equal(new RectI(1130, 0, 70, 29),
            Assert.Single(controls, node => node.Label == "Close").Evidence.Single().Bounds);
        Assert.Equal(new RectI(100, 80, 90, 24),
            Assert.Single(controls, node => node.Label == "Save").Evidence.Single().Bounds);
    }

    [Fact]
    public void RebuildSuppressesGenericOcrCandidateInsideVisualTableButKeepsCells()
    {
        var now = new DateTimeOffset(2026, 8, 28, 0, 0, 0, TimeSpan.Zero);
        var target = new TargetScope(2, 2, 7, "LegacyApp", now.AddHours(-1));
        var manifest = new RecordingManifest(FormatVersions.RecordingBundle, FormatVersions.Tool, "legacy-table-duplicate",
            now, now.AddSeconds(1), RecordingOutcome.Partial, target, new(), new(), true, 0, 1);
        var window = new WindowObservation(2, 2, 7, "TfrmMain", "Legacy application",
            new(0, 0, 1200, 800), true, true, false, false, 96);
        var table = new AutomationObservation(
            "visual:v3:111111111111111111111111", "", "visual:v3:111111111111111111111111", "Visual table",
            "ControlType.Table", "UiAtlas.VisualControlRegion", new(18, 357, 1000, 336),
            false, true, "UiAtlas.Visual.Ocr", 2, VisualRole: "table");
        var cell = new AutomationObservation(
            "visual:v3:222222222222222222222222", table.RuntimeId, "visual:v3:222222222222222222222222", "Cell 8,1",
            "ControlType.DataItem", "UiAtlas.VisualControlRegion", new(19, 631, 56, 31),
            false, true, "UiAtlas.Visual.Ocr", 2, VisualRole: "table-cell", TableRow: 7, TableColumn: 0);
        var falseOcrButton = new AutomationObservation(
            "visual:v3:333333333333333333333333", "", "visual:v3:333333333333333333333333", "203 204",
            "ControlType.Button", "UiAtlas.VisualControlRegion", new(32, 617, 28, 65),
            false, true, "UiAtlas.Visual.Ocr", 2, ["Invoke"], VisualRole: "button", OcrText: "203 204");
        var frame = new FrameObservation(1, now, "", window, [table, cell, falseOcrButton],
            false, "visual-only", "adaptive-root-change", [window]);

        var graph = new RecordingGraphBuilder().Build(new RecordingGraphInput(manifest, [frame], []));

        Assert.Contains(graph.Nodes, node => node.Kind == GraphNodeKind.Control && node.Label == "Visual table");
        Assert.Contains(graph.Nodes, node => node.Kind == GraphNodeKind.Control && node.Label == "Cell 8,1");
        Assert.DoesNotContain(graph.Nodes, node => node.Kind == GraphNodeKind.Control && node.Label == "203 204");
        Assert.True(GraphValidator.Validate(graph).IsValid);
    }

    [Fact]
    public void RebuildDoesNotSuppressVisualButtonBecauseUnrelatedNativeControlUsedSameBoundsOnAnotherFrame()
    {
        var now = new DateTimeOffset(2026, 8, 28, 0, 0, 0, TimeSpan.Zero);
        var target = new TargetScope(2, 2, 7, "LegacyApp", now.AddHours(-1));
        var manifest = new RecordingManifest(FormatVersions.RecordingBundle, FormatVersions.Tool, "cross-state-bounds",
            now, now.AddSeconds(2), RecordingOutcome.Partial, target, new(), new(), true, 0, 2);
        var window = new WindowObservation(2, 2, 7, "TfrmMain", "Legacy application",
            new(0, 0, 1200, 800), true, true, false, false, 96);
        var oldScreenField = new AutomationObservation("2.10", "", "search", "Search",
            "ControlType.Edit", "TEdit", new(500, 230, 100, 50), true, false,
            "Win32", WindowHwnd: 2);
        var nextMonth = new AutomationObservation(
            "visual:v3:444444444444444444444444", "", "visual:v3:444444444444444444444444", "Next Month",
            "ControlType.Button", "UiAtlas.VisualControlRegion", new(500, 230, 100, 50),
            false, true, "UiAtlas.Visual.Ocr", 2, ["Invoke"], VisualRole: "button", OcrText: "Next Month");
        var first = new FrameObservation(1, now, "", window, [oldScreenField],
            false, "ok", "quick-map:initial", [window]);
        var second = new FrameObservation(2, now.AddSeconds(1), "", window, [nextMonth],
            false, "visual-only", "adaptive-root-change", [window]);

        var graph = new RecordingGraphBuilder().Build(new RecordingGraphInput(manifest, [first, second], []));

        Assert.Contains(graph.Nodes, node =>
            node.Kind == GraphNodeKind.Control && node.Label == "Next Month" &&
            node.Properties.Any(property => property is
                { Name: "className", Value: "UiAtlas.VisualControlRegion" }));
        Assert.True(GraphValidator.Validate(graph).IsValid);
    }

    [Fact]
    public void NativeHeaderItemSuppressesMatchingVisualGeometryHeader()
    {
        var now = new DateTimeOffset(2026, 8, 29, 0, 0, 0, TimeSpan.Zero);
        var target = new TargetScope(2, 2, 7, "LegacyApp", now.AddHours(-1));
        var manifest = new RecordingManifest(FormatVersions.RecordingBundle, FormatVersions.Tool, "native-header",
            now, now.AddSeconds(1), RecordingOutcome.Complete, target, new(), new(), true, 0, 1);
        var window = new WindowObservation(2, 2, 7, "TfrmMain", "Legacy application",
            new(0, 0, 1200, 800), true, true, false, false, 96);
        var visual = new AutomationObservation(
            "visual:header", "", "visual:header", "Reservation Code",
            "ControlType.HeaderItem", "UiAtlas.VisualControlRegion", new(100, 200, 180, 28),
            false, true, "UiAtlas.Visual.Geometry", 2, VisualRole: "column-header");
        var native = new AutomationObservation(
            "native:header", "native:grid", "reservation-code", "Reservation Code",
            "ControlType.HeaderItem", "TAbacreGridHeader", new(100, 200, 180, 28),
            true, false, "Win32", 2);
        var frame = new FrameObservation(1, now, "", window, [native, visual],
            false, "ok", "adaptive-root-change", [window]);

        var graph = new RecordingGraphBuilder().Build(new RecordingGraphInput(manifest, [frame], []));

        Assert.Contains(graph.Nodes, node => node.Kind == GraphNodeKind.Control &&
            node.Properties.Any(property => property is { Name: "className", Value: "TAbacreGridHeader" }));
        Assert.DoesNotContain(graph.Nodes, node => node.Kind == GraphNodeKind.Control &&
            node.Properties.Any(property => property is
                { Name: "className", Value: "UiAtlas.VisualControlRegion" }));
        Assert.True(GraphValidator.Validate(graph).IsValid);
    }

    [Fact]
    public void PopupDeltaUsesPreviouslyPromotedOwnerWhenNativeRootIsHidden()
    {
        var now = new DateTimeOffset(2026, 8, 27, 0, 0, 0, TimeSpan.Zero);
        var target = new TargetScope(2, 1, 7, "LegacyApp", now.AddHours(-1));
        var manifest = new RecordingManifest(FormatVersions.RecordingBundle, FormatVersions.Tool, "legacy-popup-owner",
            now, now.AddSeconds(2), RecordingOutcome.Complete, target, new(), new(), true, 0, 2);
        var hiddenRoot = new WindowObservation(1, 1, 7, "TApplication", "Legacy application",
            new(400, 300, 0, 0), true, true, false, false, 96);
        var visibleForm = new WindowObservation(2, 1, 7, "TfrmMain", "Legacy application",
            new(0, 0, 1200, 800), true, true, false, false, 96, OwnerHwnd: 1);
        var formRoot = new AutomationObservation("2.1", "", "", "Legacy application",
            "ControlType.Window", "TfrmMain", visibleForm.Bounds, true, false, "Win32", WindowHwnd: 2);
        var formButton = new AutomationObservation("2.2", "2.1", "open", "Open menu",
            "ControlType.Button", "TButton", new(8, 8, 100, 30), true, false, "Win32", 2, ["Invoke"]);
        var baseline = new FrameObservation(1, now, "", hiddenRoot, [formRoot, formButton], false, "partial",
            "quick-map:auto-tabs-initial-surface", [hiddenRoot, visibleForm],
            ObservationScope: "control-delta", ObservedWindowHwnds: [1]);

        var popup = new WindowObservation(3, 1, 7, "#32768", "",
            new(20, 40, 240, 300), true, true, false, false, 96, OwnerHwnd: 2, ZOrder: 1, IsToolWindow: true);
        var popupRoot = new AutomationObservation("3.1", "", "", "Menu",
            "ControlType.Menu", "#32768", popup.Bounds, true, false, "Win32", WindowHwnd: 3);
        var popupItem = new AutomationObservation("3.2", "3.1", "item", "Menu item",
            "ControlType.MenuItem", "", new(30, 50, 180, 24), true, false, "Win32", 3, ["Invoke"]);
        var popupDelta = new FrameObservation(2, now.AddSeconds(1), "", hiddenRoot, [popupRoot, popupItem], false, "ok",
            "owned-window", [hiddenRoot, popup], ObservationScope: "popup-delta", ObservedWindowHwnds: [3]);

        var graph = new RecordingGraphBuilder().Build(new RecordingGraphInput(manifest, [baseline, popupDelta], []));

        var popupSurface = Assert.Single(graph.Nodes, node =>
            node.Kind == GraphNodeKind.Surface &&
            node.Properties.Any(property => property.Name == "layer" && property.Value == "raw-world") &&
            node.Properties.Any(property => property.Name == "className" && property.Value == "#32768"));
        var ownerId = Assert.Single(popupSurface.Properties, property => property.Name == "ownerRawSurfaceId").Value;
        Assert.Contains(graph.Nodes, node => node.Id == ownerId && node.Kind == GraphNodeKind.Surface);
    }

    [Fact]
    public void VisualOnlyPopupFallbackIsPromotedInsteadOfBeingDropped()
    {
        var now = new DateTimeOffset(2026, 8, 31, 0, 0, 0, TimeSpan.Zero);
        var target = new TargetScope(1, 1, 7, "EXCEL", now.AddHours(-1));
        var manifest = new RecordingManifest(FormatVersions.RecordingBundle, FormatVersions.Tool, "popup-fallback",
            now, now.AddSeconds(2), RecordingOutcome.Partial, target, new(), new(), true, 0, 2);
        var root = new WindowObservation(1, 1, 7, "XLMAIN", "Book1 - Excel",
            new RectI(0, 0, 1200, 800), true, true, false, false, 96);
        var rootControl = new AutomationObservation("1.root", "", "", "Book1 - Excel",
            "ControlType.Window", "XLMAIN", root.Bounds, true, false, "Win32", 1);
        var ribbonButton = new AutomationObservation("1.menu", "1.root", "menu", "Conditional Formatting",
            "ControlType.Button", "NetUIAnchor", new RectI(800, 80, 180, 48), true, false, "Win32", 1,
            ["ExpandCollapse"]);
        var baseline = new FrameObservation(1, now, "", root, [rootControl, ribbonButton], false, "ok",
            "auto-tabs:initial-surface", [root]);

        var popup = new WindowObservation(2, 1, 7, "#32768", "",
            new RectI(800, 128, 280, 320), true, true, false, false, 96, OwnerHwnd: 1, IsToolWindow: true);
        var popupRoot = new AutomationObservation("popup-visual-root:2", "", "", "Visible popup",
            "ControlType.Window", "#32768", popup.Bounds, true, false, "Win32", 2);
        var visualItem = new AutomationObservation(
            "visual:popup-surface:test", popupRoot.RuntimeId, "visual:popup-surface:test", "Visible popup content",
            "ControlType.Custom", "UiAtlas.VisualControlRegion", popup.Bounds, false, false,
            "UiAtlas.Visual.Geometry", 2, VisualRole: "popup-surface");
        var fallback = new FrameObservation(2, now.AddSeconds(1), "", root, [popupRoot, visualItem], true,
            "visual-only", "adaptive-popup", [root, popup], ObservationScope: "popup-delta",
            ObservedWindowHwnds: [2], ScreenshotBounds: popup.Bounds, BaseFrameSequence: 1,
            InteractionSource: ribbonButton, InteractionId: "interaction-popup");

        var graph = new RecordingGraphBuilder().Build(new RecordingGraphInput(manifest, [baseline, fallback], []));

        Assert.Contains(graph.Nodes, node => node.Kind == GraphNodeKind.Surface &&
            node.Properties.Any(property => property.Name == "surfaceClass" && property.Value == "RawPopupWindow"));
        Assert.Contains(graph.Nodes, node => node.Kind == GraphNodeKind.Control &&
            node.Label == "Visible popup content" &&
            node.Properties.Any(property => property.Name == "layer" && property.Value == "raw-world"));
        Assert.True(GraphValidator.Validate(graph).IsValid);
    }

    [Fact]
    public void VisualOnlyLegacyFramePromotesVisibleFormAndControls()
    {
        var now = new DateTimeOffset(2026, 8, 26, 0, 0, 0, TimeSpan.Zero);
        var target = new TargetScope(2, 1, 7, "LegacyApp", now.AddHours(-1));
        var manifest = new RecordingManifest(FormatVersions.RecordingBundle, FormatVersions.Tool, "legacy-visual",
            now, now.AddSeconds(1), RecordingOutcome.Partial, target, new(), new(), true, 0, 1);
        var hiddenRoot = new WindowObservation(1, 1, 7, "TApplication", "Legacy application",
            new(400, 300, 1200, 0), true, true, false, false, 96);
        var visibleForm = new WindowObservation(2, 1, 7, "TfrmMain", "Legacy application",
            new(0, 0, 1200, 800), true, true, false, false, 96, OwnerHwnd: 1);
        var visualButton = new AutomationObservation(
            "visual:v3:0123456789abcdef", "", "visual:v3:0123456789abcdef", "Reservations...",
            "ControlType.Button", "UiAtlas.VisualControlRegion", new(8, 108, 100, 56),
            false, true, "UiAtlas.Visual.Ocr", 2, ["Invoke"], VisualRole: "button", OcrText: "Reservations...");
        var frame = new FrameObservation(1, now, "", hiddenRoot, [visualButton], false, "visual-only",
            "adaptive-root-change", [hiddenRoot, visibleForm]);

        var graph = new RecordingGraphBuilder().Build(new RecordingGraphInput(manifest, [frame], []));

        var rawSurface = Assert.Single(graph.Nodes, node =>
            node.Kind == GraphNodeKind.Surface &&
            node.Properties.Any(property => property.Name == "layer" && property.Value == "raw-world"));
        Assert.Contains(rawSurface.Properties, property => property.Name == "surfaceClass" && property.Value == "RawWindow");
        Assert.Contains(graph.Nodes, node =>
            node.Kind == GraphNodeKind.Control && node.Label == "Reservations..." &&
            node.Properties.Any(property => property.Name == "layer" && property.Value == "semantic-world"));
        Assert.Contains(graph.Nodes.Where(node => node.Kind == GraphNodeKind.State),
            state => state.Properties.Any(property => property.Name == "controlCount" && property.Value == "1"));
    }

    [Fact]
    public void VisualRootRefreshCoveredByModalDialogIsNotPromotedAsMainSurface()
    {
        var now = new DateTimeOffset(2026, 8, 26, 0, 0, 0, TimeSpan.Zero);
        var target = new TargetScope(2, 1, 7, "LegacyApp", now.AddHours(-1));
        var manifest = new RecordingManifest(FormatVersions.RecordingBundle, FormatVersions.Tool, "legacy-dialog",
            now, now.AddSeconds(1), RecordingOutcome.Partial, target, new(), new(), true, 0, 1);
        var hiddenRoot = new WindowObservation(1, 1, 7, "TApplication", "Legacy application",
            new(400, 300, 1200, 0), true, true, false, false, 96);
        var visibleForm = new WindowObservation(2, 1, 7, "TfrmMain", "Legacy application",
            new(0, 0, 1200, 800), true, true, false, false, 96, OwnerHwnd: 1);
        var dialog = new WindowObservation(3, 1, 7, "TfrmChooseItem", "Choose Item",
            new(300, 200, 600, 400), true, true, false, false, 96, OwnerHwnd: 1, ZOrder: 1);
        var visualButton = new AutomationObservation(
            "visual:v3:0123456789abcdef", "", "visual:v3:0123456789abcdef", "Add to Order",
            "ControlType.Button", "UiAtlas.VisualControlRegion", new(20, 100, 100, 40),
            false, true, "UiAtlas.Visual.Ocr", 2, ["Invoke"], VisualRole: "button", OcrText: "Add to Order");
        var frame = new FrameObservation(1, now, "", hiddenRoot, [visualButton], false, "visual-only",
            "adaptive-root-change", [hiddenRoot, visibleForm, dialog]);

        var graph = new RecordingGraphBuilder().Build(new RecordingGraphInput(manifest, [frame], []));

        Assert.Contains(graph.Nodes, node =>
            node.Kind == GraphNodeKind.Control && node.Label == "Add to Order" &&
            node.Properties.Any(property => property.Name == "layer" && property.Value == "raw-data-streams"));
        Assert.DoesNotContain(graph.Nodes, node =>
            node.Kind == GraphNodeKind.Control && node.Label == "Add to Order" &&
            node.Properties.Any(property => property.Name == "layer" &&
                (property.Value == "raw-world" || property.Value == "semantic-world")));
    }

    [Fact]
    public void SuccessfulInteractionMarksItsSemanticControlConfirmed()
    {
        using var temp = new TempDirectory();
        var bundle = SyntheticBundleFactory.Create(temp.Path, interactionTrace: true);

        var graph = new RecordingGraphBuilder().Build(bundle);

        var semanticControls = graph.Nodes.Where(node =>
            node.Kind == GraphNodeKind.Control &&
            node.Properties.Any(property => property.Name == "layer" && property.Value == "semantic-world")).ToArray();
        Assert.True(semanticControls.Any(node =>
            node.Kind == GraphNodeKind.Control &&
            node.Properties.Any(property => property.Name == "automationId" && property.Value == "save") &&
            node.Properties.Any(property => property.Name == "verificationStatus" && property.Value == "Confirmed")),
            string.Join(Environment.NewLine, semanticControls.Select(node =>
                $"{node.Label}: {string.Join(',', node.Properties.Where(property => property.Name is "automationId" or "verificationStatus").Select(property => property.Name + "=" + property.Value))}")));
    }

    [Fact]
    public void ConfirmedHoverShadowIsPromotedInPlaceWithoutDuplicateControl()
    {
        using var temp = new TempDirectory();
        var bundle = SyntheticBundleFactory.Create(
            temp.Path,
            interactionTrace: true,
            firstTrigger: "quick-map:manual",
            hoverShadowPromotion: true);

        var graph = new RecordingGraphBuilder().Build(bundle);
        var semanticControls = graph.Nodes.Where(node =>
            node.Kind == GraphNodeKind.Control &&
            node.Properties.Any(property => property.Name == "layer" && property.Value == "semantic-world") &&
            node.Properties.Any(property => property.Name == "automationId" && property.Value == "shadow:one"))
            .ToArray();

        var promoted = Assert.Single(semanticControls);
        Assert.Contains(promoted.Properties, property =>
            property.Name == "verificationStatus" && property.Value == "Confirmed");
        Assert.Contains(promoted.Evidence, evidence => evidence.FrameSequence == 1);
        Assert.Contains(promoted.Evidence, evidence => evidence.FrameSequence == 2);
    }

    [Fact]
    public void RepeatedQuickMapSnapshotMergesStableSemanticControls()
    {
        using var temp = new TempDirectory();
        var first = SyntheticBundleFactory.Create(
            temp.Path, "quick-a.mlrec", sessionId: "quick-a",
            representativeFrames: [1], firstTrigger: "quick-map:standalone");
        var second = SyntheticBundleFactory.Create(
            temp.Path, "quick-b.mlrec", sessionId: "quick-b",
            representativeFrames: [1], firstTrigger: "quick-map:standalone");

        var single = new RecordingGraphBuilder().Build(first);
        var merged = new RecordingGraphBuilder().Build([first, second], "quick-logical-map");
        static int SemanticControlCount(UiKnowledgeGraph graph) => graph.Nodes.Count(node =>
            node.Kind == GraphNodeKind.Control &&
            node.Properties.Any(property => property.Name == "layer" && property.Value == "semantic-world"));

        Assert.Equal(SemanticControlCount(single), SemanticControlCount(merged));
        Assert.All(merged.Nodes.Where(node => node.Kind == GraphNodeKind.Control &&
            node.Properties.Any(property => property.Name == "layer" && property.Value == "semantic-world")),
            node => Assert.Equal(2, node.Evidence.Select(evidence => evidence.BundleId).Distinct().Count()));
    }

    [Fact]
    public void LaterVisibleObservationUpgradesQuickMapCandidateInPlace()
    {
        using var temp = new TempDirectory();
        var bundle = SyntheticBundleFactory.Create(
            temp.Path,
            firstTrigger: "quick-map:standalone");

        var graph = new RecordingGraphBuilder().Build(bundle);
        var upgraded = Assert.Single(graph.Nodes, node =>
            node.Kind == GraphNodeKind.Control &&
            node.Properties.Any(property => property.Name == "layer" && property.Value == "semantic-world") &&
            node.Evidence.Any(evidence => evidence.FrameSequence == 1 && evidence.Bounds == new RectI(110, 10, 80, 24)));

        Assert.Contains(upgraded.Evidence, evidence => evidence.FrameSequence == 2);
        Assert.Contains(upgraded.Properties, property =>
            property.Name == "verificationStatus" && property.Value == "Observed");
        Assert.DoesNotContain(upgraded.Properties, property =>
            property.Name == "verificationStatus" && property.Value == "Unverified");
    }

    [Fact]
    public void DialogDeltaPromotesOwnedDialogAndControlsThroughSemanticWorld()
    {
        using var temp = new TempDirectory();
        var bundle = SyntheticBundleFactory.Create(temp.Path, dialogDelta: true);

        Assert.True(RecordingBundleValidator.Validate(bundle).IsValid);
        var graph = new RecordingGraphBuilder().Build(bundle);

        Assert.Contains(graph.Nodes, node =>
            node.Properties.Any(property => property.Name == "surfaceClass" && property.Value == "RawDialogWindow") &&
            node.Evidence.Any(evidence => evidence.FrameSequence == 2));
        Assert.Contains(graph.Nodes, node =>
            node.Properties.Any(property => property.Name == "semanticClass" && property.Value == "SemanticDialogWindow"));
        Assert.Contains(graph.Nodes, node => node.Label == "OK" &&
            node.Evidence.Any(evidence => evidence.FrameSequence == 2));
        Assert.Contains(graph.Nodes, node => node.Kind == GraphNodeKind.State &&
            node.ParentId == graph.Nodes.Single(surface => surface.Kind == GraphNodeKind.Surface &&
                surface.Properties.Any(property => property.Name == "layer" && property.Value == "raw-world") &&
                surface.Properties.Any(property => property.Name == "surfaceClass" && property.Value == "RawDialogWindow")).Id &&
            node.Properties.Any(property => property.Name == "contextLabel" && property.Value == "Number"));
    }

    [Fact]
    public void DialogTabsRemainVariantsOfOneDialogSurfaceWhenTopMostFlagSettles()
    {
        var now = new DateTimeOffset(2026, 8, 31, 0, 0, 0, TimeSpan.Zero);
        var target = new TargetScope(1, 1, 7, "EXCEL", now.AddHours(-1));
        var manifest = new RecordingManifest(FormatVersions.RecordingBundle, FormatVersions.Tool, "dialog-tabs",
            now, now.AddSeconds(4), RecordingOutcome.Complete, target, new(), new(), true, 0, 4);
        var root = new WindowObservation(1, 1, 7, "XLMAIN", "Book1 - Excel", new(0, 0, 1200, 900),
            true, false, false, false, 96, Style: 0x10CF0000);
        var dialog = new WindowObservation(2, 99, 7, "bosa_sdm_XL9", "Page Setup", new(240, 140, 560, 520),
            true, true, false, false, 96, OwnerHwnd: 99, Style: 0x10C80000, ExStyle: 0x501);
        var openingDialog = dialog with { ExStyle = 0x509, IsTopMost = true };

        AutomationObservation[] DialogControls(WindowObservation currentDialog, string selectedTab, string pageControl) =>
        [
            new("dialog", "", "", "Page Setup", "ControlType.Window", currentDialog.ClassName,
                currentDialog.Bounds, true, false, "Win32", currentDialog.Hwnd),
            new("tabs", "dialog", "tabs", "Pages", "ControlType.Tab", "SysTabControl32",
                new(260, 180, 500, 36), true, false, "Win32", currentDialog.Hwnd),
            new("page", "tabs", "page", "Page", "ControlType.TabItem", "SysTabControl32",
                new(260, 180, 70, 32), true, false, "Win32", currentDialog.Hwnd,
                ["SelectionItem"], IsSelected: selectedTab == "Page"),
            new("margins", "tabs", "margins", "Margins", "ControlType.TabItem", "SysTabControl32",
                new(330, 180, 80, 32), true, false, "Win32", currentDialog.Hwnd,
                ["SelectionItem"], IsSelected: selectedTab == "Margins"),
            new("header-footer", "tabs", "header-footer", "Header/Footer", "ControlType.TabItem", "SysTabControl32",
                new(410, 180, 110, 32), true, false, "Win32", currentDialog.Hwnd,
                ["SelectionItem"], IsSelected: selectedTab == "Header/Footer"),
            new("sheet", "tabs", "sheet", "Sheet", "ControlType.TabItem", "SysTabControl32",
                new(520, 180, 70, 32), true, false, "Win32", currentDialog.Hwnd,
                ["SelectionItem"], IsSelected: selectedTab == "Sheet"),
            new("page-control", "dialog", pageControl, pageControl, "ControlType.Edit", "Edit",
                new(300, 260, 180, 28), true, false, "Win32", currentDialog.Hwnd, ["Value"]),
            new("ok", "dialog", "OK", "OK", "ControlType.Button", "Button",
                new(600, 610, 80, 28), true, false, "Win32", currentDialog.Hwnd, ["Invoke"])
        ];

        var first = new FrameObservation(1, now, "", root, DialogControls(openingDialog, "Sheet", "Print area"),
            false, "ok", "adaptive-dialog:Page Setup", [root, openingDialog],
            ObservationScope: "full-root", ObservedWindowHwnds: [openingDialog.Hwnd]);
        var second = new FrameObservation(2, now.AddSeconds(1), "", root, DialogControls(dialog, "Page", "Paper size"),
            false, "ok", "adaptive-dialog:Page Setup", [root, dialog],
            ObservationScope: "full-root", ObservedWindowHwnds: [dialog.Hwnd]);
        var third = new FrameObservation(3, now.AddSeconds(2), "", root, DialogControls(dialog, "Margins", "Top margin"),
            false, "ok", "adaptive-dialog:Page Setup", [root, dialog],
            ObservationScope: "full-root", ObservedWindowHwnds: [dialog.Hwnd]);
        var fourth = new FrameObservation(4, now.AddSeconds(3), "", root, DialogControls(dialog, "Header/Footer", "Custom header"),
            false, "ok", "adaptive-dialog:Page Setup", [root, dialog],
            ObservationScope: "full-root", ObservedWindowHwnds: [dialog.Hwnd]);

        var graph = new RecordingGraphBuilder().Build(new RecordingGraphInput(manifest, [first, second, third, fourth], []));
        var surface = Assert.Single(graph.Nodes, node =>
            node.Kind == GraphNodeKind.Surface &&
            node.Properties.Any(property => property.Name == "layer" && property.Value == "raw-world") &&
            node.Properties.Any(property => property.Name == "surfaceClass" && property.Value == "RawDialogWindow"));
        var states = graph.Nodes.Where(node => node.Kind == GraphNodeKind.State && node.ParentId == surface.Id).ToArray();

        Assert.Equal(4, states.Length);
        Assert.Equal([1L, 2L, 3L, 4L], states.SelectMany(state => state.Evidence)
            .Select(evidence => evidence.FrameSequence).Distinct().Order().ToArray());
    }

    [Fact]
    public void ExplicitExcelDialogWithProxyRootOwnerIsStillPromoted()
    {
        using var temp = new TempDirectory();
        var bundle = SyntheticBundleFactory.Create(
            temp.Path,
            dialogDelta: true,
            detachedDialogRootOwner: true);

        Assert.True(RecordingBundleValidator.Validate(bundle).IsValid);
        var graph = new RecordingGraphBuilder().Build(bundle);
        var dialog = Assert.Single(graph.Nodes, node =>
            node.Kind == GraphNodeKind.Surface &&
            node.Properties.Any(property => property.Name == "layer" && property.Value == "raw-world") &&
            node.Properties.Any(property => property.Name == "surfaceClass" && property.Value == "RawDialogWindow"));

        Assert.Contains(dialog.Evidence, evidence => evidence.FrameSequence == 2);
        Assert.Contains(graph.Nodes, node => node.Kind == GraphNodeKind.Control && node.Label == "OK" &&
            node.Properties.Any(property => property.Name == "rawSurfaceId" && property.Value == dialog.Id));
    }

    [Fact]
    public void PeerRootWindowRetainsTitleScreenshotAndPromotesThroughSemanticWorld()
    {
        using var temp = new TempDirectory();
        var bundle = SyntheticBundleFactory.Create(
            temp.Path,
            includeScreenshot: true,
            peerRootDelta: true);

        Assert.True(RecordingBundleValidator.Validate(bundle).IsValid);
        var graph = new RecordingGraphBuilder().Build(bundle);
        var rds = Assert.Single(graph.Nodes, node =>
            node.Kind == GraphNodeKind.Window &&
            node.Label == "Untitled - Field Service Mission" &&
            node.Properties.Any(property => property.Name == "layer" && property.Value == "raw-data-streams"));
        Assert.Contains(rds.Properties, property => property.Name == "role" && property.Value == "peer-root");
        Assert.Contains(rds.Properties, property => property.Name == "nativeWindowType" && property.Value == "RawWindow");
        Assert.Contains(rds.Evidence, evidence =>
            evidence.FrameSequence == 2 && evidence.ScreenshotEntry == "raw/frames/frame-000002.png");

        var raw = Assert.Single(graph.Nodes, node =>
            node.Kind == GraphNodeKind.Surface &&
            node.Properties.Any(property => property.Name == "layer" && property.Value == "raw-world") &&
            node.Properties.Any(property => property.Name == "role" && property.Value == "peer-root"));
        Assert.Contains(raw.Properties, property => property.Name == "surfaceClass" && property.Value == "RawWindow");
        Assert.Contains(graph.Nodes, node => node.Kind == GraphNodeKind.Control && node.Label == "OK" &&
            node.Properties.Any(property => property.Name == "rawSurfaceId" && property.Value == raw.Id));
        Assert.Contains(graph.Nodes, node =>
            node.Kind == GraphNodeKind.Surface &&
            node.Properties.Any(property => property.Name == "semanticClass" && property.Value == "SemanticWindow") &&
            node.Properties.Any(property => property.Name == "sourceRawSurfaceId" && property.Value == raw.Id));

        var pipeline = new UiMappingReadModel(graph).BuildPipeline(UiUnderstandingLevel.RawDataStreams);
        var peerNode = Assert.Single(pipeline.Nodes, node =>
            node.Kind == UiPipelineNodeKind.NativeSurface && node.SourceIds.Contains(rds.Id));
        Assert.Equal("Untitled - Field Service Mission", peerNode.DisplayName);
    }

    [Fact]
    public void PeerRootNativeEvidenceStillPromotesWhenAccessibilityTimesOut()
    {
        using var temp = new TempDirectory();
        var bundle = SyntheticBundleFactory.Create(
            temp.Path,
            includeScreenshot: true,
            peerRootDelta: true,
            unreadyPeerRootDelta: true,
            outcome: RecordingOutcome.Partial);

        Assert.True(RecordingBundleValidator.Validate(bundle).IsValid);
        var graph = new RecordingGraphBuilder().Build(bundle);
        var rds = Assert.Single(graph.Nodes, node =>
            node.Kind == GraphNodeKind.Window &&
            node.Label == "Untitled - Field Service Mission" &&
            node.Properties.Any(property => property.Name == "role" && property.Value == "peer-root"));
        var raw = Assert.Single(graph.Nodes, node =>
            node.Kind == GraphNodeKind.Surface &&
            node.Properties.Any(property => property.Name == "layer" && property.Value == "raw-world") &&
            node.Properties.Any(property => property.Name == "role" && property.Value == "peer-root"));
        Assert.Contains(raw.Evidence, evidence =>
            evidence.FrameSequence == 2 && evidence.ScreenshotEntry == "raw/frames/frame-000002.png");
        Assert.Contains(graph.Nodes, node =>
            node.Kind == GraphNodeKind.Surface &&
            node.Properties.Any(property => property.Name == "layer" && property.Value == "semantic-world") &&
            node.Properties.Any(property => property.Name == "sourceRawSurfaceId" && property.Value == raw.Id));

        var pipeline = new UiMappingReadModel(graph).BuildPipeline(UiUnderstandingLevel.RawDataStreams);
        var peerNode = Assert.Single(pipeline.Nodes, node =>
            node.Kind == UiPipelineNodeKind.NativeSurface && node.SourceIds.Contains(rds.Id));
        Assert.Equal("Untitled - Field Service Mission", peerNode.DisplayName);
    }

    [Fact]
    public void EmptyDialogEvidenceRemainsInRawDataStreamsButIsNotPromoted()
    {
        using var temp = new TempDirectory();
        var bundle = SyntheticBundleFactory.Create(
            temp.Path,
            dialogDelta: true,
            emptyDialogDelta: true);

        Assert.True(RecordingBundleValidator.Validate(bundle).IsValid);
        var graph = new RecordingGraphBuilder().Build(bundle);

        Assert.Contains(graph.Nodes, node =>
            node.Kind == GraphNodeKind.Window &&
            node.Properties.Any(property => property.Name == "layer" && property.Value == "raw-data-streams") &&
            node.Properties.Any(property => property.Name == "nativeWindowType" && property.Value == "RawDialogWindow") &&
            node.Evidence.Any(evidence => evidence.FrameSequence == 2));
        Assert.DoesNotContain(graph.Nodes, node =>
            node.Kind == GraphNodeKind.Surface &&
            node.Properties.Any(property => property.Name == "surfaceClass" && property.Value == "RawDialogWindow") &&
            node.Evidence.Any(evidence => evidence.FrameSequence == 2));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void RecorderDeltaFramesRemainValidAndBuildable(
        bool controlDelta,
        bool emptyScreenshotBoundsWithoutImage)
    {
        using var temp = new TempDirectory();
        var bundle = SyntheticBundleFactory.Create(
            temp.Path,
            popupDelta: !controlDelta,
            controlDelta: controlDelta,
            emptyScreenshotBoundsWithoutImage: emptyScreenshotBoundsWithoutImage);

        Assert.True(RecordingBundleValidator.Validate(bundle).IsValid);
        Assert.NotEmpty(new RecordingGraphBuilder().Build(bundle).Nodes);
    }

    [Fact]
    public void PopupDeltaMaterializesOnlyCapturedPopupEvidence()
    {
        using var temp = new TempDirectory();
        var bundle = SyntheticBundleFactory.Create(temp.Path, includeScreenshot: true, popupDelta: true);

        Assert.True(RecordingBundleValidator.Validate(bundle).IsValid);
        var graph = new RecordingGraphBuilder().Build(bundle);
        var deltaEvidence = graph.Nodes.SelectMany(node => node.Evidence).Where(evidence => evidence.FrameSequence == 2).ToArray();

        Assert.NotEmpty(deltaEvidence);
        Assert.All(deltaEvidence, evidence => Assert.True(evidence.Bounds is { X: >= 840, Y: >= 120 }));
        Assert.DoesNotContain(deltaEvidence, evidence => evidence.Bounds == new RectI(0, 0, 1200, 900));
    }

    [Fact]
    public void PopupSourceCreatesExplicitRawAndSemanticOpensPopupRelationships()
    {
        using var temp = new TempDirectory();
        var bundle = SyntheticBundleFactory.Create(
            temp.Path,
            popupDelta: true,
            popupInteractionSource: true);

        var graph = new RecordingGraphBuilder().Build(bundle);
        var relationships = graph.Edges.Where(edge => edge.Kind == "opens-popup").ToArray();

        Assert.Equal(2, relationships.Length);
        Assert.All(relationships, edge =>
        {
            Assert.Contains(graph.Nodes, node => node.Id == edge.FromId && node.Kind == GraphNodeKind.Control);
            Assert.Contains(graph.Nodes, node => node.Id == edge.ToId && node.Kind == GraphNodeKind.Surface);
            Assert.Contains(edge.Evidence, evidence => evidence.FrameSequence == 2);
        });
        Assert.Contains(graph.Nodes, node => node.Properties.Any(property =>
            property.Name is "interactionSourceRawControlId" or "interactionSourceControlId"));
    }

    [Fact]
    public void InteractionTraceBuildsCausalEdgesAndUnobservedAffordances()
    {
        using var temp = new TempDirectory();
        var bundle = SyntheticBundleFactory.Create(temp.Path, interactionTrace: true);

        Assert.True(RecordingBundleValidator.Validate(bundle).IsValid);
        var graph = new RecordingGraphBuilder().Build(bundle);
        var interactions = graph.Edges.Where(edge => edge.Kind == "interaction").ToArray();
        Assert.NotEmpty(interactions);
        Assert.All(interactions, interaction =>
            Assert.Equal("Succeeded", interaction.Properties.Single(property => property.Name == "outcome").Value));
        Assert.Contains(graph.Nodes, node => node.Kind == GraphNodeKind.Control &&
            node.Properties.Any(property => property.Name == "affordance" && property.Value == "Invoke"));

        var model = new UiMappingReadModel(graph);
        Assert.Single(model.InteractionSteps);
        Assert.Contains(model.Affordances, affordance => affordance.Action == InteractionActionKind.Invoke);
    }

    [Fact]
    public void PointerObservedInteractionCanUseControlEvidenceCapturedAfterTheClick()
    {
        using var temp = new TempDirectory();
        var bundle = SyntheticBundleFactory.Create(
            temp.Path,
            interactionTrace: true,
            pointerObservedSourceAfterClick: true);

        var graph = new RecordingGraphBuilder().Build(bundle);
        var interactions = graph.Edges.Where(edge => edge.Kind == "interaction").ToArray();

        Assert.NotEmpty(interactions);
        Assert.All(interactions, interaction =>
        {
            Assert.Contains(interaction.Properties, property =>
                property.Name == "sourceFrameSequence" && property.Value == "1");
            Assert.Contains(interaction.Properties, property =>
                property.Name == "sourceControlEvidenceFrameSequence" && property.Value == "2");
        });
        Assert.True(GraphValidator.Validate(graph).IsValid);
    }

    [Fact]
    public void ContaminatedLegacyPopupRemainsRawDataButIsNotPromoted()
    {
        using var temp = new TempDirectory();
        var bundle = SyntheticBundleFactory.Create(
            temp.Path,
            popupDelta: true,
            contaminatedPopupDelta: true);

        Assert.True(RecordingBundleValidator.Validate(bundle).IsValid);
        var graph = new RecordingGraphBuilder().Build(bundle);
        var rawDataEvidence = graph.Nodes
            .Where(node => node.Properties.Any(property => property.Name == "layer" && property.Value == "raw-data-streams"))
            .SelectMany(node => node.Evidence)
            .Where(evidence => evidence.FrameSequence == 2)
            .ToArray();
        var promotedEvidence = graph.Nodes
            .Where(node => node.Properties.Any(property => property.Name == "layer" &&
                property.Value is "raw-world" or "semantic-world"))
            .SelectMany(node => node.Evidence)
            .Where(evidence => evidence.FrameSequence == 2)
            .ToArray();

        Assert.NotEmpty(rawDataEvidence);
        Assert.Empty(promotedEvidence);
    }

    [Fact]
    public void PopupOwnedByAnotherProcessRootRemainsRawDataButIsNotPromoted()
    {
        using var temp = new TempDirectory();
        var bundle = SyntheticBundleFactory.Create(
            temp.Path,
            popupDelta: true,
            foreignOwnedPopup: true);

        var graph = new RecordingGraphBuilder().Build(bundle);

        Assert.Contains(graph.Nodes, node =>
            node.Properties.Any(property => property.Name == "layer" && property.Value == "raw-data-streams") &&
            node.Evidence.Any(evidence => evidence.FrameSequence == 2));
        Assert.DoesNotContain(graph.Nodes, node =>
            node.Properties.Any(property => property.Name == "layer" && property.Value is "raw-world" or "semantic-world") &&
            node.Evidence.Any(evidence => evidence.FrameSequence == 2));
        Assert.True(GraphValidator.Validate(graph).IsValid);
    }

    [Fact]
    public void ListPopupValuesScrollbarAndThumbArePromotedAsControls()
    {
        using var temp = new TempDirectory();
        var bundle = SyntheticBundleFactory.Create(
            temp.Path,
            popupDelta: true,
            valueListPopupDelta: true);

        Assert.True(RecordingBundleValidator.Validate(bundle).IsValid);
        var graph = new RecordingGraphBuilder().Build(bundle);
        var promoted = graph.Nodes
            .Where(node => node.Properties.Any(property => property.Name == "layer" &&
                property.Value is "raw-world" or "semantic-world"))
            .ToArray();

        Assert.Contains(promoted, node => node.Properties.Any(property =>
            property.Name == "surfaceClass" && property.Value == "RawPopupWindow"));
        Assert.Contains(promoted, node => node.Properties.Any(property =>
            property.Name == "controlType" && property.Value == "Text"));
        Assert.Contains(promoted, node => node.Properties.Any(property =>
            property.Name == "controlType" && property.Value == "ScrollBar"));
        Assert.Contains(promoted, node => node.Properties.Any(property =>
            property.Name == "controlType" && property.Value == "Thumb"));
    }

    [Fact]
    public void VisibleWorksheetCellsHeadersAndFormulaButtonsArePromotedOnTheMainSurface()
    {
        using var temp = new TempDirectory();
        var bundle = SyntheticBundleFactory.Create(temp.Path, worksheetControls: true);

        Assert.True(RecordingBundleValidator.Validate(bundle).IsValid);
        var graph = new RecordingGraphBuilder().Build(bundle);
        var promoted = graph.Nodes
            .Where(node => node.Properties.Any(property => property.Name == "layer" &&
                property.Value is "raw-world" or "semantic-world"))
            .ToArray();

        Assert.Contains(promoted, node => node.Properties.Any(property =>
            property.Name == "className" && property.Value == "XLSpreadsheetCell"));
        Assert.Contains(promoted, node => node.Properties.Any(property =>
            property.Name == "className" && property.Value == "XLGridColumnHeader"));
        Assert.Contains(promoted, node => node.Label == "Insert Function");
    }

    [Fact]
    public void FullRootTimeoutPopupRemainsRawDataButIsNotPromoted()
    {
        using var temp = new TempDirectory();
        var bundle = SyntheticBundleFactory.Create(temp.Path, unreadyFullRootPopup: true);

        Assert.True(RecordingBundleValidator.Validate(bundle).IsValid);
        var graph = new RecordingGraphBuilder().Build(bundle);
        var rawDataEvidence = graph.Nodes
            .Where(node => node.Properties.Any(property => property.Name == "layer" && property.Value == "raw-data-streams"))
            .SelectMany(node => node.Evidence)
            .Where(evidence => evidence.FrameSequence == 2)
            .ToArray();
        var promotedPopup = graph.Nodes
            .Where(node => node.Kind == GraphNodeKind.Surface &&
                           node.Properties.Any(property => property.Name == "surfaceClass" && property.Value == "RawPopupWindow"))
            .ToArray();
        var promotedEvidence = graph.Nodes
            .Where(node => node.Properties.Any(property => property.Name == "layer" &&
                property.Value is "raw-world" or "semantic-world"))
            .SelectMany(node => node.Evidence)
            .Where(evidence => evidence.FrameSequence == 2)
            .ToArray();

        Assert.NotEmpty(rawDataEvidence);
        Assert.Empty(promotedPopup);
        Assert.Empty(promotedEvidence);
    }

    [Fact]
    public void BundleBuildIsSemanticallyDeterministic()
    {
        using var temp = new TempDirectory();
        var bundle = SyntheticBundleFactory.Create(temp.Path);
        var first = new RecordingGraphBuilder().Build(bundle);
        var second = new RecordingGraphBuilder().Build(bundle);
        Assert.Equal(first.Metadata.SemanticHash, second.Metadata.SemanticHash);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(first), System.Text.Json.JsonSerializer.Serialize(second));
    }

    [Fact]
    public void MultiBundleBuildCarriesLogicalSessionMetadataAndEvidence()
    {
        using var temp = new TempDirectory();
        var first = SyntheticBundleFactory.Create(temp.Path, "first.mlrec", includeScreenshot: true, sessionId: "session-a");
        var second = SyntheticBundleFactory.Create(temp.Path, "second.mlrec", includeScreenshot: true, sessionId: "session-b");

        var graph = new RecordingGraphBuilder().Build([first, second], logicalMapId: "logical-map");

        Assert.Equal(FormatVersions.Graph, graph.Metadata.FormatVersion);
        Assert.Equal(["session-a", "session-b"], graph.Metadata.EffectiveSourceBundleIds.OrderBy(value => value, StringComparer.Ordinal));
        Assert.Equal("logical-map", graph.Metadata.EffectiveLogicalMapId);
        Assert.Contains(graph.Nodes.SelectMany(node => node.Evidence), evidence => evidence.BundleId == "session-a");
        Assert.Contains(graph.Nodes.SelectMany(node => node.Evidence), evidence => evidence.BundleId == "session-b");
        Assert.True(GraphValidator.Validate(graph).IsValid);
    }

    [Fact]
    public void ResumeMergesEqualInteractionRoutesButRetainsSessionStepsDeterministically()
    {
        using var temp = new TempDirectory();
        var firstBundle = SyntheticBundleFactory.Create(temp.Path, "trace-a.mlrec", sessionId: "trace-a", interactionTrace: true);
        var secondBundle = SyntheticBundleFactory.Create(temp.Path, "trace-b.mlrec", sessionId: "trace-b", interactionTrace: true);

        var first = new RecordingGraphBuilder().Build([firstBundle, secondBundle], logicalMapId: "trace-map");
        var second = new RecordingGraphBuilder().Build([secondBundle, firstBundle], logicalMapId: "trace-map");
        var model = new UiMappingReadModel(first);

        Assert.Equal(2, model.InteractionSteps.Count);
        Assert.Equal(["trace-a", "trace-b"], model.InteractionSteps.Select(step => step.BundleId).Order().ToArray());
        Assert.All(model.Routes, route => Assert.Equal(2, route.ObservedCount));
        Assert.Equal(
            System.Text.Json.JsonSerializer.Serialize(model.Routes),
            System.Text.Json.JsonSerializer.Serialize(new UiMappingReadModel(second).Routes));
    }

    [Fact]
    public void PartialAutoBundleBuildsIntoAValidGraph()
    {
        using var temp = new TempDirectory();
        var bundle = SyntheticBundleFactory.Create(
            temp.Path,
            "partial-auto.mlrec",
            sessionId: "partial-auto",
            outcome: RecordingOutcome.Partial,
            markers: ["auto-tabs:selected:home"]);

        var graph = new RecordingGraphBuilder().Build(bundle);

        Assert.Equal(["partial-auto"], graph.Metadata.EffectiveSourceBundleIds);
        Assert.True(GraphValidator.Validate(graph).IsValid);
        Assert.NotEmpty(graph.Nodes);
    }

    [Theory]
    [InlineData(FormatVersions.IntermediateGraph)]
    [InlineData(FormatVersions.PreviousGraph)]
    public void IntermediateGraphMigrationPromotesLogicalMapMetadata(string sourceVersion)
    {
        var node = new GraphNode("app_000000000000000000000000", GraphNodeKind.Application, "", "legacy", "Legacy", [], []);
        var hash = GraphSemantics.ComputeHash([node], []);
        var graph = new UiKnowledgeGraph(
            new GraphMetadata(sourceVersion, FormatVersions.Tool, "graph_000000000000000000000000",
                DateTimeOffset.UnixEpoch, "bundle-a", hash, FormatVersions.FullEvidenceProfile),
            [node],
            []);

        var migrated = GraphMigration.UpgradeToCurrent(graph);

        Assert.Equal(FormatVersions.Graph, migrated.Metadata.FormatVersion);
        Assert.Equal(["bundle-a"], migrated.Metadata.EffectiveSourceBundleIds);
        Assert.Equal("bundle-a", migrated.Metadata.EffectiveLogicalMapId);
    }

    [Fact]
    public void ValidatorAcceptsEvidenceFromAnyDeclaredSourceBundle()
    {
        using var temp = new TempDirectory();
        var first = SyntheticBundleFactory.Create(temp.Path, "first.mlrec", sessionId: "bundle-a");
        var second = SyntheticBundleFactory.Create(temp.Path, "second.mlrec", sessionId: "bundle-b");
        var graph = new RecordingGraphBuilder().Build([first, second], logicalMapId: "logical-map");
        var allowed = graph.Metadata.EffectiveSourceBundleIds.ToHashSet(StringComparer.Ordinal);

        Assert.All(graph.Nodes.SelectMany(node => node.Evidence), evidence => Assert.Contains(evidence.BundleId, allowed));
        Assert.All(graph.Edges.SelectMany(edge => edge.Evidence), evidence => Assert.Contains(evidence.BundleId, allowed));
    }

    [Fact]
    public void SqliteAndJsonRoundTripLosslessly()
    {
        using var temp = new TempDirectory();
        var graph = new RecordingGraphBuilder().Build(SyntheticBundleFactory.Create(temp.Path));
        var database = System.IO.Path.Combine(temp.Path, "graph.db");
        var json = System.IO.Path.Combine(temp.Path, "graph.json");
        SqliteGraphStore.Save(graph, database);
        GraphJsonStore.Save(graph, json);
        var expected = System.Text.Json.JsonSerializer.Serialize(graph);
        Assert.Equal(expected, System.Text.Json.JsonSerializer.Serialize(SqliteGraphStore.Load(database)));
        Assert.Equal(expected, System.Text.Json.JsonSerializer.Serialize(GraphJsonStore.Load(json)));
        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(json));
        var root = document.RootElement;
        Assert.Equal(FormatVersions.GraphJsonExport, root.GetProperty("formatVersion").GetString());
        Assert.Equal(["formatVersion", "metadata", "nodeOrder", "edgeOrder", "app", "process", "rawDataStreams", "rawWorld", "semanticWorld"],
            root.EnumerateObject().Select(property => property.Name));
        Assert.Single(root.GetProperty("app").GetProperty("nodes").EnumerateArray());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("process").GetProperty("name").GetString()));
        Assert.NotEmpty(root.GetProperty("rawDataStreams").GetProperty("nodes").EnumerateArray());
        Assert.NotEmpty(root.GetProperty("rawWorld").GetProperty("nodes").EnumerateArray());
        Assert.NotEmpty(root.GetProperty("semanticWorld").GetProperty("nodes").EnumerateArray());
    }

    [Fact]
    public void JsonLoadKeepsLegacyFlatV2InterchangeReadable()
    {
        using var temp = new TempDirectory();
        var graph = new RecordingGraphBuilder().Build(SyntheticBundleFactory.Create(temp.Path));
        var json = Path.Combine(temp.Path, "legacy-flat.json");
        File.WriteAllBytes(json, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(graph, JsonDefaults.Options));

        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(graph),
            System.Text.Json.JsonSerializer.Serialize(GraphJsonStore.Load(json)));
    }

    [Fact]
    public void CorruptSqliteFailsClosed()
    {
        using var temp = new TempDirectory();
        var database = System.IO.Path.Combine(temp.Path, "graph.db");
        File.WriteAllText(database, "not a database");
        Assert.Throws<InvalidDataException>(() => SqliteGraphStore.Load(database));
    }

    [Fact]
    public void SqliteMetadataRejectsOversizeAndDuplicateJsonMembers()
    {
        using var temp = new TempDirectory();
        var graph = new RecordingGraphBuilder().Build(SyntheticBundleFactory.Create(temp.Path));
        var oversized = Path.Combine(temp.Path, "oversized.db");
        var duplicate = Path.Combine(temp.Path, "duplicate.db");
        SqliteGraphStore.Save(graph, oversized);
        SqliteGraphStore.Save(graph, duplicate);

        UpdateMetadata(oversized, new string('x', 64 * 1024 + 1));
        var metadata = System.Text.Json.JsonSerializer.Serialize(graph.Metadata, JsonDefaults.Options);
        UpdateMetadata(duplicate, "{\"formatVersion\":\"duplicate\"," + metadata[1..]);

        Assert.Throws<InvalidDataException>(() => SqliteGraphStore.Load(oversized));
        Assert.Throws<InvalidDataException>(() => SqliteGraphStore.Load(duplicate));
    }

    [Fact]
    public void SqliteMetadataRejectsNonTextBeforeMaterialization()
    {
        using var temp = new TempDirectory();
        var graph = new RecordingGraphBuilder().Build(SyntheticBundleFactory.Create(temp.Path));
        var database = Path.Combine(temp.Path, "blob-metadata.db");
        SqliteGraphStore.Save(graph, database);
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE metadata SET value=zeroblob(65536) WHERE key='graph'";
            command.ExecuteNonQuery();
        }

        Assert.Throws<InvalidDataException>(() => SqliteGraphStore.Load(database));
    }

    private static void UpdateMetadata(string path, string value)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE metadata SET value=$value WHERE key='graph'";
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    [Fact]
    public void MalformedJsonFailsAsInvalidData()
    {
        using var temp = new TempDirectory();
        var path = System.IO.Path.Combine(temp.Path, "graph.json");
        File.WriteAllText(path, "{not-json");
        Assert.Throws<InvalidDataException>(() => GraphJsonStore.Load(path));
    }

    [Fact]
    public void ValidatorRejectsDuplicateIdsAndMissingEvidence()
    {
        var metadata = new GraphMetadata(FormatVersions.Graph, FormatVersions.Tool, "g", DateTimeOffset.UnixEpoch, "b", "h", FormatVersions.FullEvidenceProfile);
        var node = new GraphNode("x", GraphNodeKind.State, "missing", "k", "state", [], []);
        var report = GraphValidator.Validate(new(metadata, [node, node], []));
        Assert.False(report.IsValid);
        Assert.Contains(report.Issues, x => x.Code == "graph.duplicate-id");
        Assert.Contains(report.Issues, x => x.Code == "graph.evidence");
    }

    [Fact]
    public void SafeExportRemovesLocalValuesAndScreenshotReferences()
    {
        var evidence = new EvidenceRef("b", 1, "obs.json", new(0, 0, 1, 1), "frames/private.png");
        var node = new GraphNode("app", GraphNodeKind.Application, "", "C:\\Users\\person", "person@example.test",
            [new("executablePath", "C:\\Users\\person\\app.exe"), new("stable", "button")], [evidence]);
        var graph = new UiKnowledgeGraph(new(FormatVersions.Graph, FormatVersions.Tool, "g", DateTimeOffset.UnixEpoch, "b", "h", FormatVersions.FullEvidenceProfile), [node], []);
        var safe = GraphExport.ApplyProfile(graph, false);
        Assert.Equal(FormatVersions.SafeExportProfile, safe.Metadata.PrivacyProfile);
        Assert.DoesNotContain("Users", System.Text.Json.JsonSerializer.Serialize(safe));
        Assert.DoesNotContain("person", System.Text.Json.JsonSerializer.Serialize(safe), StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(graph.Metadata.SemanticHash, safe.Metadata.SemanticHash);
        Assert.NotEqual(graph.Nodes[0].Id, safe.Nodes[0].Id);
        Assert.Null(safe.Nodes[0].Evidence[0].ScreenshotEntry);
        Assert.Null(safe.Nodes[0].Evidence[0].Bounds);
        Assert.Equal("area", Assert.Single(safe.Nodes[0].Properties).Name);
    }

    [Fact]
    public void SafeExportDropsFreeTextFromNodeAndEdgeProperties()
    {
        var evidence = new EvidenceRef("bundle-private", 7, "raw/observations/frame.json", new(0, 0, 1, 1));
        var app = new GraphNode("app", GraphNodeKind.Application, "", "private-id", "private label",
            [new("trigger", "confidential document")], [evidence]);
        var edge = new GraphEdge("edge", "observed", app.Id, app.Id, [new("trigger", "secret action")], [evidence]);
        var graph = new UiKnowledgeGraph(new(FormatVersions.Graph, FormatVersions.Tool, "g", DateTimeOffset.UnixEpoch, "b", "h", FormatVersions.FullEvidenceProfile), [app], [edge]);
        var json = System.Text.Json.JsonSerializer.Serialize(GraphExport.ApplyProfile(graph, false));
        Assert.DoesNotContain("confidential", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidatorRejectsSafeProfileThatRetainsContent()
    {
        using var temp = new TempDirectory();
        var graph = new RecordingGraphBuilder().Build(SyntheticBundleFactory.Create(temp.Path));
        var safe = GraphExport.ApplyProfile(graph, false);
        var nodes = safe.Nodes.Select((node, index) => index == 0
            ? node with { Label = "retained label", Properties = [new("retained", "value")] }
            : node).ToArray();
        var forged = safe with { Nodes = nodes, Metadata = safe.Metadata with { SemanticHash = GraphSemantics.ComputeHash(nodes, safe.Edges) } };

        var report = GraphValidator.Validate(forged);

        Assert.False(report.IsValid);
        Assert.Contains(report.Issues, issue => issue.Code == "graph.safe-profile");
    }

    [Fact]
    public void ValidatorRejectsSafeProfileMetadataAndEdgeTextChannels()
    {
        using var temp = new TempDirectory();
        var graph = new RecordingGraphBuilder().Build(SyntheticBundleFactory.Create(temp.Path));
        var safe = GraphExport.ApplyProfile(graph, false);
        var edges = safe.Edges.Select((edge, index) => index == 0 ? edge with { Kind = "retained text" } : edge).ToArray();
        var forged = safe with
        {
            Edges = edges,
            Metadata = safe.Metadata with { ToolVersion = "retained text", SemanticHash = GraphSemantics.ComputeHash(safe.Nodes, edges) }
        };

        var report = GraphValidator.Validate(forged);

        Assert.False(report.IsValid);
        Assert.Contains(report.Issues, issue => issue.Code == "graph.safe-profile");
        Assert.Contains(report.Issues, issue => issue.Code == "graph.edge-kind");
    }

    [Fact]
    public void ValidatorRejectsForgedSemanticSurfaceLineage()
    {
        using var temp = new TempDirectory();
        var graph = new RecordingGraphBuilder().Build(SyntheticBundleFactory.Create(temp.Path));
        var target = graph.Nodes.First(node => node.Kind == GraphNodeKind.Control && node.Properties.Any(property => property.Name == "layer" && property.Value == "semantic-world"));
        var properties = target.Properties.Select(property => property.Name == "semanticSurfaceId" ? property with { Value = "missing-surface" } : property).ToArray();
        var nodes = graph.Nodes.Select(node => node.Id == target.Id ? node with { Properties = properties } : node).ToArray();
        var forged = graph with { Nodes = nodes, Metadata = graph.Metadata with { SemanticHash = GraphSemantics.ComputeHash(nodes, graph.Edges) } };

        var report = GraphValidator.Validate(forged);

        Assert.False(report.IsValid);
        Assert.Contains(report.Issues, issue => issue.Code is "graph.semantic-surface" or "graph.lineage-surface");
    }

    [Fact]
    public void ValidatorFailsClosedForAmbiguousLayerWithoutThrowing()
    {
        using var temp = new TempDirectory();
        var graph = new RecordingGraphBuilder().Build(SyntheticBundleFactory.Create(temp.Path));
        var target = graph.Nodes.First(node => node.Kind == GraphNodeKind.Surface &&
            node.Properties.Any(property => property.Name == "layer" && property.Value == "raw-world"));
        var nodes = graph.Nodes.Select(node => node.Id == target.Id
            ? node with { Properties = [.. node.Properties, new GraphProperty("layer", "semantic-world")] }
            : node).ToArray();
        var forged = graph with { Nodes = nodes, Metadata = graph.Metadata with { SemanticHash = GraphSemantics.ComputeHash(nodes, graph.Edges) } };

        var report = GraphValidator.Validate(forged);

        Assert.False(report.IsValid);
        Assert.Contains(report.Issues, issue => issue.Code == "graph.layer");
    }

    [Fact]
    public void ValidatorRejectsRawSurfaceOwnerCycle()
    {
        using var temp = new TempDirectory();
        var graph = new RecordingGraphBuilder().Build(SyntheticBundleFactory.Create(temp.Path));
        var surfaces = graph.Nodes.Where(node => node.Kind == GraphNodeKind.Surface && node.Properties.Any(property => property.Name == "layer" && property.Value == "raw-world")).Take(2).ToArray();
        Assert.Equal(2, surfaces.Length);
        var nodes = graph.Nodes.Select(node => node.Id == surfaces[0].Id
                ? node with { Properties = [.. node.Properties.Where(property => property.Name != "ownerRawSurfaceId"), new GraphProperty("ownerRawSurfaceId", surfaces[1].Id)] }
                : node.Id == surfaces[1].Id
                    ? node with { Properties = [.. node.Properties.Where(property => property.Name != "ownerRawSurfaceId"), new GraphProperty("ownerRawSurfaceId", surfaces[0].Id)] }
                    : node).ToArray();
        var forged = graph with { Nodes = nodes, Metadata = graph.Metadata with { SemanticHash = GraphSemantics.ComputeHash(nodes, graph.Edges) } };

        var report = GraphValidator.Validate(forged);

        Assert.False(report.IsValid);
        Assert.Contains(report.Issues, issue => issue.Code == "graph.owner-cycle");
    }

    [Fact]
    public void ValidatorRejectsSemanticOwnerThatDisagreesWithRawSource()
    {
        using var temp = new TempDirectory();
        var graph = new RecordingGraphBuilder().Build(SyntheticBundleFactory.Create(temp.Path));
        var target = graph.Nodes.First(node => node.Kind == GraphNodeKind.Surface && node.Properties.Any(property => property.Name == "semanticSurfaceKind" && property.Value == "PopupVariant"));
        var sourceRaw = target.Properties.Single(property => property.Name == "sourceRawSurfaceId").Value;
        var properties = target.Properties.Select(property => property.Name == "sourceOwnerRawSurfaceId" ? property with { Value = sourceRaw } : property).ToArray();
        var nodes = graph.Nodes.Select(node => node.Id == target.Id ? node with { Properties = properties } : node).ToArray();
        var forged = graph with { Nodes = nodes, Metadata = graph.Metadata with { SemanticHash = GraphSemantics.ComputeHash(nodes, graph.Edges) } };

        var report = GraphValidator.Validate(forged);

        Assert.False(report.IsValid);
        Assert.Contains(report.Issues, issue => issue.Code == "graph.owner-lineage");
    }

    [Fact]
    public void ValidatorReportsNullEdgeEndpointWithoutThrowing()
    {
        var app = new GraphNode("app", GraphNodeKind.Application, "", "app", "app", [new("layer", "shared")], []);
        var edge = new GraphEdge("edge", "contains", null!, app.Id, [], []);
        var graph = new UiKnowledgeGraph(new(FormatVersions.Graph, FormatVersions.Tool, "graph", DateTimeOffset.UnixEpoch, "bundle", new string('0', 64), FormatVersions.FullEvidenceProfile), [app], [edge]);

        var report = GraphValidator.Validate(graph);

        Assert.False(report.IsValid);
        Assert.Contains(report.Issues, issue => issue.Code == "graph.required");
    }

    [Fact]
    public void ValidatorRejectsNullNestedMembers()
    {
        var node = new GraphNode("app", GraphNodeKind.Application, "", "key", "label", [null!], [null!]);
        var hash = GraphSemantics.ComputeHash([node], []);
        var graph = new UiKnowledgeGraph(new(FormatVersions.Graph, FormatVersions.Tool, "g", DateTimeOffset.UnixEpoch, "b", hash, FormatVersions.FullEvidenceProfile), [node], []);
        var report = GraphValidator.Validate(graph);
        Assert.False(report.IsValid);
        Assert.Contains(report.Issues, x => x.Code == "graph.property");
    }

    [Fact]
    public void ValidatorRejectsSpoofedOrNonCanonicalEvidence()
    {
        using var temp = new TempDirectory();
        var graph = new RecordingGraphBuilder().Build(SyntheticBundleFactory.Create(temp.Path, includeScreenshot: true));
        var target = graph.Nodes.First(x => x.Evidence.Count > 0);
        var original = target.Evidence[0];
        foreach (var invalid in new[]
        {
            original with { BundleId = "another-bundle" },
            original with { ObservationEntry = "raw/observations/other.json" },
            original with { ScreenshotEntry = "raw/evidence.png" }
        })
        {
            var nodes = graph.Nodes.Select(x => x.Id == target.Id ? x with { Evidence = [invalid] } : x).ToArray();
            var changed = graph with
            {
                Nodes = nodes,
                Metadata = graph.Metadata with { SemanticHash = GraphSemantics.ComputeHash(nodes, graph.Edges) }
            };
            Assert.Contains(GraphValidator.Validate(changed).Issues, x => x.Code == "graph.evidence");
        }
    }

    [Fact]
    public void JsonLoadRejectsNullNodeIdWithoutEscapingInvalidData()
    {
        using var temp = new TempDirectory();
        var path = System.IO.Path.Combine(temp.Path, "null-id.json");
        var json = """
            {"metadata":{"formatVersion":"uikg/1","toolVersion":"1.0.0","graphId":"g","builtUtc":"2026-01-01T00:00:00Z","sourceBundleId":"b","semanticHash":"0000000000000000000000000000000000000000000000000000000000000000","privacyProfile":"full-evidence/1"},"nodes":[{"id":null,"kind":"Application","parentId":"","stableKey":"k","label":"l","properties":[],"evidence":[]}],"edges":[]}
            """;
        File.WriteAllText(path, json);
        Assert.Throws<InvalidDataException>(() => GraphJsonStore.Load(path));
    }

    [Fact]
    public void JsonLoadRejectsNullNestedCollectionsAsInvalidData()
    {
        using var temp = new TempDirectory();
        var path = System.IO.Path.Combine(temp.Path, "null-properties.json");
        var json = """
            {"metadata":{"formatVersion":"ui-atlas.uikg/2","toolVersion":"1.1.0","graphId":"g","builtUtc":"2026-01-01T00:00:00Z","sourceBundleId":"b","semanticHash":"0000000000000000000000000000000000000000000000000000000000000000","privacyProfile":"full-evidence/1"},"nodes":[{"id":"app","kind":"Application","parentId":"","stableKey":"k","label":"l","properties":null,"evidence":[]}],"edges":[]}
            """;
        File.WriteAllText(path, json);

        Assert.Throws<InvalidDataException>(() => GraphJsonStore.Load(path));
    }

    [Fact]
    public void LegacyJsonRejectsNullPropertyElementsAsInvalidData()
    {
        using var temp = new TempDirectory();
        var path = System.IO.Path.Combine(temp.Path, "legacy-null-property.json");
        var json = """
            {"metadata":{"formatVersion":"ui-atlas.uikg/1","toolVersion":"1.0.0","graphId":"g","builtUtc":"2026-01-01T00:00:00Z","sourceBundleId":"b","semanticHash":"0000000000000000000000000000000000000000000000000000000000000000","privacyProfile":"full-evidence/1"},"nodes":[{"id":"app","kind":"Application","parentId":"","stableKey":"k","label":"l","properties":[null],"evidence":[]}],"edges":[]}
            """;
        File.WriteAllText(path, json);

        Assert.Throws<InvalidDataException>(() => GraphJsonStore.Load(path));
    }

    [Fact]
    public void DiffFindsAddedNodes()
    {
        using var temp = new TempDirectory();
        var graph = new RecordingGraphBuilder().Build(SyntheticBundleFactory.Create(temp.Path));
        var added = graph.Nodes[0] with { Id = "extra" };
        var diff = UiGraphDiff.Compare(graph, graph with { Nodes = [.. graph.Nodes, added] });
        Assert.Equal(["extra"], diff.AddedNodes);
    }

    [Fact]
    public void ValidatorRejectsHierarchyCycle()
    {
        var metadata = new GraphMetadata(FormatVersions.Graph, FormatVersions.Tool, "graph_000000000000000000000000", DateTimeOffset.UnixEpoch, "b", new string('0', 64), FormatVersions.FullEvidenceProfile);
        var a = new GraphNode("node_000000000000000000000001", GraphNodeKind.Window, "node_000000000000000000000002", "a", "a", [], []);
        var b = new GraphNode("node_000000000000000000000002", GraphNodeKind.Surface, a.Id, "b", "b", [], []);
        var report = GraphValidator.Validate(new(metadata, [a, b], []));
        Assert.False(report.IsValid);
        Assert.Contains(report.Issues, x => x.Code == "graph.cycle");
    }

    [Fact]
    public void ExplorerReadModelRejectsGraphsBeyondItsUiBudget()
    {
        using var temp = new TempDirectory();
        var graph = new RecordingGraphBuilder().Build(SyntheticBundleFactory.Create(temp.Path));
        var oversized = graph with { Nodes = Enumerable.Repeat(graph.Nodes[0], ExplorerReadModel.MaxExplorerNodes + 1).ToArray() };
        Assert.Throws<InvalidDataException>(() => new ExplorerReadModel(oversized));
    }
}
