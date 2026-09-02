using UiAtlas.Core.Cli;
using UiAtlas.Core.Contracts;

namespace UiAtlas.Core.Tests;

public sealed class RecordingHighlightHistoryTests
{
    [Fact]
    public void RestoresOnlyTheUsersRecordedClick()
    {
        using var temp = new TempDirectory();
        var recording = SyntheticBundleFactory.Create(temp.Path, manualPointerUp: true);

        var highlights = RecordingHighlightHistory.Load([recording]);

        var highlight = Assert.Single(highlights);
        Assert.Equal(new RectI(15, 15, 120, 36), highlight.Bounds);
    }

    [Fact]
    public void RestoresSuccessfulAutomaticRecorderClicks()
    {
        using var temp = new TempDirectory();
        var recording = SyntheticBundleFactory.Create(
            temp.Path,
            markers:
            [
                "auto-tabs:command:tab-home:command-1:target:10,20,30,40",
                "auto-tabs:command:tab-home:command-1:opened"
            ]);

        var highlights = RecordingHighlightHistory.Load([recording]);

        var highlight = Assert.Single(highlights);
        Assert.Equal(new RectI(10, 20, 30, 40), highlight.Bounds);
    }

    [Fact]
    public void DoesNotRestoreAnAutomaticTargetThatWasNotOpened()
    {
        using var temp = new TempDirectory();
        var recording = SyntheticBundleFactory.Create(
            temp.Path,
            markers:
            [
                "auto-tabs:command:tab-home:command-1:target:10,20,30,40",
                "auto-tabs:command:skipped:tab-home:command-1"
            ]);

        var highlights = RecordingHighlightHistory.Load([recording]);

        Assert.Empty(highlights);
    }

    [Fact]
    public void RecognizesOnlyRecorderControlPanelWindows()
    {
        Assert.True(RecordingPanelCoordinator.IsRecorderWindowTitle("UiAtlas recording - excel"));
        Assert.False(RecordingPanelCoordinator.IsRecorderWindowTitle("UiAtlas mapped controls overlay"));
        Assert.False(RecordingPanelCoordinator.IsRecorderWindowTitle("UiAtlas Core — UI Knowledge Graph Editor"));
    }
}
