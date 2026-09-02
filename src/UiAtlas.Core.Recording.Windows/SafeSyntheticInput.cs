using System.Runtime.InteropServices;

namespace UiAtlas.Core.Recording.Windows;

internal static class SafeSyntheticInput
{
    internal static readonly nuint Marker = Environment.Is64BitProcess
        ? unchecked((nuint)0x4D4C50524F42454FUL)
        : (nuint)0x4D4C504FUL;
    private const int SmXvirtualscreen = 76;
    private const int SmYvirtualscreen = 77;
    private const int SmCxvirtualscreen = 78;
    private const int SmCyvirtualscreen = 79;

    public static bool MovePointer(int x, int y)
    {
        var left = NativeMethods.GetSystemMetrics(SmXvirtualscreen);
        var top = NativeMethods.GetSystemMetrics(SmYvirtualscreen);
        var width = Math.Max(1, NativeMethods.GetSystemMetrics(SmCxvirtualscreen));
        var height = Math.Max(1, NativeMethods.GetSystemMetrics(SmCyvirtualscreen));
        var absoluteX = (int)Math.Round((x - left) * 65_535d / Math.Max(1, width - 1));
        var absoluteY = (int)Math.Round((y - top) * 65_535d / Math.Max(1, height - 1));
        var input = new[]
        {
            new NativeMethods.Input
            {
                Type = NativeMethods.InputMouse,
                Union = new NativeMethods.InputUnion
                {
                    Mouse = new NativeMethods.MouseInput
                    {
                        X = Math.Clamp(absoluteX, 0, 65_535),
                        Y = Math.Clamp(absoluteY, 0, 65_535),
                        Flags = NativeMethods.MouseeventfMove | NativeMethods.MouseeventfAbsolute |
                                NativeMethods.MouseeventfVirtualDesk,
                        ExtraInfo = Marker
                    }
                }
            }
        };
        return NativeMethods.SendInput(1, input, Marshal.SizeOf<NativeMethods.Input>()) == 1;
    }

    public static bool PressKey(byte virtualKey)
    {
        if (!IsSafeProbeKey(virtualKey)) return false;
        var input = new[] { Keyboard(virtualKey, 0), Keyboard(virtualKey, NativeMethods.KeyeventfKeyup) };
        return NativeMethods.SendInput((uint)input.Length, input, Marshal.SizeOf<NativeMethods.Input>()) == input.Length;
    }

    internal static bool IsSafeProbeKey(byte virtualKey) =>
        virtualKey is 0x12 or NativeMethods.VkTab or NativeMethods.VkEscape;

    private static NativeMethods.Input Keyboard(byte virtualKey, uint flags) => new()
    {
        Type = NativeMethods.InputKeyboard,
        Union = new NativeMethods.InputUnion
        {
            Keyboard = new NativeMethods.KeyboardInput
            {
                VirtualKey = virtualKey,
                Flags = flags,
                ExtraInfo = Marker
            }
        }
    };
}

internal sealed class UserInputCancellationMonitor : IDisposable
{
    private CancellationTokenSource? _cancellation = new();
    private readonly CancellationToken _token;
    private readonly NativeMethods.HookProc _mouseCallback;
    private readonly NativeMethods.HookProc _keyboardCallback;
    private Thread? _thread;
    private nint _mouseHook;
    private nint _keyboardHook;
    private uint _threadId;
    private int _userInputDetected;
    private int _startupFailed;

    public UserInputCancellationMonitor()
    {
        _token = _cancellation.Token;
        _mouseCallback = MouseCallback;
        _keyboardCallback = KeyboardCallback;
    }

    public CancellationToken Token => _token;
    public bool WasCancelledByUser => Volatile.Read(ref _userInputDetected) != 0;
    public bool StartupFailed => Volatile.Read(ref _startupFailed) != 0;

    public void Start()
    {
        using var ready = new ManualResetEventSlim();
        _thread = new Thread(() => Run(ready)) { IsBackground = true, Name = "UiAtlas safe probe input guard" };
        _thread.Start();
        if (!ready.Wait(TimeSpan.FromSeconds(2)) || _mouseHook == 0 || _keyboardHook == 0)
        {
            Volatile.Write(ref _startupFailed, 1);
            CancelSafely(Volatile.Read(ref _cancellation));
        }
    }

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
            if (data.ExtraInfo != SafeSyntheticInput.Marker)
            {
                Volatile.Write(ref _userInputDetected, 1);
                CancelSafely(Volatile.Read(ref _cancellation));
            }
        }
        return NativeMethods.CallNextHookEx(_mouseHook, code, wParam, lParam);
    }

    private nint KeyboardCallback(int code, nint wParam, nint lParam)
    {
        if (code >= 0)
        {
            var data = Marshal.PtrToStructure<NativeMethods.KeyboardHook>(lParam);
            if (data.ExtraInfo != SafeSyntheticInput.Marker)
            {
                Volatile.Write(ref _userInputDetected, 1);
                CancelSafely(Volatile.Read(ref _cancellation));
            }
        }
        return NativeMethods.CallNextHookEx(_keyboardHook, code, wParam, lParam);
    }

    public void Dispose()
    {
        var cancellation = Interlocked.Exchange(ref _cancellation, null);
        if (cancellation is null) return;

        CancelSafely(cancellation);
        var thread = _thread;
        _thread = null;
        var hookThreadStopped = true;
        if (thread is not null)
        {
            NativeMethods.PostThreadMessageW(_threadId, NativeMethods.WmQuit, 0, 0);
            hookThreadStopped = thread.Join(TimeSpan.FromSeconds(2));
        }

        // Native hook callbacks must never observe a disposed source. If Windows
        // has not stopped the hook thread within the bounded shutdown wait, leave
        // this tiny object for GC instead of crashing the recorder on late input.
        if (hookThreadStopped) cancellation.Dispose();
    }

    internal static void CancelSafely(CancellationTokenSource? cancellation)
    {
        if (cancellation is null) return;
        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // A native callback can already be in flight while Dispose tears down
            // the hook. No managed exception may escape across that callback edge.
        }
    }
}
