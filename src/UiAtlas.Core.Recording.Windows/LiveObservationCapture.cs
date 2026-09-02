using System.Diagnostics;
using UiAtlas.Core.Contracts;

namespace UiAtlas.Core.Recording.Windows;

public sealed record LiveObservationCaptureResult(
    FrameObservation Frame,
    byte[] ScreenshotPng,
    RectI ScreenshotBounds,
    string ScreenshotMethod,
    bool ScreenshotUsedFallback,
    bool IsPartial,
    IReadOnlyList<CaptureHealthEvent> Health);

/// <summary>
/// Captures one transient, target-scoped observation using the same Windows and
/// UI Automation collectors as the attended recorder, without starting input
/// monitoring or publishing a durable recording bundle.
/// </summary>
public sealed class LiveObservationCapture
{
    private readonly UiaWorkerClient _automation;

    public LiveObservationCapture() : this(new UiaWorkerClient()) { }

    internal LiveObservationCapture(UiaWorkerClient automation) =>
        _automation = automation ?? throw new ArgumentNullException(nameof(automation));

    public async Task<LiveObservationCaptureResult> CaptureAsync(
        WindowTarget target,
        long sequence,
        string trigger,
        TimeSpan automationTimeout,
        CancellationToken cancellationToken,
        int maxAutomationNodes = RecordingContractLimits.MaxControlsPerFrame)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (sequence <= 0) throw new ArgumentOutOfRangeException(nameof(sequence));
        if (string.IsNullOrWhiteSpace(trigger) || trigger.Length > 256)
            throw new ArgumentException("A bounded observation trigger is required.", nameof(trigger));
        if (automationTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(automationTimeout));
        if (maxAutomationNodes is < 1 or > RecordingContractLimits.MaxControlsPerFrame)
            throw new ArgumentOutOfRangeException(nameof(maxAutomationNodes));

        var currentTarget = RevalidateTarget(target);
        var discovered = WindowCatalog.ListScopedWindows(currentTarget).ToArray();
        var scopedTargets = discovered.Take(RecordingContractLimits.MaxScopedWindows).ToArray();
        if (scopedTargets.Length == 0)
            throw new InvalidOperationException("The sealed target has no capturable scoped windows.");

        var scopedWindows = scopedTargets.Select(WindowSnapshotCapture.Observe).ToArray();
        var root = scopedWindows.FirstOrDefault(window => window.Hwnd == currentTarget.RootOwnerHwnd) ??
                   WindowSnapshotCapture.Observe(currentTarget);
        var captureTargets = scopedTargets.Where(WindowSnapshotCapture.IsCapturable).ToArray();
        if (captureTargets.Length == 0)
            throw new InvalidOperationException("The sealed target has no visible capturable windows.");
        var primaryHwnd = captureTargets.Any(window => window.Hwnd == currentTarget.RootOwnerHwnd)
            ? currentTarget.RootOwnerHwnd
            : currentTarget.Hwnd;
        var screenshotBounds = Union(captureTargets.Select(window => window.Bounds));
        var health = new List<CaptureHealthEvent>();
        var partial = discovered.Length > scopedTargets.Length;
        if (partial)
            health.Add(Health("scope", "window-limit", "The scoped window family exceeded the live capture limit."));

        var screenshot = await WindowSnapshotCapture.CapturePngAsync(captureTargets, cancellationToken)
            .ConfigureAwait(false);
        partial |= screenshot.IsPartial;
        health.Add(Health("screenshot", screenshot.Method,
            screenshot.IsPartial ? "The scoped screenshot is partial." : "The scoped screenshot was captured."));

        var timer = Stopwatch.StartNew();
        var automation = new List<AutomationObservation>();
        var observedHwnds = new HashSet<long>();
        var automationTimedOut = false;
        var rootStatus = "failed";
        var nodeLimitReached = false;

        foreach (var scopedTarget in captureTargets
                     .OrderByDescending(window => window.Hwnd == primaryHwnd)
                     .ThenBy(window => window.ZOrder))
        {
            if (scopedTarget.Hwnd != currentTarget.RootOwnerHwnd &&
                automation.Any(item => item.WindowHwnd == scopedTarget.Hwnd))
            {
                observedHwnds.Add(scopedTarget.Hwnd);
                continue;
            }

            var remainingTime = automationTimeout - timer.Elapsed;
            var remainingNodes = maxAutomationNodes - automation.Count;
            if (remainingTime <= TimeSpan.Zero)
            {
                partial = true;
                health.Add(Health("uia", "timeout", "The live UI Automation budget was exhausted."));
                break;
            }
            if (remainingNodes <= 0)
            {
                nodeLimitReached = true;
                partial = true;
                health.Add(Health("uia", "node-limit", "The live UI Automation node limit was reached."));
                break;
            }

            var result = await _automation.CollectAsync(
                currentTarget,
                remainingTime,
                remainingNodes,
                cancellationToken,
                scopedTarget.Hwnd).ConfigureAwait(false);
            automationTimedOut |= result.TimedOut;
            if (scopedTarget.Hwnd == primaryHwnd)
            {
                rootStatus = result.Status;
            }

            if (result.Status is "ok" or "node-limit")
            {
                automation.AddRange(result.Items);
                observedHwnds.Add(scopedTarget.Hwnd);
                foreach (var hwnd in result.Items.Select(item => item.WindowHwnd).Where(hwnd => hwnd != 0))
                    if (scopedTargets.Any(window => window.Hwnd == hwnd)) observedHwnds.Add(hwnd);
                if (result.Status == "node-limit")
                {
                    nodeLimitReached = true;
                    partial = true;
                    health.Add(Health("uia", "node-limit", "A scoped UI Automation observation reached its node limit."));
                }
            }
            else
            {
                partial = true;
                health.Add(Health("uia", result.Status,
                    scopedTarget.Hwnd == primaryHwnd
                        ? "The root UI Automation observation is unavailable."
                        : "An owned-window UI Automation observation is unavailable."));
            }
        }

        currentTarget = RevalidateTarget(currentTarget);
        var boundedAutomation = automation
            .GroupBy(item => (item.WindowHwnd, item.RuntimeId))
            .Select(group => group.First())
            .Take(maxAutomationNodes)
            .ToArray();
        if (boundedAutomation.Length == maxAutomationNodes)
        {
            nodeLimitReached = true;
            partial = true;
            if (!health.Any(item => item.Component == "uia" && item.Status == "node-limit"))
                health.Add(Health("uia", "node-limit", "The live UI Automation node limit was reached."));
        }
        var frameStatus = rootStatus is "ok" or "node-limit"
            ? nodeLimitReached ? "node-limit" : partial ? "partial" : "ok"
            : rootStatus;

        var frame = new FrameObservation(
            sequence,
            DateTimeOffset.UtcNow,
            string.Empty,
            root,
            boundedAutomation,
            automationTimedOut,
            frameStatus,
            trigger,
            scopedWindows,
            CapturePhase: "materialized",
            ObservationScope: "full-root",
            ObservedWindowHwnds: observedHwnds.Order().ToArray(),
            ScreenshotBounds: screenshotBounds);

        return new(frame, screenshot.Png, screenshotBounds, screenshot.Method,
            screenshot.UsedFallback, partial, health);
    }

    private static WindowTarget RevalidateTarget(WindowTarget target)
    {
        var current = WindowCatalog.Resolve(target.Hwnd);
        if (current.RootOwnerHwnd != target.RootOwnerHwnd || current.ProcessId != target.ProcessId ||
            current.ProcessStartedUtc != target.ProcessStartedUtc)
            throw new InvalidOperationException("Selected target identity changed.");
        return current;
    }

    private static RectI Union(IEnumerable<RectI> bounds)
    {
        var values = bounds.Where(value => value.Width > 0 && value.Height > 0).ToArray();
        if (values.Length == 0) throw new InvalidOperationException("The scoped window family has no visible bounds.");
        var left = values.Min(value => value.X);
        var top = values.Min(value => value.Y);
        var right = values.Max(value => checked(value.X + value.Width));
        var bottom = values.Max(value => checked(value.Y + value.Height));
        return new(left, top, checked(right - left), checked(bottom - top));
    }

    private static CaptureHealthEvent Health(string component, string status, string detail) =>
        new(DateTimeOffset.UtcNow, component, status, detail, true);
}
