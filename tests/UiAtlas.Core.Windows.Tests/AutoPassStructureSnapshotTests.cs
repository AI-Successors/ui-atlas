using UiAtlas.Core.Contracts;
using UiAtlas.Core.Recording.Windows;

namespace UiAtlas.Core.Windows.Tests;

public sealed class AutoPassStructureSnapshotTests
{
    [Fact]
    public void Capture_DetectsScopedPopupWindowAsStructuralChange()
    {
        var baselineFrame = CreateExcelFrame("Home");
        var popupWindow = new WindowObservation(
            222,
            100,
            7,
            "#32768",
            "Paste Menu",
            new RectI(140, 206, 260, 320),
            true,
            true,
            false,
            false,
            96,
            OwnerHwnd: 100);
        var changedFrame = CreateExcelFrame(
            "Home",
            scopedWindows:
            [
                baselineFrame.Window,
                popupWindow
            ]);

        var baseline = AutoPassStructureSnapshotFactory.Capture(baselineFrame, fallbackLayerKey: null);
        var changed = AutoPassStructureSnapshotFactory.Capture(changedFrame, fallbackLayerKey: null);

        Assert.True(changed.HasStructuralChangeComparedTo(baseline));
    }

    [Fact]
    public void Capture_DetectsTopLevelTabSwitchAsStructuralChange()
    {
        var baseline = AutoPassStructureSnapshotFactory.Capture(CreateExcelFrame("Home"), fallbackLayerKey: null);
        var changed = AutoPassStructureSnapshotFactory.Capture(CreateExcelFrame("Insert"), fallbackLayerKey: null);

        Assert.True(changed.HasStructuralChangeComparedTo(baseline));
    }

    [Fact]
    public void Capture_DetectsRibbonBandReshapeWithinTheSameTab()
    {
        var baseline = AutoPassStructureSnapshotFactory.Capture(CreateExcelFrame("Home"), fallbackLayerKey: null);
        var changed = AutoPassStructureSnapshotFactory.Capture(
            CreateExcelFrame(
                "Home",
                extraControls:
                [
                    new AutomationObservation(
                        "cell-styles",
                        "home-group",
                        "CellStyles",
                        "Cell Styles",
                        "ControlType.Button",
                        "NetUIRibbonButton",
                        new RectI(282, 154, 74, 54),
                        true,
                        false,
                        "Win32",
                        SupportedPatterns: ["ExpandCollapse"])
                ]),
            fallbackLayerKey: null);

        Assert.True(changed.HasStructuralChangeComparedTo(baseline));
    }

    [Fact]
    public void Capture_DoesNotReportStructuralChangeForAutomationlessProbeWithoutWindowChange()
    {
        var baselineFrame = CreateExcelFrame("Home");
        var fallbackLayerKey = AutoTabDiscovery.Discover(baselineFrame).Single(candidate => candidate.DisplayName == "Home").StableKey;
        var probeFrame = new FrameObservation(
            0,
            DateTimeOffset.UnixEpoch,
            "",
            baselineFrame.Window,
            [],
            false,
            "not-requested",
            "auto-refresh-probe",
            [baselineFrame.Window]);

        var baseline = AutoPassStructureSnapshotFactory.Capture(baselineFrame, fallbackLayerKey);
        var probe = AutoPassStructureSnapshotFactory.Capture(probeFrame, fallbackLayerKey);

        Assert.False(probe.HasStructuralChangeComparedTo(baseline));
        Assert.False(baseline.HasStructuralChangeComparedTo(probe));
    }

    private static FrameObservation CreateExcelFrame(
        string? selectedTabName,
        IReadOnlyList<AutomationObservation>? extraControls = null,
        IReadOnlyList<WindowObservation>? scopedWindows = null)
    {
        var window = new WindowObservation(
            100,
            100,
            7,
            "XLMAIN",
            "Workbook",
            new RectI(0, 0, 1440, 900),
            true,
            true,
            false,
            false,
            96);

        var controls = new List<AutomationObservation>
        {
            CreateTab("home", "Home", new RectI(64, 108, 66, 30), selectedTabName),
            CreateTab("insert", "Insert", new RectI(140, 108, 70, 30), selectedTabName),
            CreateTab("draw", "Draw", new RectI(220, 108, 58, 30), selectedTabName)
        };

        if (string.Equals(selectedTabName, "Insert", StringComparison.Ordinal))
        {
            controls.AddRange(
            [
                new AutomationObservation("table", "insert-group", "Table", "Table", "ControlType.Button", "NetUIRibbonButton", new RectI(72, 154, 56, 54), true, false, "Win32", SupportedPatterns: ["Invoke"]),
                new AutomationObservation("pictures", "insert-group", "Pictures", "Pictures", "ControlType.Button", "NetUIRibbonButton", new RectI(144, 154, 64, 54), true, false, "Win32", SupportedPatterns: ["Invoke"]),
                new AutomationObservation("illustrations", "insert-group", "Illustrations", "Illustrations", "ControlType.Group", "NetUIRibbonGroup", new RectI(224, 148, 104, 66), true, false, "Win32"),
                new AutomationObservation("shapes", "insert-group", "Shapes", "Shapes", "ControlType.MenuItem", "NetUIRibbonButton", new RectI(236, 154, 68, 54), true, false, "Win32", SupportedPatterns: ["ExpandCollapse"])
            ]);
        }
        else
        {
            controls.AddRange(
            [
                new AutomationObservation("paste", "home-group", "PasteMenu_Dropdown", "Paste", "ControlType.MenuItem", "NetUIRibbonButton", new RectI(72, 154, 54, 54), true, false, "Win32", SupportedPatterns: ["ExpandCollapse"]),
                new AutomationObservation("font", "home-group", "Font", "Font", "ControlType.ComboBox", "NetUIComboboxAnchor", new RectI(144, 156, 110, 26), true, false, "Win32", SupportedPatterns: ["ExpandCollapse"]),
                new AutomationObservation("bold", "home-group", "Bold", "Bold", "ControlType.Button", "NetUIRibbonButton", new RectI(270, 154, 28, 28), true, false, "Win32", SupportedPatterns: ["Invoke"]),
                new AutomationObservation("fill", "home-group", "FillColor", "Fill Color", "ControlType.SplitButton", "NetUISplitButtonAnchor", new RectI(314, 150, 44, 34), true, false, "Win32", SupportedPatterns: ["ExpandCollapse", "Invoke"])
            ]);
        }

        if (extraControls is not null)
            controls.AddRange(extraControls);

        return new FrameObservation(
            1,
            DateTimeOffset.UnixEpoch,
            "",
            window,
            controls,
            false,
            "ok",
            "materialized",
            scopedWindows ?? [window]);
    }

    private static AutomationObservation CreateTab(string runtimeId, string name, RectI bounds, string? selectedTabName) =>
        new(
            runtimeId,
            "root",
            "Tab" + name,
            name,
            "ControlType.TabItem",
            "NetUIRibbonTab",
            bounds,
            true,
            false,
            "Win32",
            SupportedPatterns: ["SelectionItem"],
            IsSelected: string.Equals(name, selectedTabName, StringComparison.Ordinal));
}
