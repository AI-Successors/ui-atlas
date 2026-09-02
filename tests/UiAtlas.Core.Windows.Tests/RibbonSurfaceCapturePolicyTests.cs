using UiAtlas.Core.Contracts;
using UiAtlas.Core.Recording.Windows;

namespace UiAtlas.Core.Windows.Tests;

public sealed class RibbonSurfaceCapturePolicyTests
{
    [Fact]
    public void OutlookInitialSurfaceIncludesVisibleApplicationBody()
    {
        var target = new WindowTarget(
            10, 10, 20, "OUTLOOK", DateTimeOffset.UtcNow,
            "Inbox - Microsoft Outlook", "rctrl_renwnd32", new RectI(0, 0, 1600, 900),
            OriginalFilename: "OUTLOOK.EXE", ProductName: "Microsoft Outlook");

        Assert.True(RibbonSurfaceCapturePolicy.NeedsVisibleApplicationBody(target));
    }

    [Fact]
    public void ExcelDoesNotUseOutlookApplicationBodyPolicy()
    {
        var target = new WindowTarget(
            10, 10, 20, "EXCEL", DateTimeOffset.UtcNow,
            "Book1 - Excel", "XLMAIN", new RectI(0, 0, 1600, 900),
            OriginalFilename: "EXCEL.EXE", ProductName: "Microsoft Excel");

        Assert.False(RibbonSurfaceCapturePolicy.NeedsVisibleApplicationBody(target));
    }

    [Fact]
    public void RevitKeepsFastAndDenseProviderBudgetsDistinct()
    {
        var target = new WindowTarget(100, 100, 7, "Window", DateTimeOffset.UnixEpoch,
            "Autodesk Revit 2027", "Revit", new RectI(0, 0, 1900, 1009),
            CompanyName: "Autodesk, Inc.", ProductName: "Autodesk Revit");

        var fast = RibbonSurfaceCapturePolicy.ForTarget(target, RibbonSurfaceCapturePolicy.Fast);
        var command = RibbonSurfaceCapturePolicy.ForTarget(target, RibbonSurfaceCapturePolicy.CommandScan);
        var dense = RibbonSurfaceCapturePolicy.ForTarget(target, RibbonSurfaceCapturePolicy.DenseRetry);

        Assert.Equal(RibbonSurfaceCapturePolicy.RevitFast, fast);
        Assert.Equal(RibbonSurfaceCapturePolicy.RevitCommandScan, command);
        Assert.Equal(RibbonSurfaceCapturePolicy.RevitDenseRetry, dense);
        Assert.True(fast.RibbonTimeout < dense.RibbonTimeout);
        Assert.True(command.RibbonTimeout <= fast.RibbonTimeout);
        Assert.True(dense.RibbonTimeout <= TimeSpan.FromSeconds(9));
    }

    [Theory]
    [InlineData("ControlType.Tab", "ExcelBookTabControl", "Book1")]
    [InlineData("ControlType.TabItem", "", "SheetTab")]
    [InlineData("ControlType.Button", "", "SheetTab")]
    public void WorksheetCollectorKeepsExcelSheetNavigation(
        string controlType,
        string className,
        string automationId)
    {
        Assert.True(BoundedAutomationCollector.IsWorksheetSurfaceControl(controlType, className, automationId));
        Assert.True(BoundedAutomationCollector.IsExcelSheetNavigationControl(controlType, className, automationId));
    }

    [Fact]
    public void WorksheetCollectorRecognizesBottomStatusBarAndRejectsTopCommandBars()
    {
        var application = new RectI(0, 0, 1920, 1040);

        Assert.True(BoundedAutomationCollector.IsExcelStatusBarWindow(
            "MsoCommandBar", "Status Bar", new RectI(0, 1013, 1920, 27), application));
        Assert.True(BoundedAutomationCollector.IsExcelStatusBarWindow(
            "MsoCommandBar", "", new RectI(0, 1013, 1920, 27), application));
        Assert.False(BoundedAutomationCollector.IsExcelStatusBarWindow(
            "MsoCommandBar", "Ribbon", new RectI(0, 80, 1920, 140), application));
    }

    [Fact]
    public void RevitCollectorRecognizesNativeStatusBarButtonsAndCombos()
    {
        var application = new RectI(-9, -9, 1938, 1048);
        var statusBar = new RectI(0, 999, 1922, 30);

        Assert.True(BoundedAutomationCollector.IsRevitStatusBarWindow(
            "msctls_statusbar32", statusBar, application));
        Assert.True(BoundedAutomationCollector.IsRevitStatusBarControl(
            "Button", new RectI(1628, 1001, 28, 28), statusBar));
        Assert.True(BoundedAutomationCollector.IsRevitStatusBarControl(
            "ComboBox", new RectI(1096, 1001, 258, 28), statusBar));
        Assert.False(BoundedAutomationCollector.IsRevitStatusBarControl(
            "Static", new RectI(1628, 1001, 28, 28), statusBar));
        Assert.False(BoundedAutomationCollector.IsRevitStatusBarWindow(
            "msctls_statusbar32", new RectI(0, 80, 1922, 30), application));
    }

    [Fact]
    public void NavigationTabsAloneAreNotACompletedRibbonSurface()
    {
        var tabsOnly = new[] { Observation("Home", "ControlType.TabItem") };

        Assert.False(RibbonSurfaceCapturePolicy.HasMaterializedRibbonContent(tabsOnly));
    }

    [Fact]
    public void DenseRibbonControlCompletesTheSurface()
    {
        var controls = new[]
        {
            Observation("Home", "ControlType.TabItem"),
            Observation("Paste", "ControlType.SplitButton")
        };

        Assert.True(RibbonSurfaceCapturePolicy.HasMaterializedRibbonContent(controls));
        Assert.True(RibbonSurfaceCapturePolicy.DenseRetry.RibbonTimeout > RibbonSurfaceCapturePolicy.Fast.RibbonTimeout);
        Assert.True(RibbonSurfaceCapturePolicy.DenseRetry.RibbonMaxNodes > RibbonSurfaceCapturePolicy.Fast.RibbonMaxNodes);
    }

    private static AutomationObservation Observation(string name, string controlType) => new(
        RuntimeId: name,
        ParentRuntimeId: "",
        AutomationId: name,
        Name: name,
        ControlType: controlType,
        ClassName: "NetUI",
        Bounds: new RectI(10, 10, 100, 30),
        IsEnabled: true,
        IsOffscreen: false,
        FrameworkId: "Win32",
        WindowHwnd: 1,
        SupportedPatterns: []);
}
