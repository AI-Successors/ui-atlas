using UiAtlas.Core.Cli;

namespace UiAtlas.Core.Tests;

public sealed class ConsoleInputModeTests
{
    [Theory]
    [InlineData(0x0000u, 0x0080u)]
    [InlineData(0x0040u, 0x0080u)]
    [InlineData(0x00F7u, 0x00B7u)]
    public void DisablesQuickEditAndKeepsOtherConsoleFlags(uint original, uint expected)
    {
        Assert.Equal(expected, ConsoleInputMode.WithoutQuickEdit(original));
    }
}
