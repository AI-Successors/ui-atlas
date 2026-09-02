using System.Diagnostics;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using UiAtlas.Core.Contracts;

namespace UiAtlas.Core.Recording.Windows;

[SupportedOSPlatform("windows")]
public sealed class LowLevelInputMonitor : IDisposable
{
    private readonly long _rootOwner;
    private readonly int _processId;
    private readonly DateTimeOffset _processStartedUtc;
    private readonly ConcurrentQueue<InputEvent> _events = new();
    private readonly NativeMethods.HookProc _mouseCallback;
    private readonly NativeMethods.HookProc _keyboardCallback;
    private Thread? _thread;
    private nint _mouseHook;
    private nint _keyboardHook;
    private uint _threadId;
    private long _sequence;
    private int _queuedCount;
    private long _droppedEvents;
    private int _inputCapturePaused;
    private const int MaxQueuedEvents = 100_000;

    public LowLevelInputMonitor(long rootOwner, int processId, DateTimeOffset processStartedUtc)
    {
        _rootOwner = rootOwner;
        _processId = processId;
        _processStartedUtc = processStartedUtc;
        _mouseCallback = MouseCallback;
        _keyboardCallback = KeyboardCallback;
    }

    public void Start()
    {
        if (_thread is not null) throw new InvalidOperationException("Input monitor already started.");
        using var ready = new ManualResetEventSlim();
        _thread = new Thread(() => Run(ready)) { IsBackground = true, Name = "UiAtlas input monitor" };
        _thread.Start();
        if (!ready.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Input monitor did not start.");
        if (_mouseHook == 0 || _keyboardHook == 0) throw new InvalidOperationException("Input hooks could not be installed.");
    }

    public IReadOnlyList<InputEvent> Drain()
    {
        var result = new List<InputEvent>();
        while (_events.TryDequeue(out var item)) { Interlocked.Decrement(ref _queuedCount); result.Add(item); }
        return result;
    }

    public long DroppedEvents => Interlocked.Read(ref _droppedEvents);

    public void SetInputCapturePaused(bool paused) =>
        Volatile.Write(ref _inputCapturePaused, paused ? 1 : 0);

    private void Run(ManualResetEventSlim ready)
    {
        _threadId = NativeMethods.GetCurrentThreadId();
        _mouseHook = NativeMethods.SetWindowsHookExW(NativeMethods.WhMouseLl, _mouseCallback, 0, 0);
        _keyboardHook = NativeMethods.SetWindowsHookExW(NativeMethods.WhKeyboardLl, _keyboardCallback, 0, 0);
        ready.Set();
        while (NativeMethods.GetMessageW(out _, 0, 0, 0) > 0) { }
        if (_mouseHook != 0) NativeMethods.UnhookWindowsHookEx(_mouseHook);
        if (_keyboardHook != 0) NativeMethods.UnhookWindowsHookEx(_keyboardHook);
    }

    private nint MouseCallback(int code, nint wParam, nint lParam)
    {
        if (code >= 0)
        {
            var data = Marshal.PtrToStructure<NativeMethods.MouseHook>(lParam);
            var pointed = NativeMethods.WindowFromPoint(data.Point);
            var root = WindowCatalog.GetRootOwnerHandle(pointed);
            if (IsInScope(pointed))
            {
                var message = (int)wParam;
                if (!ShouldRecordInput(Volatile.Read(ref _inputCapturePaused) != 0, data.ExtraInfo))
                    return NativeMethods.CallNextHookEx(_mouseHook, code, wParam, lParam);

                if (ShouldFocusTargetBeforeMouseMessage(message, IsTargetForeground()))
                    TryFocusTargetBeforeClick(root, pointed);

                if (MouseKind(message) is { } kind)
                    Enqueue(kind, data.Point.X, data.Point.Y, 0, pointed, (nint)_rootOwner);
            }
        }
        return NativeMethods.CallNextHookEx(_mouseHook, code, wParam, lParam);
    }

    private nint KeyboardCallback(int code, nint wParam, nint lParam)
    {
        if (code >= 0)
        {
            var data = Marshal.PtrToStructure<NativeMethods.KeyboardHook>(lParam);
            var foreground = NativeMethods.GetForegroundWindow();
            var message = (int)wParam;
            if (ShouldRecordInput(Volatile.Read(ref _inputCapturePaused) != 0, data.ExtraInfo) &&
                IsInScope(foreground) &&
                message is NativeMethods.WmKeyDown or NativeMethods.WmKeyUp or NativeMethods.WmSysKeyDown or NativeMethods.WmSysKeyUp)
            {
                var virtualKey = IsPrintable(data.VirtualKey) ? 0 : (int)data.VirtualKey;
                Enqueue(message is NativeMethods.WmKeyDown or NativeMethods.WmSysKeyDown ? InputEventKind.KeyDown : InputEventKind.KeyUp,
                    0, 0, virtualKey, foreground, (nint)_rootOwner);
            }
        }
        return NativeMethods.CallNextHookEx(_keyboardHook, code, wParam, lParam);
    }

    private void Enqueue(InputEventKind kind, int x, int y, int virtualKey, nint pointed, nint root)
    {
        if (Interlocked.Increment(ref _queuedCount) > MaxQueuedEvents)
        {
            Interlocked.Decrement(ref _queuedCount);
            Interlocked.Increment(ref _droppedEvents);
            return;
        }
        _events.Enqueue(new(Interlocked.Increment(ref _sequence), DateTimeOffset.UtcNow, kind, x, y, virtualKey, "[redacted]",
            pointed.ToInt64(), root.ToInt64(), NativeMethods.GetForegroundWindow().ToInt64()));
    }

    private bool IsInScope(nint window)
    {
        if (window == 0 || !NativeMethods.IsWindow(window)) return false;
        NativeMethods.GetWindowThreadProcessId(window, out var pid);
        if (pid != _processId) return false;
        try
        {
            using var process = Process.GetProcessById(_processId);
            return !process.HasExited && process.StartTime.ToUniversalTime() == _processStartedUtc;
        }
        catch { return false; }
    }

    private static InputEventKind? MouseKind(int message) => message switch
    {
        NativeMethods.WmMouseMove => InputEventKind.PointerMove,
        NativeMethods.WmLButtonDown or NativeMethods.WmRButtonDown or NativeMethods.WmMButtonDown => InputEventKind.PointerDown,
        NativeMethods.WmLButtonUp or NativeMethods.WmRButtonUp or NativeMethods.WmMButtonUp => InputEventKind.PointerUp,
        NativeMethods.WmMouseWheel => InputEventKind.Wheel,
        _ => null
    };

    internal static bool ShouldRecordInput(bool inputCapturePaused) => !inputCapturePaused;

    internal static bool ShouldRecordInput(bool inputCapturePaused, nuint extraInfo) =>
        !inputCapturePaused && extraInfo != SafeSyntheticInput.Marker;

    internal static bool ShouldFocusTargetBeforeMouseMessage(int message, bool targetIsForeground) =>
        !targetIsForeground && message is
            NativeMethods.WmLButtonDown or NativeMethods.WmRButtonDown or NativeMethods.WmMButtonDown;

    private bool IsTargetForeground() =>
        IsInScope(NativeMethods.GetForegroundWindow());

    private static void TryFocusTargetBeforeClick(nint root, nint pointed)
    {
        var currentThread = NativeMethods.GetCurrentThreadId();
        var foregroundThread = NativeMethods.GetWindowThreadProcessId(NativeMethods.GetForegroundWindow(), out _);
        var targetThread = NativeMethods.GetWindowThreadProcessId(root, out _);
        var attachedForeground = foregroundThread != 0 && foregroundThread != currentThread &&
            NativeMethods.AttachThreadInput(currentThread, foregroundThread, true);
        var attachedTarget = targetThread != 0 && targetThread != currentThread && targetThread != foregroundThread &&
            NativeMethods.AttachThreadInput(currentThread, targetThread, true);
        try
        {
            NativeMethods.BringWindowToTop(root);
            NativeMethods.SetForegroundWindow(root);
            NativeMethods.SetActiveWindow(root);
            NativeMethods.SetFocus(pointed != 0 ? pointed : root);
        }
        finally
        {
            if (attachedTarget) NativeMethods.AttachThreadInput(currentThread, targetThread, false);
            if (attachedForeground) NativeMethods.AttachThreadInput(currentThread, foregroundThread, false);
        }
    }

    private static bool IsPrintable(uint key) => key is >= 0x30 and <= 0x5A or >= 0x60 and <= 0x6F or >= 0xBA and <= 0xE2;

    public void Dispose()
    {
        if (_thread is null) return;
        NativeMethods.PostThreadMessageW(_threadId, NativeMethods.WmQuit, 0, 0);
        _thread.Join(TimeSpan.FromSeconds(5));
        _thread = null;
    }
}
