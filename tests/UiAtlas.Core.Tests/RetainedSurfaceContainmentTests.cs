using UiAtlas.Core.Build;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Reader;
using UiAtlas.Core.Recording;
using UiAtlas.Core.Storage;

namespace UiAtlas.Core.Tests;

public sealed class RetainedSurfaceContainmentTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void RetainedControlsStayOnTheirOriginalSurface(bool dialog, bool merged)
    {
        using var temp = new TempDirectory();
        var now = DateTimeOffset.UnixEpoch;
        var manifest = new RecordingManifest(FormatVersions.RecordingBundle, FormatVersions.Tool, "first",
            now, now.AddSeconds(2), RecordingOutcome.Partial,
            new TargetScope(1, 1, 7, "Fixture", now.AddHours(-1)), new(), new(), true, 0, 2);
        var root = new WindowObservation(1, 1, 7, "Main", "Main", new(0, 0, 800, 600),
            true, true, false, false, 96, Style: 0x00C00000);
        var popup = new WindowObservation(2, 1, 7, dialog ? "#32770" : "Net UI Tool Window", "Options",
            new(100, 100, 300, 300), true, true, false, false, 96, OwnerHwnd: 1,
            IsToolWindow: !dialog);
        var anchor = new AutomationObservation("popup", "", "popup", "Options", "Window", popup.ClassName,
            popup.Bounds, true, false, "Win32", 2);
        var button = new AutomationObservation("button", "popup", "button", "Choose", "Button", "Button",
            new(110, 110, 80, 24), true, false, "Win32", 2);
        var pane = new AutomationObservation("old-pane", "popup", "old-pane", "", "Pane", "MSAA.Role10",
            new(110, 150, 250, 200), true, false, "Win32", 2);
        var first = new FrameObservation(1, now, "", root, [anchor, button, pane], false, "ok", "manual-action",
            [root, popup]);
        // A partial response leaves the HWND, native bounds, and root intact, but
        // omits an old child. Popup content identity changes; a dialog shell does not.
        var second = first with { Sequence = 2, TimestampUtc = now.AddSeconds(1),
            Automation = [anchor with { }, button with { IsSelected = true }], AutomationStatus = "node-limit" };
        var input = new RecordingGraphInput(manifest, [first, second], []);
        var inputs = merged ? new[] { input, input with { Manifest = manifest with { SessionId = "second" } } } : [input];

        var graph = new RecordingGraphBuilder().Build(inputs);
        Assert.True(GraphValidator.Validate(graph).IsValid);
        var path = Path.Combine(temp.Path, "retained.db");
        SqliteGraphStore.Save(graph, path);
        var loaded = SqliteGraphStore.Load(path);
        Assert.Equal(graph.Metadata.SemanticHash, loaded.Metadata.SemanticHash);
        var model = new UiMappingReadModel(loaded);
        var raw = model.LayerFor(UiUnderstandingLevel.RawWorld);
        var surfaces = raw.Surfaces.Where(surface => UiMappingReadModel.Property(surface.Source, "className") == popup.ClassName).ToArray();
        Assert.Equal(dialog ? 1 : 2, surfaces.Length);
        foreach (var session in inputs.Select(item => item.Manifest.SessionId))
        {
            var current = Assert.Single(surfaces, surface => surface.Variants.Any(variant => variant.BundleId == session && variant.FrameSequence == 2));
            var controls = raw.ControlsForSurface(current.Id, 2, session);
            Assert.Contains(controls, control => control.Source.Observations?.Any(observation => observation.Control.RuntimeId == "button") == true);
            Assert.Equal(dialog, controls.Any(control => control.Source.Observations?.Any(observation => observation.Control.RuntimeId == "old-pane") == true));
        }
        var oldPanes = loaded.Nodes.Where(node => node.Kind == GraphNodeKind.Control &&
            UiMappingReadModel.Property(node, "layer") == "raw-world" &&
            node.Observations?.Any(observation => observation.Control.RuntimeId == "old-pane") == true).ToArray();
        Assert.NotEmpty(oldPanes);
        Assert.All(oldPanes.SelectMany(node => node.Observations!), observation => Assert.Equal(1, observation.FrameSequence));
        Assert.All(oldPanes.SelectMany(node => node.Evidence), evidence => Assert.Equal(1, evidence.FrameSequence));
        Assert.DoesNotContain(loaded.Nodes, node => UiMappingReadModel.Property(node, "layer") == "raw-data-streams" &&
            node.Observations?.Any(observation => observation.FrameSequence == 2 && observation.Control.RuntimeId == "old-pane") == true);
        // Keep the validator strict: a state must never claim a foreign surface's control.
        foreach (var state in loaded.Nodes.Where(node => node.Kind == GraphNodeKind.State))
            foreach (var edge in loaded.Edges.Where(edge => edge.Kind == "contains" && edge.FromId == state.Id))
                Assert.Equal(state.ParentId, UiMappingReadModel.Property(loaded.Nodes.Single(node => node.Id == edge.ToId), "rawSurfaceId"));
    }
}
