using System.Runtime.InteropServices;

namespace UiAtlas.Core.Cli;

internal static class ConsoleInputMode
{
    private const int StdInputHandle = -10;
    private const uint EnableQuickEditMode = 0x0040;
    private const uint EnableExtendedFlags = 0x0080;

    public static void DisableQuickEdit()
    {
        var input = GetStdHandle(StdInputHandle);
        if (input == 0 || input == new nint(-1) || !GetConsoleMode(input, out var mode))
            return;

        _ = SetConsoleMode(input, WithoutQuickEdit(mode));
    }

    internal static uint WithoutQuickEdit(uint mode) =>
        (mode | EnableExtendedFlags) & ~EnableQuickEditMode;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GetStdHandle(int standardHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetConsoleMode(nint consoleHandle, out uint mode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetConsoleMode(nint consoleHandle, uint mode);
}
