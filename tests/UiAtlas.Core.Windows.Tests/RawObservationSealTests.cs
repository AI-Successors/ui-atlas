using UiAtlas.Core.Contracts;
using UiAtlas.Core.Recording.Windows;

namespace UiAtlas.Core.Windows.Tests;

public sealed class RawObservationSealTests
{
    [Fact]
    public void SealedCollectorRejectsLateProviderResultsAndPreservesTargetAndQueueTime()
    {
        using var collector = new RawObservationCollector(() => { });
        var queued = DateTimeOffset.UtcNow.AddSeconds(-2);
        var started = queued.AddSeconds(1);
        collector.Add("uia.raw/0xAB", started, DateTimeOffset.UtcNow, "timeout", [], queued);
        var sealedStreams = collector.Seal();
        collector.Add("uia.raw/0xAB", started, DateTimeOffset.UtcNow, "ok", []);
        Assert.Single(collector.Snapshot());
        var stream = Assert.Single(sealedStreams);
        Assert.Equal(171, stream.TargetWindowHwnd);
        Assert.Equal(queued, stream.QueuedUtc);
        Assert.Equal("timeout", stream.Status);
        Assert.Equal("native-automation", stream.EvidenceKind);
    }

    [Fact]
    public void StabilityDetectsMovementDpiPopupAndVisibilityButNotEnumerationOrder()
    {
        var root = new WindowObservation(1, 1, 1, "Window", "Title", new(0, 0, 500, 400), true, true, false, false, 96);
        var popup = root with { Hwnd = 2, ClassName = "Popup" };
        Assert.True(ManualRecordingSession.SameNativeScope([root, popup], [popup, root]));
        Assert.False(ManualRecordingSession.SameNativeScope([root], [root, popup]));
        Assert.False(ManualRecordingSession.SameNativeScope([root], [root with { Dpi = 144 }]));
        Assert.False(ManualRecordingSession.SameNativeScope([root], [root with { Bounds = root.Bounds with { X = 25 } }]));
        Assert.False(ManualRecordingSession.SameNativeScope([root], [root with { IsVisible = false }]));
    }
}
