using UiAtlas.Core.Recording.Windows;

namespace UiAtlas.Core.Windows.Tests;

public sealed class ManualRecordingSessionMarkerTests
{
    [Fact]
    public void LongAutoMarkerIsBoundedAndDeterministic()
    {
        var marker = "auto-tabs:command:mapped:" + new string('x', 600);

        var first = ManualRecordingSession.NormalizeMarkerText(marker);
        var second = ManualRecordingSession.NormalizeMarkerText(marker);

        Assert.Equal(256, first.Length);
        Assert.Equal(first, second);
        Assert.StartsWith("auto-tabs:command:mapped:", first, StringComparison.Ordinal);
        Assert.Contains("~#", first, StringComparison.Ordinal);
    }

    [Fact]
    public void ShortMarkerIsUnchanged()
    {
        const string marker = "manual-mode:armed";

        Assert.Equal(marker, ManualRecordingSession.NormalizeMarkerText(marker));
    }
}
