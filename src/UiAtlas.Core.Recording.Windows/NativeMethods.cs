using System.Runtime.InteropServices;

namespace UiAtlas.Core.Recording.Windows;

internal static partial class NativeMethods
{
    internal const uint GaRoot = 2;
    internal const uint GaRootOwner = 3;
    internal const uint GwOwner = 4;
    internal const int GwlStyle = -16;
    internal const int GwlExStyle = -20;
    internal const long WsChild = 0x40000000L;
    internal const long WsExToolWindow = 0x00000080L;
    internal const long WsExTopmost = 0x00000008L;
    internal const int SwShowMinimized = 2;
    internal const int SwRestore = 9;
    internal const int SwShow = 5;
    internal const int DwmaCloaked = 14;
    internal const int WhKeyboardLl = 13;
    internal const int WhMouseLl = 14;
    internal const int WmKeyDown = 0x0100;
    internal const int WmKeyUp = 0x0101;
    internal const int WmSysKeyDown = 0x0104;
    internal const int WmSysKeyUp = 0x0105;
    internal const int WmMouseMove = 0x0200;
    internal const int WmLButtonDown = 0x0201;
    internal const int WmLButtonUp = 0x0202;
    internal const int WmRButtonDown = 0x0204;
    internal const int WmRButtonUp = 0x0205;
    internal const int WmMButtonDown = 0x0207;
    internal const int WmMButtonUp = 0x0208;
    internal const int WmMouseWheel = 0x020A;
    internal const int WmQuit = 0x0012;
    internal const int WmClose = 0x0010;
    internal const int WmCommand = 0x0111;
    internal const int WmSysCommand = 0x0112;
    internal const int BmClick = 0x00F5;
    internal const int IdCancel = 2;
    internal const int ScClose = 0xF060;
    internal const uint PmNoRemove = 0;
    internal const uint Srccopy = 0x00CC0020;
    internal const uint Captureblt = 0x40000000;
    internal const uint InputMouse = 0;
    internal const uint InputKeyboard = 1;
    internal const byte VkEscape = 0x1B;
    internal const byte VkTab = 0x09;
    internal const byte VkControl = 0x11;
    internal const uint KeyeventfKeyup = 0x0002;
    internal const uint MouseeventfMove = 0x0001;
    internal const uint MouseeventfAbsolute = 0x8000;
    internal const uint MouseeventfLeftDown = 0x0002;
    internal const uint MouseeventfLeftUp = 0x0004;
    internal const uint MouseeventfWheel = 0x0800;
    internal const uint MouseeventfVirtualDesk = 0x4000;
    internal const uint SwpNoSize = 0x0001;
    internal const uint SwpNoMove = 0x0002;
    internal const uint SwpShowWindow = 0x0040;
    internal const uint SwpNoOwnerZOrder = 0x0200;
    internal const uint EventObjectCreate = 0x8000;
    internal const uint EventObjectShow = 0x8002;
    internal const uint EventObjectHide = 0x8003;
    internal const uint WineventOutofcontext = 0x0000;
    internal const uint WineventSkipownprocess = 0x0002;
    internal const int ObjidWindow = 0;
    internal const int ObjidClient = -4;
    internal static readonly nint DpiAwarenessContextPerMonitorAwareV2 = new(-4);
    internal static readonly nint HwndTopMost = new(-1);
    internal static readonly nint HwndNoTopMost = new(-2);

    internal delegate bool EnumWindowsProc(nint hwnd, nint lParam);
    internal delegate nint HookProc(int code, nint wParam, nint lParam);
    internal delegate void WinEventProc(nint hook, uint eventType, nint hwnd, int objectId, int childId, uint eventThread, uint eventTime);

    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool EnumChildWindows(nint parent, EnumWindowsProc callback, nint lParam);
    [DllImport("oleacc.dll")] internal static extern int AccessibleObjectFromWindow(
        nint hwnd, int objectId, ref Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out object accessible);
    [DllImport("oleacc.dll")] internal static extern int AccessibleObjectFromPoint(
        Point point,
        [MarshalAs(UnmanagedType.Interface)] out object accessible,
        [MarshalAs(UnmanagedType.Struct)] out object child);
    [DllImport("oleacc.dll")] internal static extern int AccessibleChildren(
        Accessibility.IAccessible container, int childStart, int childCount,
        [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] object[] children,
        out int obtained);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindowEnabled(nint hwnd);
    [DllImport("user32.dll")] internal static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll")] internal static extern nint GetWindow(nint hwnd, uint command);
    [DllImport("user32.dll")] internal static extern nint GetDlgItem(nint hwnd, int itemId);
    [DllImport("user32.dll")] internal static extern int GetDlgCtrlID(nint hwnd);
    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] internal static extern nint SetActiveWindow(nint hwnd);
    [DllImport("user32.dll")] internal static extern nint SetFocus(nint hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool BringWindowToTop(nint hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool AttachThreadInput(uint attach, uint attachTo, [MarshalAs(UnmanagedType.Bool)] bool value);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool ShowWindow(nint hwnd, int command);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] internal static extern nint WindowFromPoint(Point point);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int virtualKey);
    [DllImport("user32.dll")] internal static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] internal static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll")] internal static extern int GetWindowTextLengthW(nint hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetWindowTextW(nint hwnd, char[] text, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetClassNameW(nint hwnd, char[] text, int count);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetWindowRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll")] internal static extern int GetWindowDpiAwarenessContext(nint hwnd);
    [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetProcessDpiAwarenessContext(nint value);
    [DllImport("user32.dll")] internal static extern nint GetWindowDC(nint hwnd);
    [DllImport("user32.dll")] internal static extern int ReleaseDC(nint hwnd, nint dc);
    [DllImport("user32.dll")] internal static extern int GetWindowPlacement(nint hwnd, ref WindowPlacement placement);
    [DllImport("user32.dll")] internal static extern nint SetWindowsHookExW(int idHook, HookProc callback, nint module, uint threadId);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] internal static extern nint SetWinEventHook(uint eventMin, uint eventMax, nint module, WinEventProc callback, uint processId, uint threadId, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool UnhookWinEvent(nint hook);
    [DllImport("user32.dll")] internal static extern nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);
    [DllImport("user32.dll")] internal static extern int GetMessageW(out Message message, nint hwnd, uint min, uint max);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool PeekMessageW(out Message message, nint hwnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool PostThreadMessageW(uint threadId, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll", EntryPoint = "PostMessageW")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool PostMessageW(nint hwnd, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] internal static extern uint SendInput(uint inputCount, Input[] inputs, int inputSize);
    [DllImport("user32.dll")] internal static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, nuint extraInfo);
    [DllImport("kernel32.dll")] internal static extern uint GetCurrentThreadId();
    [DllImport("gdi32.dll")] internal static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] internal static extern nint CreateCompatibleBitmap(nint dc, int width, int height);
    [DllImport("gdi32.dll")] internal static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool BitBlt(nint dest, int x, int y, int width, int height, nint source, int sx, int sy, uint rop);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool DeleteDC(nint dc);
    [DllImport("dwmapi.dll")] internal static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out int value, int size);

    [StructLayout(LayoutKind.Sequential)] internal readonly record struct Point(int X, int Y);
    [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left; public int Top; public int Right; public int Bottom; }
    [StructLayout(LayoutKind.Sequential)] internal struct Message { public nint Hwnd; public uint Value; public nuint WParam; public nint LParam; public uint Time; public Point Point; }
    [StructLayout(LayoutKind.Sequential)] internal struct MouseHook { public Point Point; public uint MouseData; public uint Flags; public uint Time; public nuint ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] internal struct KeyboardHook { public uint VirtualKey; public uint ScanCode; public uint Flags; public uint Time; public nuint ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] internal struct WindowPlacement { public uint Length; public uint Flags; public uint ShowCmd; public Point Min; public Point Max; public Rect Normal; }
    [StructLayout(LayoutKind.Sequential)] internal struct Input { public uint Type; public InputUnion Union; }
    [StructLayout(LayoutKind.Explicit)] internal struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyboardInput Keyboard;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct MouseInput
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }
}
