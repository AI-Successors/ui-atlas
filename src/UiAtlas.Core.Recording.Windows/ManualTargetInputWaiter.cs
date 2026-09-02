using System.Diagnostics;
using System.Runtime.Versioning;

namespace UiAtlas.Core.Recording.Windows;

[SupportedOSPlatform("windows")]
internal sealed class ManualTargetInputWaiter
{
    private const int VkLButton = 0x01;
    private const int VkRButton = 0x02;
    private const int VkMButton = 0x04;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(30);
    private readonly WindowTarget _target;
    private readonly Func<ManualButtonState> _readButtons;
    private readonly Func<bool> _isCursorInsideTargetScope;
    private readonly TimeSpan _pollInterval;

    public ManualTargetInputWaiter(WindowTarget target)
    {
        _target = target;
        _readButtons = ReadButtons;
        _isCursorInsideTargetScope = IsCursorInsideTargetScope;
        _pollInterval = PollInterval;
    }

    internal ManualTargetInputWaiter(
        WindowTarget target,
        Func<ManualButtonState> readButtons,
        Func<bool> isCursorInsideTargetScope,
        TimeSpan pollInterval)
    {
        _target = target;
        _readButtons = readButtons;
        _isCursorInsideTargetScope = isCursorInsideTargetScope;
        _pollInterval = pollInterval;
    }

    public async Task<DateTimeOffset> WaitForClicksAsync(
        int clickCount,
        Action<int, int>? progress,
        CancellationToken cancellationToken)
    {
        if (clickCount is < 1 or > 2) throw new ArgumentOutOfRangeException(nameof(clickCount));

        var last = _readButtons();
        while (last.AnyDown)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);
            last = _readButtons();
        }
        var observed = 0;

        while (observed < clickCount)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = _readButtons();

            var released = (last.Left && !current.Left) ||
                           (last.Right && !current.Right) ||
                           (last.Middle && !current.Middle);
            // GetAsyncKeyState also exposes a transition bit. It preserves a
            // fast click that began and ended between two polling intervals.
            var quickClick = !current.AnyDown && current.AnyPressedSinceLastRead;
            if ((released || quickClick) && _isCursorInsideTargetScope())
            {
                observed++;
                progress?.Invoke(observed, clickCount);
                if (observed == clickCount)
                    return DateTimeOffset.UtcNow;
            }

            last = current;
            if (observed < clickCount)
                await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);
        }

        throw new InvalidOperationException("Manual click wait ended unexpectedly.");
    }

    private bool IsCursorInsideTargetScope()
    {
        try
        {
            if (!NativeMethods.GetCursorPos(out var point)) return false;
            var candidate = NativeMethods.WindowFromPoint(point);
            if (candidate == 0) return false;
            var rootOwner = WindowCatalog.GetRootOwnerHandle(candidate);
            if (rootOwner.ToInt64() != _target.RootOwnerHwnd) return false;
            NativeMethods.GetWindowThreadProcessId(rootOwner, out var processId);
            if (processId != _target.ProcessId) return false;
            using var process = Process.GetProcessById(_target.ProcessId);
            return !process.HasExited && process.StartTime.ToUniversalTime() == _target.ProcessStartedUtc;
        }
        catch
        {
            return false;
        }
    }

    private static ManualButtonState ReadButtons()
    {
        var left = NativeMethods.GetAsyncKeyState(VkLButton);
        var right = NativeMethods.GetAsyncKeyState(VkRButton);
        var middle = NativeMethods.GetAsyncKeyState(VkMButton);
        return new(
            (left & 0x8000) != 0,
            (right & 0x8000) != 0,
            (middle & 0x8000) != 0,
            (left & 0x0001) != 0,
            (right & 0x0001) != 0,
            (middle & 0x0001) != 0);
    }
}

internal readonly record struct ManualButtonState(
    bool Left,
    bool Right,
    bool Middle,
    bool LeftPressedSinceLastRead = false,
    bool RightPressedSinceLastRead = false,
    bool MiddlePressedSinceLastRead = false)
{
    public bool AnyDown => Left || Right || Middle;
    public bool AnyPressedSinceLastRead =>
        LeftPressedSinceLastRead || RightPressedSinceLastRead || MiddlePressedSinceLastRead;
}
