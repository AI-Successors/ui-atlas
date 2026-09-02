using UiAtlas.Core.Contracts;
using UiAtlas.Core.Recording.Windows;

namespace UiAtlas.Core.Windows.Tests;

public sealed class ProgrammaticControlInvokerTests
{
    [Fact]
    public void PrefersDirectMouseClick_ReturnsTrueForTinyChevronLeaf()
    {
        var control = new AutomationObservation(
            "font-size-chevron",
            "font-size",
            "",
            "",
            "ControlType.Button",
            "NetUIStickyButton",
            new RectI(338, 150, 18, 30),
            true,
            false,
            "Win32",
            SupportedPatterns: ["Invoke"]);

        Assert.True(ProgrammaticControlInvoker.PrefersDirectMouseClick(control));
        Assert.Equal((0.72, 0.58), ProgrammaticControlInvoker.ResolveClickBias(control));
    }

    [Fact]
    public void PrefersDirectMouseClick_ReturnsFalseForComboHost()
    {
        var control = new AutomationObservation(
            "number-format",
            "root",
            "NumberFormat",
            "General",
            "ControlType.ComboBox",
            "NetUIComboboxAnchor",
            new RectI(820, 150, 184, 32),
            true,
            false,
            "Win32",
            SupportedPatterns: ["ExpandCollapse"]);

        Assert.False(ProgrammaticControlInvoker.PrefersDirectMouseClick(control));
        Assert.Equal((0.5, 0.5), ProgrammaticControlInvoker.ResolveClickBias(control));
        Assert.True(ProgrammaticControlInvoker.PrefersExpandBeforeInvoke(control));
    }

    [Fact]
    public void ExcelChartGalleryUsesPhysicalClickInsteadOfFalseSuccessfulExpand()
    {
        var control = new AutomationObservation(
            "chart-column",
            "charts",
            "ChartTypeColumnInsertGallery",
            "Insert Column or Bar Chart",
            "ControlType.MenuItem",
            "NetUIAnchor",
            new RectI(808, 102, 50, 30),
            true,
            false,
            "Win32",
            SupportedPatterns: ["ExpandCollapsePatternIdentifiers.Pattern"]);

        Assert.True(ProgrammaticControlInvoker.PrefersDirectMouseClick(control));
        Assert.Equal((0.78, 0.58), ProgrammaticControlInvoker.ResolveClickBias(control));
    }

    [Fact]
    public void WideDedicatedOfficeDropdownLeafUsesPhysicalClick()
    {
        var control = new AutomationObservation(
            "pivot-chevron",
            "pivot-host",
            "InsertPivotTableDropdown_Dropdown",
            "More Options",
            "ControlType.MenuItem",
            "NetUIRibbonButton",
            new RectI(25, 148, 74, 50),
            true,
            false,
            "Win32",
            SupportedPatterns: ["ExpandCollapsePatternIdentifiers.Pattern"]);

        Assert.True(ProgrammaticControlInvoker.PrefersDirectMouseClick(control));
        Assert.Equal((0.72, 0.58), ProgrammaticControlInvoker.ResolveClickBias(control));
    }

    [Fact]
    public void RevitRibbonFlyoutUsesPhysicalClickOnBottomChevron()
    {
        var control = new AutomationObservation(
            "revit-set-flyout",
            "work-plane",
            "ID_SKETCH_PLANE_TOOL_RibbonListButton_FlyoutButtonShowFlyout",
            "Set",
            "ControlType.Button",
            "Button",
            new RectI(1455, 78, 48, 43),
            true,
            false,
            "WPF",
            SupportedPatterns: ["InvokePatternIdentifiers.Pattern"]);

        Assert.True(ProgrammaticControlInvoker.IsRevitRibbonFlyoutButton(control));
        Assert.True(ProgrammaticControlInvoker.PrefersDirectMouseClick(control));
        Assert.Equal((0.5, 0.82), ProgrammaticControlInvoker.ResolveClickBias(control));
    }
}
