using System.Diagnostics;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Recording.Windows;

namespace UiAtlas.Core.Windows.Tests;

public sealed class UiaWorkerClientTests
{
    [Fact]
    public void WorkerCollectionRunsInMtaDespiteStaGuiEntryPoint()
    {
        var apartment = UiaWorkerHost.RunCollection(() => Thread.CurrentThread.GetApartmentState());

        Assert.Equal(ApartmentState.MTA, apartment);
    }

    [Fact]
    public void WorkerHostRejectsMalformedRequestsBeforeTouchingDesktopState()
    {
        Assert.Equal(64, UiaWorkerHost.Run([]));
        Assert.Equal(64, UiaWorkerHost.Run(["1", "0", "1", "0", "1", "full"]));
        Assert.Equal(64, UiaWorkerHost.Run(["1", "1", "1", "0", "1", "full", "extra"]));
    }

    [Fact]
    public void InspectionPointModeParsingIsBoundedAndDeterministic()
    {
        Assert.True(UiaWorkerHost.TryParseInspectionPointsMode(
            "inspect-points:10,20;-30,40", out var points));
        Assert.Equal([new RectI(10, 20, 1, 1), new RectI(-30, 40, 1, 1)], points);
        Assert.False(UiaWorkerHost.TryParseInspectionPointsMode("inspect-points:", out _));
        Assert.False(UiaWorkerHost.TryParseInspectionPointsMode("inspect-points:10", out _));
        Assert.False(UiaWorkerHost.TryParseInspectionPointsMode(
            "inspect-points:" + string.Join(';', Enumerable.Range(0, 97).Select(index => $"{index},{index}")),
            out _));
    }

    [Fact]
    public async Task InspectionPointsUseOneBoundedWorkerRequest()
    {
        if (!OperatingSystem.IsWindows()) return;
        string? observedMode = null;
        var client = new UiaWorkerClient((_, _, _, mode) =>
        {
            observedMode = mode;
            var start = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-NonInteractive");
            start.ArgumentList.Add("-Command");
            start.ArgumentList.Add("[Console]::Out.Write('[]'); exit 0");
            return start;
        });
        var target = new WindowTarget(22, 11, Environment.ProcessId, "Synthetic", DateTimeOffset.UtcNow,
            "Window", "Window", new RectI(0, 0, 100, 100));

        var result = await client.CollectInspectionPointsAsync(
            target, target.Hwnd, [new RectI(30, 40, 1, 1), new RectI(10, 20, 1, 1)],
            TimeSpan.FromSeconds(5), 32, CancellationToken.None);

        Assert.Equal("inspect-points:30,40;10,20", observedMode);
        Assert.Equal("ok", result.Status);
    }

    [Fact]
    public async Task HungWorkerIsTerminatedAtTimeout()
    {
        if (!OperatingSystem.IsWindows()) return;
        var client = new UiaWorkerClient((_, _, _, _) =>
        {
            var start = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-NonInteractive");
            start.ArgumentList.Add("-Command");
            start.ArgumentList.Add("[Threading.Thread]::Sleep(30000)");
            return start;
        });
        var target = new WindowTarget(1, 1, Environment.ProcessId, "Synthetic", DateTimeOffset.UtcNow, "", "", new RectI(0, 0, 1, 1));
        var elapsed = Stopwatch.StartNew();

        var result = await client.CollectAsync(target, TimeSpan.FromMilliseconds(250), 10, CancellationToken.None);

        Assert.True(result.TimedOut);
        Assert.Equal("timeout", result.Status);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task DefaultCollectionUsesVisibleSelectedWindowInsteadOfHiddenRootOwner()
    {
        if (!OperatingSystem.IsWindows()) return;
        long observedScope = 0;
        var client = new UiaWorkerClient((_, scopeHwnd, _, _) =>
        {
            observedScope = scopeHwnd;
            var start = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-NonInteractive");
            start.ArgumentList.Add("-Command");
            start.ArgumentList.Add("[Console]::Out.Write('[]'); exit 0");
            return start;
        });
        var target = new WindowTarget(22, 11, Environment.ProcessId, "Synthetic", DateTimeOffset.UtcNow,
            "Visible dialog", "Window", new RectI(0, 0, 100, 100));

        var result = await client.CollectAsync(target, TimeSpan.FromSeconds(15), 10, CancellationToken.None);

        Assert.Equal(22, observedScope);
        Assert.Equal("ok", result.Status);
    }

    [Fact]
    public async Task TransientCollectorFailureIsRetriedOnce()
    {
        if (!OperatingSystem.IsWindows()) return;
        var attempts = 0;
        var client = new UiaWorkerClient((_, _, _, _) =>
        {
            var attempt = Interlocked.Increment(ref attempts);
            var start = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-NonInteractive");
            start.ArgumentList.Add("-Command");
            start.ArgumentList.Add(attempt == 1
                ? "[Console]::Error.Write('uia-worker-error:COMException:80004005'); exit 65"
                : "[Console]::Out.Write('[]'); exit 0");
            return start;
        });
        var target = new WindowTarget(1, 1, Environment.ProcessId, "Synthetic", DateTimeOffset.UtcNow, "", "", new RectI(0, 0, 1, 1));

        var result = await client.CollectAsync(target, TimeSpan.FromSeconds(15), 10, CancellationToken.None);

        Assert.Equal(2, attempts);
        Assert.False(result.TimedOut);
        Assert.Equal("ok", result.Status);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task TargetIdentityFailureIsNotRetried()
    {
        if (!OperatingSystem.IsWindows()) return;
        var attempts = 0;
        var client = new UiaWorkerClient((_, _, _, _) =>
        {
            Interlocked.Increment(ref attempts);
            var start = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-NonInteractive");
            start.ArgumentList.Add("-Command");
            start.ArgumentList.Add("exit 66");
            return start;
        });
        var target = new WindowTarget(1, 1, Environment.ProcessId, "Synthetic", DateTimeOffset.UtcNow, "", "", new RectI(0, 0, 1, 1));

        var result = await client.CollectAsync(target, TimeSpan.FromSeconds(15), 10, CancellationToken.None);

        Assert.Equal(1, attempts);
        Assert.False(result.TimedOut);
        Assert.Equal("target-changed", result.Status);
    }

    [Fact]
    public async Task RepeatedCollectorFailurePreservesSanitizedDiagnostic()
    {
        if (!OperatingSystem.IsWindows()) return;
        var attempts = 0;
        var client = new UiaWorkerClient((_, _, _, _) =>
        {
            Interlocked.Increment(ref attempts);
            var start = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-NonInteractive");
            start.ArgumentList.Add("-Command");
            start.ArgumentList.Add("[Console]::Error.Write('uia-worker-error:COMException:80004005'); exit 65");
            return start;
        });
        var target = new WindowTarget(1, 1, Environment.ProcessId, "Synthetic", DateTimeOffset.UtcNow, "", "", new RectI(0, 0, 1, 1));

        var result = await client.CollectAsync(target, TimeSpan.FromSeconds(15), 10, CancellationToken.None);

        Assert.Equal(2, attempts);
        Assert.False(result.TimedOut);
        Assert.Equal("collector-failed:COMException:80004005", result.Status);
    }
}
