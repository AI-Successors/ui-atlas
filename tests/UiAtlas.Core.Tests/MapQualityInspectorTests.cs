using UiAtlas.Core.Cli;
using UiAtlas.Core.Contracts;

namespace UiAtlas.Core.Tests;

public sealed class MapQualityInspectorTests
{
    [Fact]
    public void ReportsDeduplicatedScreensControlsAndTableCells()
    {
        var graph = Graph(includeSemanticControls: true);
        var frames = new[]
        {
            new MapQualityFrame(Frame(1, "raw/frames/frame-000001.png"), "same-pixels"),
            new MapQualityFrame(Frame(2, "raw/frames/frame-000002.png"), "same-pixels")
        };
        var interaction = new InteractionObservation(
            "interaction-1", "operation-1", 1, 1, InteractionActor.User,
            InteractionGestureKind.Click, InteractionActionKind.Invoke, 1, null, [], [2],
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, InteractionOutcome.Succeeded);

        var report = MapQualityInspector.Evaluate(
            graph, new MapQualityEvidence(frames, [interaction], []));

        Assert.False(report.NeedsReview);
        Assert.Equal(2, report.ScreenCount);
        Assert.Equal(2, report.SemanticControlCount);
        Assert.Equal(1, report.TableCellCount);
        Assert.Equal(1, report.DuplicateScreenshotCount);
        Assert.Contains("Every recorded interaction has a result screen", report.UserSummary());
    }

    [Fact]
    public void ExplainsEveryActionableCaptureGap()
    {
        var partialFrame = Frame(1, string.Empty) with
        {
            AutomationTimedOut = true,
            AutomationStatus = "timeout"
        };
        var failedInteraction = new InteractionObservation(
            "interaction-1", "operation-1", 1, 1, InteractionActor.User,
            InteractionGestureKind.Click, InteractionActionKind.Unknown, 1, null, [], [],
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, InteractionOutcome.Unobserved);
        var health = new CaptureHealthEvent(
            DateTimeOffset.UtcNow, "adaptive", "root-change-missed", "test", true);

        var report = MapQualityInspector.Evaluate(
            Graph(includeSemanticControls: true),
            new MapQualityEvidence([new(partialFrame, string.Empty)], [failedInteraction], [health]));

        Assert.True(report.NeedsReview);
        Assert.Equal(1, report.InteractionWithoutResultCount);
        Assert.Equal(1, report.MissingScreenshotCount);
        Assert.Equal(1, report.PartialFrameCount);
        Assert.Equal(1, report.CriticalCaptureIssueCount);
        Assert.Contains(report.ReviewReasons, reason => reason.Contains("without a confirmed result screen", StringComparison.Ordinal));
        Assert.Contains(report.ReviewReasons, reason => reason.Contains("without pixels", StringComparison.Ordinal));
    }

    private static UiKnowledgeGraph Graph(bool includeSemanticControls)
    {
        var nodes = new List<GraphNode>
        {
            Node("state-1", GraphNodeKind.State, ("layer", "raw-world")),
            Node("state-2", GraphNodeKind.State, ("layer", "raw-world")),
            Node("surface-1", GraphNodeKind.Surface,
                ("layer", "semantic-world"), ("semanticSurfaceKind", "Window"))
        };
        if (includeSemanticControls)
        {
            nodes.Add(Node("control-1", GraphNodeKind.Control,
                ("layer", "semantic-world"), ("tableRow", "0"), ("tableColumn", "0")));
            nodes.Add(Node("control-2", GraphNodeKind.Control, ("layer", "semantic-world")));
        }

        return new UiKnowledgeGraph(
            new GraphMetadata("uikg/4", "test", "graph", DateTimeOffset.UtcNow,
                "bundle", "hash", "full-evidence/1"),
            nodes,
            []);
    }

    private static GraphNode Node(
        string id,
        GraphNodeKind kind,
        params (string Name, string Value)[] properties) =>
        new(id, kind, string.Empty, id, id,
            properties.Select(property => new GraphProperty(property.Name, property.Value)).ToArray(),
            kind == GraphNodeKind.State
                ? [new EvidenceRef("bundle", id.EndsWith('1') ? 1 : 2, id + ".json", null, id + ".png")]
                : []);

    private static FrameObservation Frame(long sequence, string screenshot) =>
        new(
            sequence,
            DateTimeOffset.UtcNow,
            screenshot,
            new WindowObservation(1, 1, 1, "Window", "Window", new RectI(0, 0, 800, 600),
                true, true, false, false, 96),
            [new AutomationObservation("1", string.Empty, "root", "Root", "Pane", "Pane",
                new RectI(0, 0, 800, 600), true, false, "Synthetic", 1)],
            false,
            "ok",
            "adaptive-root-change",
            ObservationScope: "full-root");
}
