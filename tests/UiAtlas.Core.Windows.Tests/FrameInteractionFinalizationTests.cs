using System.IO;
using System.Text.Json;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Recording;
using UiAtlas.Core.Recording.Windows;

namespace UiAtlas.Core.Windows.Tests;

public sealed class FrameInteractionFinalizationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SealedCaptureKeepsFramesAndOnlyCommittedCausalReferences(bool completed)
    {
        var directory = Path.Combine(Path.GetTempPath(), "ui-atlas-interaction-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var window = new WindowObservation(1, 1, 7, "Window", "Test", new(0, 0, 800, 600), true, true, false, false, 96);
            var control = new AutomationObservation("button", "", "Open", "open", "Button", "Button", new(10, 10, 90, 24), true, false, "UIA", 1);
            var frame = new FrameObservation(1, now, "", window, [control], false, "ok", "adaptive-popup",
                [window], InteractionId: "reserved-click");
            var frames = new List<FrameObservation> { frame };
            var interactions = completed ? new[] { new InteractionObservation("reserved-click", "manual", 1, 1,
                InteractionActor.User, InteractionGestureKind.Click, InteractionActionKind.Invoke, 1, control, [], [],
                now, now, InteractionOutcome.Unobserved, "captured") } : [];
            var health = new List<CaptureHealthEvent>();
            var jsonLines = new JsonSerializerOptions(JsonDefaults.Options) { WriteIndented = false };
            var path = Path.Combine(directory, "sealed.mlrec");
            using (var writer = new RecordingBundleWriter(Path.Combine(directory, "staging")))
            {
                writer.WriteJson("raw/observations/frame-000001.json", frame);
                ManualRecordingSession.FinalizeFrameInteractionReferences(writer, frames, interactions, health);
                writer.WriteText("raw/input-events.jsonl", "");
                writer.WriteText("raw/interactions.jsonl", string.Join('\n', interactions.Select(item => JsonSerializer.Serialize(item, jsonLines))));
                writer.WriteText("raw/capture-health.jsonl", string.Join('\n', health.Select(item => JsonSerializer.Serialize(item, jsonLines))));
                writer.WriteJson("derived/statebook.json", new DerivedStatebook("statebook/1", [1], []));
                writer.WriteJson("manifest.json", new RecordingManifest(FormatVersions.RecordingBundle, FormatVersions.Tool, "sealed", now, now,
                    RecordingOutcome.Partial, new(1, 1, 7, "Test", now.AddHours(-1)), new(ScreenshotsRetained: false), new(), true, 0, 1,
                    Files: writer.DescribeEntries()));
                writer.Complete(path);
            }
            var report = RecordingBundleValidator.Validate(path);
            Assert.True(report.IsValid, string.Join("; ", report.Issues.Select(issue => issue.Message)));
            using var bundle = RecordingBundle.Open(path);
            var reopened = bundle.ReadJson<FrameObservation>("raw/observations/frame-000001.json");
            Assert.Equal(frame.Automation, reopened.Automation);
            Assert.Equal(frame.TimestampUtc, reopened.TimestampUtc);
            Assert.Equal(completed ? frame.InteractionId : null, reopened.InteractionId);
            Assert.Equal(completed ? 0 : 1, health.Count);
            Assert.Equal(completed ? 1 : 0, interactions.Length);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
