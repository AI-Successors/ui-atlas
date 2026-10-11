using System.Text.Json;
using UiAtlas.Core.Build;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Reader;
using UiAtlas.Core.Recording;
using UiAtlas.Core.Storage;

namespace UiAtlas.Core.Tests;

public sealed class ObservationSceneTests
{
    private static readonly WindowObservation Root = new(1, 1, 7, "Window", "Test", new(0, 0, 800, 600), true, true, false, false, 96);
    private static AutomationObservation Button(string id, long hwnd = 1) =>
        new(id, "", id, id, "Button", "Button", new(20, 20, 80, 24), true, false, "UIA", hwnd);
    private static FrameObservation Frame(long sequence, params AutomationObservation[] controls) =>
        new(sequence, DateTimeOffset.UnixEpoch.AddSeconds(sequence), $"raw/frames/frame-{sequence:D6}.png", Root,
            controls, false, "ok", "test", [Root], ObservedWindowHwnds: [1]);

    [Fact]
    public void PartialAndDeltaRetainKnowledgeWithoutInventingCurrentEvidence()
    {
        var reducer = new ObservationSceneReducer();
        reducer.Reduce(Frame(1, Button("a"), Button("b")));
        var partial = reducer.Reduce(Frame(2, Button("a") with { IsSelected = true }) with { AutomationStatus = "partial" });
        Assert.Equal("partial-observation", partial.Coverage);
        var retained = Assert.Single(partial.RetainedControls);
        Assert.Equal(1, retained.SourceFrameSequence);
        Assert.True(retained.Retained);
        Assert.True(Assert.Single(partial.ObservedControls).Control.IsSelected);
        var delta = reducer.Reduce(Frame(3, Button("a")) with { ObservationScope = "control-delta" });
        Assert.Single(delta.RetainedControls);
        var complete = reducer.Reduce(Frame(4, Button("a")));
        Assert.Empty(complete.RetainedControls);
        Assert.Throws<InvalidOperationException>(() => reducer.Reduce(Frame(3)));
    }

    [Fact]
    public void PopupCannotStealAnUnchangedUnderlyingSubtree()
    {
        var popup = Root with { Hwnd = 2, OwnerHwnd = 1, Bounds = new(20, 20, 200, 200), ClassName = "Popup" };
        var underlying = Button("pane") with { ControlType = "Pane", Bounds = popup.Bounds };
        var reducer = new ObservationSceneReducer();
        reducer.Reduce(Frame(1, underlying));
        var scene = reducer.Reduce(Frame(2, underlying, Button("unknown", 99)) with { ScopedWindows = [Root, popup] });
        Assert.Equal(1, scene.ObservedControls.Single(control => control.Control.RuntimeId == "pane").EffectiveOwnerHwnd);
        Assert.Equal("unresolved", scene.ObservedControls.Single(control => control.Control.RuntimeId == "unknown").OwnershipReason);
        Assert.DoesNotContain(2L, scene.ControlsByWindow.Keys);
    }

    [Fact]
    public void ScreenshotInheritanceRequiresCompatibleSurfaceAndKeepsSourceIdentity()
    {
        var first = Frame(1, Button("a"));
        var delta = Frame(2, Button("a")) with { FrameEntry = "", BaseFrameSequence = 1, ObservationScope = "control-delta" };
        Assert.Same(first, ObservationSceneReducer.ResolveScreenshot(delta, _ => first));
        Assert.Null(ObservationSceneReducer.ResolveScreenshot(delta with { ScopedWindows = [Root with { Bounds = new(0, 0, 900, 600) }] }, _ => first));
        Assert.Null(ObservationSceneReducer.ResolveScreenshot(delta with { ObservedWindowHwnds = [2] }, _ => first));
        Assert.Null(ObservationSceneReducer.ResolveScreenshot(delta with { BaseFrameSequence = 2 }, _ => first));
    }

    [Fact]
    public void StateSurvivesSqliteAndMergedSessionFrameCollisions()
    {
        using var temp = new TempDirectory();
        using var bundle = RecordingBundle.Open(SyntheticBundleFactory.Create(temp.Path));
        var manifest = bundle.ReadJson<RecordingManifest>("manifest.json");
        var original = bundle.ReadJson<FrameObservation>("raw/observations/frame-000001.json");
        var first = original with { Automation = original.Automation.Select(control => control with { IsSelected = true, IsEnabled = true }).ToArray() };
        var second = first with { Sequence = 2, TimestampUtc = first.TimestampUtc.AddSeconds(1),
            Automation = first.Automation.Select(control => control with { IsSelected = false, IsEnabled = false }).ToArray() };
        var other = first with { Automation = first.Automation.Select(control => control with { IsSelected = false }).ToArray() };
        var builder = new RecordingGraphBuilder();
        var graph = builder.Build([new RecordingGraphInput(manifest, [first, second], []),
            new RecordingGraphInput(manifest with { SessionId = "other" }, [other], [])]);
        var path = Path.Combine(temp.Path, "states.db");
        SqliteGraphStore.Save(graph, path);
        var loaded = SqliteGraphStore.Load(path);
        var control = loaded.Nodes.First(node => node.Kind == GraphNodeKind.Control &&
            node.Properties.Any(property => property.Name == "layer" && property.Value == "raw-world") &&
            node.Properties.Any(property => property.Name == "automationId" && property.Value == "save"));
        Assert.Equal("True", UiMapPresentation.ForObservation(control, manifest.SessionId, 1).Properties.Single(property => property.Name == "selected").Value);
        Assert.Equal("False", UiMapPresentation.ForObservation(control, manifest.SessionId, 2).Properties.Single(property => property.Name == "selected").Value);
        Assert.Equal("False", UiMapPresentation.ForObservation(control, "other", 1).Properties.Single(property => property.Name == "selected").Value);
        Assert.Equal(graph.Metadata.SemanticHash, loaded.Metadata.SemanticHash);
        Assert.All(GraphExport.ApplyProfile(loaded, false).Nodes, node => Assert.Null(node.Observations));
        var malformed = loaded with { Nodes = loaded.Nodes.Select(node => node.Id == control.Id ? node with
            { Observations = [control.Observations![0] with { FrameSequence = 999 }] } : node).ToArray() };
        Assert.Contains(GraphValidator.Validate(malformed).Issues, issue => issue.Code == "graph.observation");
    }

    [Fact]
    public void ProviderEvidencePreservesRejectedOwnershipButRedactsEditableTextAndBoundsCount()
    {
        var raw = Button("private", 99) with { ControlType = "Edit", Name = "typed secret", OcrText = "typed secret" };
        var attempt = ProviderEvidence.Create(1, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddSeconds(1), Root, 1,
            Enumerable.Repeat(raw, RecordingContractLimits.MaxProviderControls + 1).ToArray(), false, "ok", 0);
        Assert.Equal("wrong-ownership", attempt.NormalizationStatus);
        Assert.False(attempt.TimedOut);
        Assert.True(attempt.Truncated);
        Assert.Equal(RecordingContractLimits.MaxProviderControls, attempt.Controls.Count);
        Assert.All(attempt.Controls, control => Assert.Equal(99, control.WindowHwnd));
        Assert.DoesNotContain("typed secret", JsonSerializer.Serialize(attempt, JsonDefaults.Options));
        var complete = ProviderEvidence.Create(1, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, Root, 1,
            [Button("normal") with { SupportedPatterns = ["Invoke"] }], false, "ok", 1);
        Assert.False(complete.Truncated);
    }

    [Fact]
    public void RejectedProviderResponseRoundTripsWithoutBecomingAGraphControl()
    {
        using var temp = new TempDirectory();
        var attempt = ProviderEvidence.Create(1, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, Root, 1,
            [Button("rejected-only", 99)], false, "ok", 0);
        var path = SyntheticBundleFactory.Create(temp.Path, providerObservation: attempt);
        Assert.True(RecordingBundleValidator.Validate(path).IsValid);
        using var reader = UiEvidenceReader.Open(path);
        Assert.Equal("rejected-only", Assert.Single(Assert.Single(reader.ReadProviderObservations("golden")).Controls).RuntimeId);
        var graph = new RecordingGraphBuilder().Build(path);
        Assert.DoesNotContain(graph.Nodes, node => node.Observations?.Any(value => value.Control.RuntimeId == "rejected-only") == true);
        var invalid = SyntheticBundleFactory.Create(temp.Path, "invalid.mlrec", providerObservation: attempt with { AcceptedControlCount = 2 });
        Assert.Contains(RecordingBundleValidator.Validate(invalid).Issues, issue => issue.Code == "bundle.provider");
    }

    [Fact]
    public void LateClosedWindowEvidenceRemainsRawAndCannotBePromoted()
    {
        using var temp = new TempDirectory();
        using var bundle = RecordingBundle.Open(SyntheticBundleFactory.Create(temp.Path));
        var manifest = bundle.ReadJson<RecordingManifest>("manifest.json");
        var frame = bundle.ReadJson<FrameObservation>("raw/observations/frame-000001.json");
        var raw = frame with { Automation = frame.Automation.Concat([Button("closed-window", 99)]).ToArray() };
        var input = new RecordingGraphInput(manifest, [frame], [], [raw]);
        var graph = new RecordingGraphBuilder().Build(input);
        var unresolved = Assert.Single(graph.Nodes, node => node.Observations?.Any(value => value.Control.RuntimeId == "closed-window") == true);
        Assert.Contains(unresolved.Properties, property => property.Name == "layer" && property.Value == "raw-data-streams");
        Assert.Equal(0, Assert.Single(unresolved.Observations!).EffectiveOwnerHwnd);
        Assert.False(RecordingGraphInputValidator.Validate(input with { Observations = [raw], RecordedObservations = null }).IsValid);
        Assert.True(GraphValidator.Validate(graph).IsValid);
    }

    [Fact]
    public void PopupObservationsRemainBrowsableInBothWorlds()
    {
        using var temp = new TempDirectory();
        var graph = new RecordingGraphBuilder().Build(SyntheticBundleFactory.Create(temp.Path, popupDelta: true));
        var model = new UiMappingReadModel(graph);
        foreach (var level in new[] { UiUnderstandingLevel.RawWorld, UiUnderstandingLevel.SemanticWorld })
            Assert.All(model.LayerFor(level).Surfaces.Where(surface => surface.SurfaceKind.Contains("Popup") && surface.Evidence.Count > 0),
                surface =>
                {
                    Assert.NotEmpty(surface.Variants);
                    Assert.All(model.VariantsFor([surface]), variant => Assert.Equal("popup-delta", variant.ObservationScope));
                });
    }
}
