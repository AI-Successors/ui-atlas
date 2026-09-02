using UiAtlas.Core.Contracts;
using UiAtlas.Core.Recording.Windows;

namespace UiAtlas.Core.Windows.Tests;

public sealed class BoundedAutomationCollectorTests
{
    [Theory]
    [InlineData(1, true)]
    [InlineData(10_000, true)]
    [InlineData(RecordingContractLimits.MaxControlsPerFrame, true)]
    [InlineData(0, false)]
    [InlineData(RecordingContractLimits.MaxControlsPerFrame + 1, false)]
    public void NodeLimitMatchesRecordingContract(int maxNodes, bool expected)
    {
        Assert.Equal(expected, BoundedAutomationCollector.IsSupportedNodeLimit(maxNodes));
    }

    [Theory]
    [InlineData("ControlType.Button", "InvokePatternIdentifiers.Pattern")]
    [InlineData("ControlType.TreeItem", "SelectionItemPatternIdentifiers.Pattern")]
    [InlineData("ControlType.TreeItem", "ExpandCollapsePatternIdentifiers.Pattern")]
    [InlineData("ControlType.ComboBox", "ExpandCollapsePatternIdentifiers.Pattern")]
    [InlineData("ControlType.Edit", "ValuePatternIdentifiers.Pattern")]
    public void SuggestedRevitPatternsKeepVisibleControlsSemanticallyUseful(
        string controlType,
        string expectedPattern)
    {
        var patterns = BoundedAutomationCollector.SuggestedRevitPatterns(controlType, "test");

        Assert.Contains(expectedPattern, patterns);
    }

    [Fact]
    public void VisibleNativeFormulaButtonOverridesIncorrectProviderOffscreenState()
    {
        var controls = new List<AutomationObservation>
        {
            Observation("fx", "15", "Insert Function", new RectI(223, 233, 45, 28), isOffscreen: true),
            Observation("hidden", "other", "Hidden item", new RectI(223, 233, 45, 28), isOffscreen: true)
        };

        BoundedAutomationCollector.RestoreVisibleFormulaBarControls(
            controls, 0, new RectI(220, 230, 52, 34));

        Assert.False(controls[0].IsOffscreen);
        Assert.True(controls[1].IsOffscreen);
    }

    [Fact]
    public void FormulaButtonOutsideNativeChildRemainsOffscreen()
    {
        var controls = new List<AutomationObservation>
        {
            Observation("fx", "15", "Insert Function", new RectI(500, 500, 45, 28), isOffscreen: true)
        };

        BoundedAutomationCollector.RestoreVisibleFormulaBarControls(
            controls, 0, new RectI(220, 230, 52, 34));

        Assert.True(controls[0].IsOffscreen);
    }

    [Fact]
    public void RevitPropertyGridIsSplitIntoStableVisibleRows()
    {
        var rows = BoundedAutomationCollector.RevitPropertyRowBounds(new RectI(2, 330, 339, 230), 120);

        Assert.Equal(9, rows.Count);
        Assert.Equal(new RectI(2, 336, 318, 25), rows[0]);
        Assert.Equal(new RectI(2, 411, 318, 25), rows[3]);
        Assert.Equal(new RectI(2, 536, 318, 24), rows[^1]);
    }

    [Theory]
    [InlineData("ControlType.Pane", 0, 60, 1200, 180, true)]
    [InlineData("ControlType.Group", 100, 80, 260, 120, true)]
    [InlineData("ControlType.Button", 100, 80, 80, 30, false)]
    [InlineData("ControlType.Pane", 0, 0, 1600, 900, false)]
    public void LocalProbeRootMustBeABoundedContainer(
        string type, int x, int y, int width, int height, bool expected)
    {
        Assert.Equal(expected, BoundedAutomationCollector.IsLocalProbeContainer(
            type, new RectI(x, y, width, height), new RectI(0, 0, 1600, 900)));
    }

    [Theory]
    [InlineData("ControlType.Custom", 0, 62, 1600, 122, true)]
    [InlineData("ControlType.Group", 500, 62, 280, 122, true)]
    [InlineData("ControlType.Button", 700, 96, 24, 24, false)]
    [InlineData("ControlType.Custom", 0, 0, 1600, 900, false)]
    public void RevitPointProbeDescendsOnlyInsideBoundedContainers(
        string type, int x, int y, int width, int height, bool expected)
    {
        var control = new AutomationObservation(
            "point", "root", "", "", type, "",
            new RectI(x, y, width, height), true, false, "WPF", 42);

        Assert.Equal(expected, BoundedAutomationCollector.IsRevitPointDescentContainer(
            control, new RectI(0, 0, 1600, 900)));
    }

    [Theory]
    [InlineData("Button", 10, 300, 28, 28, true)]
    [InlineData("ComboBox", 20, 100, 220, 30, true)]
    [InlineData("Button", 486, 300, 28, 28, false)]
    [InlineData("Static", 10, 300, 250, 300, false)]
    [InlineData("Button", -20, 300, 28, 28, false)]
    public void NativePeripheralProbeKeepsInteractiveSideControlsOnly(
        string className, int x, int y, int width, int height, bool expected)
    {
        Assert.Equal(expected, BoundedAutomationCollector.IsPeripheralNativeControl(
            className, new RectI(x, y, width, height), new RectI(0, 0, 1000, 800)));
    }

    [Theory]
    [InlineData("ControlType.Button", 30, 30, false, true)]
    [InlineData("ControlType.ListItem", 280, 24, false, true)]
    [InlineData("ControlType.Text", 90, 20, false, false)]
    [InlineData("ControlType.Button", 30, 30, true, false)]
    public void RevitBrowserProbeKeepsVisibleInteractiveControls(
        string controlType, int width, int height, bool isOffscreen, bool expected)
    {
        var observation = new AutomationObservation(
            "browser", "", "", "Browser control", controlType, "ChromeControl",
            new RectI(10, 10, width, height), true, isOffscreen, "Chrome", 42,
            ["InvokePatternIdentifiers.Pattern"]);

        Assert.Equal(expected, BoundedAutomationCollector.IsUsefulBrowserControl(observation));
    }

    private static AutomationObservation Observation(
        string runtimeId,
        string automationId,
        string name,
        RectI bounds,
        bool isOffscreen) =>
        new(runtimeId, "", automationId, name, "ControlType.Button", "Button", bounds,
            IsEnabled: true, IsOffscreen: isOffscreen, FrameworkId: "Win32", WindowHwnd: 42,
            SupportedPatterns: ["InvokePatternIdentifiers.Pattern"]);
}
