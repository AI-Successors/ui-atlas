using UiAtlas.Core.Build;
using UiAtlas.Core.Contracts;

namespace UiAtlas.Core.Tests;

public sealed class RecordingHierarchyRegressionTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 7, 0, 0, 0, TimeSpan.Zero);
    private static readonly WindowObservation Window = new(1, 1, 7, "RibbonHost", "Editor",
        new(0, 0, 1200, 800), true, true, false, false, 96);

    [Fact]
    public void RibbonSwitchDialogAndReturnKeepFontUnderHomeWithoutInventingDrawEvidence()
    {
        var root = Control("root", "", "Editor", "Window", "RibbonHost", Window.Bounds);
        var draw = Control("draw", "root", "Draw", "Group", "RibbonPage", new(0, 100, 1200, 100));
        var add = Control("add", "draw", "Add", "MenuItem", "RibbonItem", new(40, 120, 60, 30));
        var home = draw with { RuntimeId = "home", Name = "Home" };
        var font = Control("font", "home", "Font", "Group", "RibbonChunk", new(200, 110, 300, 80));
        var dialog = Window with { Hwnd = 2, OwnerHwnd = 1, ClassName = "#32770", Title = "Styles", Bounds = new(300, 250, 400, 300) };
        var dialogRoot = Control("dialog", "", "Styles", "Window", "#32770", dialog.Bounds) with { WindowHwnd = 2 };
        var edit = Control("edit", "dialog", "Name", "Edit", "Edit", new(320, 290, 180, 30)) with { WindowHwnd = 2 };
        var first = Frame(8, [root, draw, add]);
        var delta = Frame(11, [root, home, font], "control-delta");
        var styles = Frame(18, [dialogRoot, edit]) with
        {
            ScopedWindows = [Window, dialog], ObservedWindowHwnds = [2], Trigger = "adaptive-dialog:Styles"
        };
        var last = Frame(20, [root, home, font], "control-delta");

        var graph = Build(first, delta, styles, last);

        Assert.True(GraphValidator.Validate(graph).IsValid);
        var fontNode = Assert.Single(RawControls(graph), node => node.Label == "Font");
        Assert.Equal("Home", graph.Nodes.Single(node => node.Id == fontNode.ParentId).Label);
        Assert.DoesNotContain(graph.Nodes, node => node.Label == "Draw" &&
            node.Evidence.Any(evidence => evidence.FrameSequence == 11));
        Assert.Equal([8L], Assert.Single(RawControls(graph), node => node.Label == "Draw").Evidence.Select(e => e.FrameSequence));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DuplicateParentDisappearanceDoesNotChangeSurvivingChildIdentity(bool reverse)
    {
        var root = Control("root", "", "Editor", "Window", "RibbonHost", Window.Bounds);
        var first = Control("a", "root", "Region", "Group", "Panel", new(20, 100, 300, 100));
        var second = first with { RuntimeId = "b" };
        var child = Control("child", "b", "Action", "Button", "Button", new(40, 120, 80, 25), "action");
        AutomationObservation[] initial = reverse ? [root, second, first, child] : [root, first, second, child];

        var graph = Build(Frame(1, initial), Frame(2, [root, second, child]));

        var action = Assert.Single(RawControls(graph), node => node.Label == "Action");
        Assert.Equal([1L, 2L], action.Evidence.Select(evidence => evidence.FrameSequence));
        Assert.Equal(2, RawControls(graph).Count(node => node.Label == "Region"));
        Assert.Contains(graph.Nodes.Single(node => node.Id == action.ParentId).Evidence, evidence => evidence.FrameSequence == 2);
        var reordered = Build(Frame(1, initial.Reverse().ToArray()), Frame(2, [child, second, root]));
        Assert.Equal(action.Id, Assert.Single(RawControls(reordered), node => node.Label == "Action").Id);
    }

    [Fact]
    public void RetainedToolbarControlKeepsSourceEvidenceAndIsNotObservedInCurrentState()
    {
        var root = Control("root", "", "Editor", "Window", "RibbonHost", Window.Bounds);
        var button = Control("button", "root", "Reservations", "Button", "TAbacreButton", new(20, 100, 100, 40), "reservations");
        var delta = Frame(2, [root], "control-delta");

        var graph = Build(Frame(1, [root, button]), delta);

        var saved = Assert.Single(RawControls(graph), node => node.Label == "Reservations");
        Assert.Equal([1L], saved.Evidence.Select(evidence => evidence.FrameSequence));
        Assert.DoesNotContain(graph.Nodes, node => node.Label == "Reservations" && Layer(node) == "raw-data-streams" &&
            node.Evidence.Any(evidence => evidence.FrameSequence == 2));
        var current = Assert.Single(graph.Nodes, node => node.Kind == GraphNodeKind.State &&
            node.Evidence.Any(evidence => evidence.FrameSequence == 2));
        Assert.DoesNotContain(graph.Edges, edge => edge.Kind == "contains" && edge.FromId == current.Id && edge.ToId == saved.Id);
    }

    [Fact]
    public void RawStreamsPreserveCapturedCaptionDuplicatesThatCurationRemoves()
    {
        var first = Control("first", "", "Close", "Button", "NetUIAppFrameHelper", new(1150, 0, 40, 40), "Close");
        var cached = first with { RuntimeId = "cached", FrameworkId = "UiAtlas.Cached", Bounds = new(1155, 4, 30, 20) };

        var graph = Build(Frame(1, [first, cached]));

        Assert.Equal(2, graph.Nodes.Count(node => node.Label == "Close" && Layer(node) == "raw-data-streams"));
        Assert.Single(RawControls(graph), node => node.Label == "Close");
    }

    [Theory]
    [InlineData("ok", "full-root", false)]
    [InlineData("ok", "full-root", true)]
    [InlineData("node-limit", "full-root", false)]
    [InlineData("ok", "control-delta", false)]
    public void CacheResetRequiresCompleteObservationOfTheSameWindow(string status, string scope, bool dialogOnly)
    {
        var root = Control("root", "", "Editor", "Window", "RibbonHost", Window.Bounds);
        var button = Control("button", "root", "Reservations", "Button", "TAbacreButton", new(20, 100, 100, 40), "reservations");
        var reset = Frame(2, [root], scope) with { AutomationStatus = status };
        if (dialogOnly)
        {
            var dialog = Window with { Hwnd = 2, OwnerHwnd = 1, ClassName = "#32770", Bounds = new(300, 250, 400, 300) };
            reset = reset with { ScopedWindows = [Window, dialog], ObservedWindowHwnds = [2],
                Automation = [Control("dialog", "", "Styles", "Window", "#32770", dialog.Bounds) with { WindowHwnd = 2 }] };
        }
        var graph = Build(Frame(1, [root, button]), reset, Frame(3, [root], "control-delta"));
        var current = Assert.Single(graph.Nodes, node => node.Kind == GraphNodeKind.State &&
            node.Evidence.Any(evidence => evidence.FrameSequence == 3));
        var shouldRetain = dialogOnly || status != "ok" || scope != "full-root";
        Assert.Contains(current.Properties, property => property.Name == "retainedControlCount" &&
            property.Value == (shouldRetain ? "1" : "0"));
        Assert.DoesNotContain(RawControls(graph).Single(node => node.Label == "Reservations").Evidence,
            evidence => evidence.FrameSequence != 1);
    }

    [Fact]
    public void EnrichmentDoesNotReplaceOriginalRawStreamEvidence()
    {
        var captured = Control("captured", "", "Captured", "Button", "Button", new(40, 120, 80, 25));
        var derived = captured with { RuntimeId = "derived", Name = "Derived" };
        var input = Input(Frame(1, [derived])) with { RecordedObservations = [Frame(1, [captured])] };
        var graph = new RecordingGraphBuilder().Build(input);
        Assert.Single(graph.Nodes, node => node.Label == "Captured" && Layer(node) == "raw-data-streams");
        Assert.DoesNotContain(graph.Nodes, node => node.Label == "Derived" && Layer(node) == "raw-data-streams");
        Assert.Single(RawControls(graph), node => node.Label == "Derived");
        Assert.False(RecordingGraphInputValidator.Validate(input with { RecordedObservations = [Frame(2, [captured])] }).IsValid);
    }

    [Fact]
    public void MergedSessionsKeepSeparateRawStreamPackages()
    {
        var first = Input(Frame(1, [Control("a", "", "First", "Button", "Button", new(40, 120, 80, 25))]));
        var second = first with { Manifest = first.Manifest with { SessionId = "second-session" } };
        var graph = new RecordingGraphBuilder().Build([first, second]);
        var captured = graph.Nodes.Where(node => node.Kind == GraphNodeKind.Control && Layer(node) == "raw-data-streams").ToArray();
        Assert.Equal(2, captured.Length);
        Assert.All(captured, node => Assert.Single(node.Evidence));
        Assert.Single(RawControls(graph));
    }

    [Fact]
    public void RetentionCannotSupplyMissingInteractionTargetEvidence()
    {
        var root = Control("root", "", "Editor", "Window", "RibbonHost", Window.Bounds);
        var button = Control("button", "root", "Reservations", "Button", "TAbacreButton", new(20, 100, 100, 40), "reservations");
        var input = Input(Frame(1, [root, button]), Frame(2, [root], "control-delta"), Frame(3, [root]));
        var interaction = new InteractionObservation("interaction", "operation", 1, 1, InteractionActor.User,
            InteractionGestureKind.Click, InteractionActionKind.Invoke, 2, button, [], [3], Start, Start.AddSeconds(3), InteractionOutcome.Succeeded);
        var graph = new RecordingGraphBuilder().Build(input with { Interactions = [interaction] });
        var target = Assert.Single(RawControls(graph), node => node.Label == "Reservations");
        Assert.DoesNotContain(target.Properties, property => property is { Name: "verificationStatus", Value: "Confirmed" });
        Assert.DoesNotContain(graph.Edges, edge => edge.Properties.Any(property => property.Name == "interactionId" && property.Value == "interaction"));
    }

    [Fact]
    public void DuplicateRuntimeParentsDoNotSilentlyChooseTheFirstParent()
    {
        var first = Control("duplicate", "", "Region", "Group", "Panel", new(20, 100, 300, 100));
        var second = first with { Bounds = new(350, 100, 300, 100) };
        var child = Control("child", "duplicate", "Action", "Button", "Button", new(40, 120, 80, 25));
        var graph = Build(Frame(1, [first, second, child]));
        var action = Assert.Single(RawControls(graph), node => node.Label == "Action");
        Assert.Contains(action.Properties, property => property is { Name: "hierarchyStatus", Value: "ambiguous-parent" });
        Assert.Equal(GraphNodeKind.Surface, graph.Nodes.Single(node => node.Id == action.ParentId).Kind);
    }

    private static UiKnowledgeGraph Build(params FrameObservation[] frames) => new RecordingGraphBuilder().Build(Input(frames));

    private static RecordingGraphInput Input(params FrameObservation[] frames)
    {
        var manifest = new RecordingManifest(FormatVersions.RecordingBundle, FormatVersions.Tool, "synthetic-ribbon-hierarchy",
            Start, Start.AddMinutes(1), RecordingOutcome.Complete,
            new(1, 1, 7, "Editor", Start.AddHours(-1)), new(), new(), true, 0, 30);
        return new(manifest, frames, []);
    }

    private static FrameObservation Frame(long sequence, IReadOnlyList<AutomationObservation> controls, string scope = "full-root") =>
        new(sequence, Start.AddSeconds(sequence), "", Window, controls, false, "ok", "adaptive-control", [Window],
            ObservationScope: scope, ObservedWindowHwnds: [1]);

    private static AutomationObservation Control(string runtime, string parent, string name, string type, string className, RectI bounds, string aid = "") =>
        new(runtime, parent, aid, name, "ControlType." + type, className, bounds, true, false, "Win32", 1);

    private static IEnumerable<GraphNode> RawControls(UiKnowledgeGraph graph) =>
        graph.Nodes.Where(node => node.Kind == GraphNodeKind.Control && Layer(node) == "raw-world");

    private static string? Layer(GraphNode node) => node.Properties.FirstOrDefault(property => property.Name == "layer")?.Value;
}
