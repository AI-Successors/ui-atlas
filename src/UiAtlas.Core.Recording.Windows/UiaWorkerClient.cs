using System.Diagnostics;
using System.Reflection;
using System.Runtime.Versioning;
using System.Text.Json;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Recording;

namespace UiAtlas.Core.Recording.Windows;

[SupportedOSPlatform("windows")]
public sealed class UiaWorkerClient
{
    private readonly Func<WindowTarget, long, int, string, ProcessStartInfo> _startInfoFactory;

    public UiaWorkerClient() : this(CreateStartInfo) { }

    internal UiaWorkerClient(Func<WindowTarget, long, int, string, ProcessStartInfo> startInfoFactory) =>
        _startInfoFactory = startInfoFactory ?? throw new ArgumentNullException(nameof(startInfoFactory));

    public async Task<(IReadOnlyList<AutomationObservation> Items, bool TimedOut, string Status)> CollectAsync(
        WindowTarget target, TimeSpan timeout, int maxNodes, CancellationToken cancellationToken, long? scopeHwnd = null)
    {
        return await CollectCoreAsync(target, timeout, maxNodes, cancellationToken, scopeHwnd, "full").ConfigureAwait(false);
    }

    public Task<(IReadOnlyList<AutomationObservation> Items, bool TimedOut, string Status)> CollectNavigationAsync(
        WindowTarget target, TimeSpan timeout, int maxNodes, CancellationToken cancellationToken, long? scopeHwnd = null) =>
        CollectCoreAsync(target, timeout, maxNodes, cancellationToken, scopeHwnd, "navigation");

    public Task<(IReadOnlyList<AutomationObservation> Items, bool TimedOut, string Status)> CollectRibbonAsync(
        WindowTarget target, TimeSpan timeout, int maxNodes, CancellationToken cancellationToken, long? scopeHwnd = null) =>
        CollectCoreAsync(target, timeout, maxNodes, cancellationToken, scopeHwnd, "ribbon");

    public Task<(IReadOnlyList<AutomationObservation> Items, bool TimedOut, string Status)> CollectNativePeripheralAsync(
        WindowTarget target, TimeSpan timeout, int maxNodes, CancellationToken cancellationToken, long? scopeHwnd = null) =>
        CollectCoreAsync(target, timeout, maxNodes, cancellationToken, scopeHwnd, "native-peripheral");

    public Task<(IReadOnlyList<AutomationObservation> Items, bool TimedOut, string Status)> CollectRevitBrowserAsync(
        WindowTarget target, TimeSpan timeout, int maxNodes, CancellationToken cancellationToken, long? scopeHwnd = null) =>
        CollectCoreAsync(target, timeout, maxNodes, cancellationToken, scopeHwnd, "revit-browser");

    public Task<(IReadOnlyList<AutomationObservation> Items, bool TimedOut, string Status)> CollectWorksheetAsync(
        WindowTarget target, TimeSpan timeout, int maxNodes, CancellationToken cancellationToken, long? scopeHwnd = null) =>
        CollectCoreAsync(target, timeout, maxNodes, cancellationToken, scopeHwnd, "worksheet");

    public Task<(IReadOnlyList<AutomationObservation> Items, bool TimedOut, string Status)> CollectAdobeDisclosuresAsync(
        WindowTarget target, TimeSpan timeout, int maxNodes, CancellationToken cancellationToken) =>
        CollectCoreAsync(target, timeout, maxNodes, cancellationToken, target.RootOwnerHwnd, "adobe-disclosures");

    public Task<(IReadOnlyList<AutomationObservation> Items, bool TimedOut, string Status)> CollectPopupAsync(
        WindowTarget target, long popupHwnd, TimeSpan timeout, int maxNodes, CancellationToken cancellationToken) =>
        CollectCoreAsync(target, timeout, maxNodes, cancellationToken, popupHwnd, "popup");

    public Task<(IReadOnlyList<AutomationObservation> Items, bool TimedOut, string Status)> CollectDialogAsync(
        WindowTarget target, long dialogHwnd, TimeSpan timeout, int maxNodes, CancellationToken cancellationToken) =>
        CollectCoreAsync(target, timeout, maxNodes, cancellationToken, dialogHwnd, "dialog");

    public Task<(IReadOnlyList<AutomationObservation> Items, bool TimedOut, string Status)> CollectPointAsync(
        WindowTarget target, RectI point, TimeSpan timeout, int maxNodes, CancellationToken cancellationToken,
        long? scopeHwnd = null) =>
        CollectCoreAsync(target, timeout, maxNodes, cancellationToken, scopeHwnd ?? target.Hwnd, $"point:{point.X}:{point.Y}");

    public Task<(IReadOnlyList<AutomationObservation> Items, bool TimedOut, string Status)> CollectLocalSubtreeAsync(
        WindowTarget target, RectI point, TimeSpan timeout, int maxNodes, CancellationToken cancellationToken,
        long? scopeHwnd = null) =>
        CollectCoreAsync(target, timeout, maxNodes, cancellationToken, scopeHwnd ?? target.Hwnd, $"subtree:{point.X}:{point.Y}");

    public Task<(IReadOnlyList<AutomationObservation> Items, bool TimedOut, string Status)> CollectViewAsync(
        WindowTarget target, long hwnd, AutomationTreeView view, TimeSpan timeout, int maxNodes,
        CancellationToken cancellationToken) =>
        CollectCoreAsync(target, timeout, maxNodes, cancellationToken, hwnd, "view-" + view.ToString().ToLowerInvariant());

    public Task<(IReadOnlyList<AutomationObservation> Items, bool TimedOut, string Status)> CollectNativeViewAsync(
        WindowTarget target, long hwnd, AutomationTreeView view, TimeSpan timeout, int maxNodes,
        CancellationToken cancellationToken) =>
        CollectCoreAsync(target, timeout, maxNodes, cancellationToken, hwnd,
            "native-view-" + view.ToString().ToLowerInvariant());

    public Task<(IReadOnlyList<AutomationObservation> Items, bool TimedOut, string Status)> CollectNativePointAsync(
        WindowTarget target, long hwnd, RectI point, TimeSpan timeout, int maxNodes,
        CancellationToken cancellationToken) =>
        CollectCoreAsync(target, timeout, maxNodes, cancellationToken, hwnd,
            $"native-point:{point.X}:{point.Y}");

    public Task<(IReadOnlyList<AutomationObservation> Items, bool TimedOut, string Status)> CollectNativeBandAsync(
        WindowTarget target, long hwnd, RectI band, int stepX, int stepY, TimeSpan timeout, int maxNodes,
        CancellationToken cancellationToken) =>
        CollectCoreAsync(target, timeout, maxNodes, cancellationToken, hwnd,
            $"native-band:{band.X}:{band.Y}:{band.Width}:{band.Height}:{stepX}:{stepY}");

    public Task<(IReadOnlyList<AutomationObservation> Items, bool TimedOut, string Status)> CollectInspectionPointsAsync(
        WindowTarget target, long hwnd, IReadOnlyList<RectI> points, TimeSpan timeout, int maxNodes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count is < 1 or > VisualNativeVerification.MaximumProbePoints)
            throw new ArgumentOutOfRangeException(nameof(points));
        var mode = "inspect-points:" + string.Join(';', points.Select(point =>
            $"{point.X.ToString(System.Globalization.CultureInfo.InvariantCulture)},{point.Y.ToString(System.Globalization.CultureInfo.InvariantCulture)}"));
        return CollectCoreAsync(target, timeout, maxNodes, cancellationToken, hwnd, mode);
    }

    public Task<(IReadOnlyList<AutomationObservation> Items, bool TimedOut, string Status)> CollectNativeFocusAsync(
        WindowTarget target, long hwnd, TimeSpan timeout, CancellationToken cancellationToken) =>
        CollectCoreAsync(target, timeout, 1, cancellationToken, hwnd, "native-focus");

    public Task<(IReadOnlyList<AutomationObservation> Items, bool TimedOut, string Status)> CollectNativeFocusWalkAsync(
        WindowTarget target, long hwnd, TimeSpan timeout, int maxNodes, CancellationToken cancellationToken) =>
        CollectCoreAsync(target, timeout, maxNodes, cancellationToken, hwnd, "native-focus-walk");

    public Task<(IReadOnlyList<AutomationObservation> Items, bool TimedOut, string Status)> CollectLegacyAsync(
        WindowTarget target, long hwnd, TimeSpan timeout, int maxNodes, CancellationToken cancellationToken) =>
        CollectCoreAsync(target, timeout, maxNodes, cancellationToken, hwnd, "legacy");

    private async Task<(IReadOnlyList<AutomationObservation> Items, bool TimedOut, string Status)> CollectCoreAsync(
        WindowTarget target, TimeSpan timeout, int maxNodes, CancellationToken cancellationToken, long? scopeHwnd, string mode)
    {
        var timer = Stopwatch.StartNew();
        const int maxAttempts = 2;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var remaining = timeout - timer.Elapsed;
            if (remaining <= TimeSpan.Zero) return ([], true, "timeout");
            var result = await CollectAttemptAsync(
                target, remaining, maxNodes, cancellationToken, scopeHwnd, mode).ConfigureAwait(false);
            if (!result.Status.StartsWith("collector-failed", StringComparison.Ordinal) || attempt == maxAttempts)
                return result;

            remaining = timeout - timer.Elapsed;
            if (remaining <= TimeSpan.Zero) return ([], true, "timeout");
            await Task.Delay(remaining < TimeSpan.FromMilliseconds(100) ? remaining : TimeSpan.FromMilliseconds(100),
                cancellationToken).ConfigureAwait(false);
        }

        return ([], false, "collector-failed");
    }

    private async Task<(IReadOnlyList<AutomationObservation> Items, bool TimedOut, string Status)> CollectAttemptAsync(
        WindowTarget target, TimeSpan timeout, int maxNodes, CancellationToken cancellationToken, long? scopeHwnd, string mode)
    {
        // Hwnd is the user's visible selection. It can legitimately differ from
        // RootOwnerHwnd when an older application keeps a hidden owner behind a
        // visible startup or modal dialog.
        var start = _startInfoFactory(target, scopeHwnd ?? target.Hwnd, maxNodes, mode);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start isolated UI Automation collector.");
        var outputTask = ReadBoundedAsync(process.StandardOutput, 16 * 1024 * 1024, cancellationToken);
        var errorTask = ReadBoundedAsync(process.StandardError, 64 * 1024, cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            TryKill(process);
            await WaitAfterKill(process).ConfigureAwait(false);
            return ([], true, "timeout");
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            await WaitAfterKill(process).ConfigureAwait(false);
            throw;
        }
        var output = await outputTask.ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);
        if (output.Exceeded || error.Exceeded) return ([], false, "response-limit");
        if (process.ExitCode != 0) return ([], false, FailureStatus(process.ExitCode, error.Text));
        try
        {
            var items = JsonSerializer.Deserialize<AutomationObservation[]>(output.Text, JsonDefaults.Options) ?? [];
            return items.Length > maxNodes ? ([], false, "response-limit") : (items, false, items.Length == maxNodes ? "node-limit" : "ok");
        }
        catch (JsonException)
        {
            return ([], false, "invalid-response");
        }
    }

    private static string FailureStatus(int exitCode, string error) => exitCode switch
    {
        64 => "invalid-request",
        65 when error.StartsWith("uia-worker-error:", StringComparison.Ordinal) => CollectorFailureStatus(error),
        65 => "collector-failed",
        66 => "target-changed",
        _ => $"worker-exit-{exitCode}"
    };

    private static string CollectorFailureStatus(string error)
    {
        var detail = error["uia-worker-error:".Length..].Trim();
        if (detail.Length > 96) detail = detail[..96];
        return string.IsNullOrWhiteSpace(detail) ? "collector-failed" : $"collector-failed:{detail}";
    }

    private static ProcessStartInfo CreateStartInfo(WindowTarget target, long scopeHwnd, int maxNodes, string mode)
    {
        var processPath = Environment.ProcessPath ?? throw new InvalidOperationException("Entry process path is unavailable.");
        var assembly = Assembly.GetEntryAssembly()?.Location ?? throw new InvalidOperationException("Entry assembly path is unavailable.");
        var isMuxer = string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase);
        var info = new ProcessStartInfo(processPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        if (isMuxer) info.ArgumentList.Add(assembly);
        info.ArgumentList.Add("__uia-worker");
        info.ArgumentList.Add(target.RootOwnerHwnd.ToString(System.Globalization.CultureInfo.InvariantCulture));
        info.ArgumentList.Add(maxNodes.ToString(System.Globalization.CultureInfo.InvariantCulture));
        info.ArgumentList.Add(target.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        info.ArgumentList.Add(target.ProcessStartedUtc.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture));
        info.ArgumentList.Add(scopeHwnd.ToString(System.Globalization.CultureInfo.InvariantCulture));
        info.ArgumentList.Add(mode);
        return info;
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
    }

    private static async Task WaitAfterKill(Process process)
    {
        try { await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch (TimeoutException) { }
    }

    private static async Task<BoundedText> ReadBoundedAsync(StreamReader reader, int maxCharacters, CancellationToken cancellationToken)
    {
        var builder = new System.Text.StringBuilder(Math.Min(maxCharacters, 64 * 1024));
        var buffer = new char[4_096];
        var exceeded = false;
        while (true)
        {
            var read = await reader.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (!exceeded && builder.Length + read <= maxCharacters) builder.Append(buffer, 0, read);
            else exceeded = true;
        }
        return new(builder.ToString(), exceeded);
    }

    private sealed record BoundedText(string Text, bool Exceeded);
}
