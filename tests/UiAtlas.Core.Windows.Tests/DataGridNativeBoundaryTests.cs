using System.Diagnostics;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Recording.Windows;

namespace UiAtlas.Core.Windows.Tests;

// Uses child processes and synthetic JSON only; never touches the desktop.
public sealed class DataGridNativeBoundaryTests
{
    [Fact]
    public async Task TypedWorkerDistinguishesUnsupportedFromKnownEmpty()
    {
        string? mode = null;
        long scope = 0;
        var client = new UiaWorkerClient((_, hwnd, _, collectionMode) =>
        {
            mode = collectionMode;
            scope = hwnd;
            return Child("[Console]::Out.Write('{\"capabilities\":{\"containerGridSupported\":false,\"containerTableSupported\":false,\"rowCount\":null,\"columnCount\":null,\"inspectedDescendants\":0,\"gridItemSupported\":false,\"tableItemSupported\":false,\"valueSupported\":false,\"legacySupported\":false,\"textSupported\":false,\"probeCompleted\":true,\"reason\":null},\"containerBounds\":null,\"columnHeaderBounds\":[]}')");
        });
        var probe = await client.ProbeGridAsync(Target(), 22, TimeSpan.FromSeconds(15), CancellationToken.None);
        Assert.Equal("datagrid-probe", mode);
        Assert.Equal(22, scope);
        Assert.True(probe.Capabilities.ProbeCompleted);
        Assert.False(probe.Capabilities.ContainerGridSupported);
        Assert.Null(probe.Capabilities.RowCount);
    }

    [Fact]
    public async Task HungTypedWorkerIsKilledWithoutReturningAnEmptyTable()
    {
        var client = new UiaWorkerClient((_, _, _, _) => Child("[Threading.Thread]::Sleep(30000)"));
        var timer = Stopwatch.StartNew();
        var probe = await client.ProbeGridAsync(Target(), 22, TimeSpan.FromMilliseconds(250), CancellationToken.None);
        Assert.False(probe.Capabilities.ProbeCompleted);
        Assert.Null(probe.Capabilities.RowCount);
        Assert.Equal("native-probe-timeout", probe.Capabilities.Reason);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task OversizeTypedWorkerOutputIsRejected()
    {
        var client = new UiaWorkerClient((_, _, _, _) => Child("[Console]::Out.Write(('x' * 70000))"));
        // Test the output quota, independently of PowerShell startup under a parallel build/test load.
        // The separate hung-worker test keeps the production deadline behavior covered.
        var probe = await client.ProbeGridAsync(Target(), 22, TimeSpan.FromSeconds(15), CancellationToken.None);
        Assert.False(probe.Capabilities.ProbeCompleted);
        Assert.Equal("response-limit", probe.Capabilities.Reason);
    }

    [Fact]
    public void UnsafeOrOverlappingRegionsAreRejected()
    {
        var regions = new GridRegions(100, 80, new(0, 0, 100, 80), new(0, 0, 100, 20), new(0, 10, 100, 70), new(0, 0, 100, 80));
        Assert.Throws<InvalidOperationException>(() => DataGridTargetBinding.ValidateRegions(regions, new(20, 30, 100, 80)));
        Assert.Throws<InvalidOperationException>(() => DataGridTargetBinding.ValidateRegions(regions with { Body = new(0, 20, 100, 60) }, new(20, 30, 101, 80)));
    }

    [Fact]
    public void EmptyCaptionIsExactSoSiblingOrdinalMatchesLocatorDescription()
    {
        var segment = new GridLocatorSegment("TAbacrePanel", Name: "", SiblingOrdinal: 1);
        Assert.True(DataGridTargetBinding.MatchesNativeSegment(segment, "TAbacrePanel", "", 0));
        Assert.False(DataGridTargetBinding.MatchesNativeSegment(segment, "TAbacrePanel", "Orders", 0));
        Assert.False(DataGridTargetBinding.MatchesNativeSegment(segment, "OtherPanel", "", 0));
    }

    private static WindowTarget Target() => new(11, 11, 1, "Synthetic", DateTimeOffset.UnixEpoch, "", "", new(0, 0, 100, 80));
    internal static ProcessStartInfo Child(string script)
    {
        var start = new ProcessStartInfo("powershell.exe")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add(script);
        return start;
    }
}
