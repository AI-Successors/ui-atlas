using UiAtlas.Core.Contracts;
using UiAtlas.Core.Recording.Windows;

namespace UiAtlas.Core.Windows.Tests;

public sealed class AutoRibbonCommandDiscoveryTests
{
    [Fact]
    public void DiscoverFindsRevitRibbonFlyoutsButNotQuickAccessOrTreeButtons()
    {
        var root = new WindowObservation(100, 100, 7, "Window", "Autodesk Revit",
            new RectI(0, 0, 1900, 1009), true, true, false, false, 96);
        var active = new AutomationObservation("architecture", "tabs", "Architecture", "Architecture",
            "ControlType.Button", "Button", new RectI(62, 40, 104, 25), true, false, "WPF", 100,
            ["InvokePatternIdentifiers.Pattern"]);
        var quickSave = new AutomationObservation("save", "title", "ID_Save", "Save",
            "ControlType.Button", "Button", new RectI(100, 7, 30, 28), true, false, "WPF", 100,
            ["InvokePatternIdentifiers.Pattern"]);
        var wallFlyout = new AutomationObservation("wall-flyout", "ribbon",
            "ID_OBJECTS_WALL_RibbonListButton_FlyoutButtonShowFlyout", "Wall",
            "ControlType.Button", "Button", new RectI(78, 118, 56, 43), true, false, "WPF", 100,
            ["InvokePatternIdentifiers.Pattern"]);
        var door = new AutomationObservation("door", "ribbon", "ID_OBJECTS_DOOR", "Door",
            "ControlType.Button", "Button", new RectI(135, 71, 55, 90), true, false, "WPF", 100,
            ["InvokePatternIdentifiers.Pattern"]);
        var tree = new AutomationObservation("tree", "", "ProjectBrowser", "Project Browser",
            "ControlType.Tree", "TreeView", new RectI(5, 640, 340, 300), true, false, "WPF", 100);
        var treeButton = new AutomationObservation("tree-plus", "tree", "Expand", "+",
            "ControlType.Button", "Button", new RectI(24, 680, 16, 16), true, false, "WPF", 100,
            ["InvokePatternIdentifiers.Pattern"]);
        var frame = new FrameObservation(1, DateTimeOffset.UnixEpoch, "", root,
            [active, quickSave, wallFlyout, door, tree, treeButton], false, "ok", "revit");

        var discovered = AutoRibbonCommandDiscovery.Discover(frame,
            new AutoTabCandidate("architecture", "Architecture", true, false, active));

        var candidate = Assert.Single(discovered);
        Assert.Equal(wallFlyout.RuntimeId, candidate.Observation.RuntimeId);
    }

    [Theory]
    [InlineData("Sign Out")]
    [InlineData("Sign out options for Irina")]
    [InlineData("Add an account")]
    [InlineData("Switch to Irina account")]
    public void AccountChangingActionsAreAlwaysForbiddenForAutomaticClicks(string name)
    {
        var control = new AutomationObservation("account-action", "root", "", name,
            "ControlType.Button", "OwnerDrawn", new RectI(100, 100, 120, 40),
            true, false, "Win32", SupportedPatterns: ["Invoke"]);

        Assert.True(AutoRibbonCommandDiscovery.IsForbiddenAutomaticAction(control));
    }

    [Fact]
    public void DiscoverVisitsSafeTopChromeOnceButNeverWindowManagementButtons()
    {
        var window = new WindowObservation(100, 100, 7, "XLMAIN", "Workbook",
            new RectI(0, 0, 1920, 1040), true, true, false, false, 96);
        var activeTab = new AutoTabCandidate("home", "Home", true, false,
            new AutomationObservation("home", "root", "TabHome", "Home", "ControlType.TabItem",
                "NetUIRibbonTab", new RectI(73, 60, 70, 38), true, false, "Win32",
                SupportedPatterns: ["SelectionItem"], IsSelected: true));
        var account = new AutomationObservation("account", "root", "MeControlWidget", "Ira N",
            "ControlType.MenuItem", "NetUIAnchor", new RectI(1677, 0, 63, 60), true, false, "Win32",
            SupportedPatterns: ["ExpandCollapse"]);
        var comments = new AutomationObservation("comments", "root", "", "Comments",
            "ControlType.Button", "NetUIStickyButton", new RectI(1687, 64, 118, 30), true, false, "Win32",
            SupportedPatterns: ["Toggle"]);
        var share = new AutomationObservation("share", "root", "", "Share",
            "ControlType.MenuItem", "NetUIAnchor", new RectI(1815, 60, 90, 38), true, false, "Win32",
            SupportedPatterns: ["ExpandCollapse"]);
        var undo = new AutomationObservation("undo", "root", "Undo", "Undo",
            "ControlType.SplitButton", "NetUISplitButtonAnchor", new RectI(223, 12, 47, 35), true, false, "Win32",
            SupportedPatterns: ["ExpandCollapse", "Invoke"]);
        var undoChevron = new AutomationObservation("undo-chevron", "undo", "Undo_Dropdown", "More Options",
            "ControlType.MenuItem", "NetUIRibbonButton", new RectI(254, 12, 16, 35), true, false, "Win32",
            SupportedPatterns: ["ExpandCollapse"]);
        var customizeQuickAccess = new AutomationObservation("quick-access-chevron", "root", "", "Customize Quick Access Toolbar",
            "ControlType.MenuItem", "NetUIAnchor", new RectI(319, 11, 34, 38), true, false, "Win32",
            SupportedPatterns: ["ExpandCollapse"]);
        var redoChevron = new AutomationObservation("redo-chevron", "redo", "Redo_Dropdown", "More Options",
            "ControlType.MenuItem", "NetUIRibbonButton", new RectI(302, 12, 16, 35), false, false, "Win32",
            SupportedPatterns: ["ExpandCollapse"]);
        var search = new AutomationObservation("search", "root", "TellMeControlAnchor", "Search",
            "ControlType.MenuItem", "NetUISearchBoxAnchor", new RectI(740, 10, 460, 40), true, false, "Win32",
            SupportedPatterns: ["ExpandCollapse"]);
        var minimize = new AutomationObservation("minimize", "root", "", "Minimize",
            "ControlType.Button", "NetUIAppFrameHelper", new RectI(1740, 0, 60, 60), true, false, "Win32",
            SupportedPatterns: ["Invoke"]);
        var restore = new AutomationObservation("restore", "root", "", "Restore Down",
            "ControlType.Button", "NetUIAppFrameHelper", new RectI(1800, 0, 60, 60), true, false, "Win32",
            SupportedPatterns: ["Invoke"]);
        var close = new AutomationObservation("close", "root", "", "Close",
            "ControlType.Button", "NetUIAppFrameHelper", new RectI(1860, 0, 60, 60), true, false, "Win32",
            SupportedPatterns: ["Invoke"]);
        var frame = new FrameObservation(1, DateTimeOffset.UnixEpoch, "", window,
            [activeTab.Observation, account, comments, share, undo, undoChevron, customizeQuickAccess, redoChevron, search, minimize, restore, close],
            false, "ok", "materialized");

        var discovered = AutoRibbonCommandDiscovery.Discover(frame, activeTab);

        Assert.Equal(["account", "comments", "quick-access-chevron", "share", "undo-chevron"],
            discovered.Select(candidate => candidate.Observation.RuntimeId).Order(StringComparer.Ordinal).ToArray());
        Assert.Contains(discovered, candidate =>
            candidate.Observation.RuntimeId == "undo-chevron" && candidate.DisplayName == "Undo chevron");
        Assert.DoesNotContain(discovered, candidate => candidate.Observation.RuntimeId is "undo" or "redo-chevron" or "search");
        Assert.DoesNotContain(discovered, candidate => candidate.Observation.RuntimeId is "minimize" or "restore" or "close");
    }

    [Fact]
    public void DialogLauncherDiscoveryFindsRibbonGroupLaunchersWithoutTreatingThemAsChevrons()
    {
        var window = new WindowObservation(100, 100, 7, "XLMAIN", "Workbook",
            new RectI(0, 0, 1440, 900), true, true, false, false, 96);
        var activeTab = new AutoTabCandidate("home", "Home", true, false,
            new AutomationObservation("home", "root", "TabHome", "Home", "ControlType.TabItem",
                "NetUIRibbonTab", new RectI(72, 60, 64, 30), true, false, "Win32",
                SupportedPatterns: ["SelectionItem"], IsSelected: true));
        var launcher = new AutomationObservation(
            "number-dialog", "number-group", "NumberDialogLauncher", "Number Settings",
            "ControlType.Button", "NetUISimpleButton", new RectI(930, 200, 20, 20),
            true, false, "Win32", SupportedPatterns: ["Invoke"]);
        var frame = new FrameObservation(1, DateTimeOffset.UnixEpoch, "", window,
            [activeTab.Observation, launcher], false, "ok", "materialized");

        var dialogs = AutoRibbonDialogLauncherDiscovery.Discover(frame, activeTab);
        var chevrons = AutoRibbonCommandDiscovery.Discover(frame, activeTab);

        Assert.Single(dialogs);
        Assert.Equal("number-dialog", dialogs[0].Observation.RuntimeId);
        Assert.DoesNotContain(chevrons, candidate => candidate.Observation.RuntimeId == "number-dialog");
    }

    [Fact]
    public void DialogLauncherDiscoveryUsesRealExcelAlignmentIdentityButRejectsClipboardPaneButton()
    {
        var window = new WindowObservation(100, 100, 7, "XLMAIN", "Workbook",
            new RectI(0, 0, 1536, 768), true, true, false, false, 96);
        var activeTab = new AutoTabCandidate("home", "Home", true, false,
            new AutomationObservation("home", "root", "TabHome", "Home", "ControlType.TabItem",
                "NetUIRibbonTab", new RectI(73, 60, 70, 38), true, false, "Win32",
                SupportedPatterns: ["SelectionItem"], IsSelected: true));
        var alignment = new AutomationObservation("alignment-dialog", "alignment-group", "CellAlignmentOptions",
            "Format Cell Alignment", "ControlType.Button", "NetUISimpleButton", new RectI(795, 200, 20, 20),
            true, false, "Win32", SupportedPatterns: ["Invoke"]);
        var clipboard = new AutomationObservation("clipboard-pane", "clipboard-group", "ShowClipboard",
            "Office Clipboard...", "ControlType.Button", "NetUISimpleButton", new RectI(129, 200, 20, 20),
            true, false, "Win32", SupportedPatterns: ["Invoke"]);
        var frame = new FrameObservation(1, DateTimeOffset.UnixEpoch, "", window,
            [activeTab.Observation, alignment, clipboard], false, "ok", "materialized");

        var dialogs = AutoRibbonDialogLauncherDiscovery.Discover(frame, activeTab);

        Assert.Contains(dialogs, candidate => candidate.Observation.RuntimeId == "alignment-dialog");
        Assert.DoesNotContain(dialogs, candidate => candidate.Observation.RuntimeId == "clipboard-pane");
    }

    [Fact]
    public void DiscoverRejectsStandaloneOpenButtonThatCanLaunchAModalDialog()
    {
        var window = new WindowObservation(100, 100, 7, "XLMAIN", "Workbook",
            new RectI(0, 0, 1440, 900), true, true, false, false, 96);
        var activeTab = new AutoTabCandidate("layout", "Page Layout", true, false,
            new AutomationObservation("layout", "root", "TabPageLayout", "Page Layout", "ControlType.TabItem",
                "NetUIRibbonTab", new RectI(200, 110, 100, 30), true, false, "Win32",
                SupportedPatterns: ["SelectionItem"], IsSelected: true));
        var frame = new FrameObservation(1, DateTimeOffset.UnixEpoch, "", window,
            [
                activeTab.Observation,
                new AutomationObservation("background", "group", "Background", "Background", "ControlType.Button",
                    "NetUIRibbonButton", new RectI(780, 150, 80, 52), true, false, "Win32", SupportedPatterns: ["Invoke"]),
                new AutomationObservation("modal-open", "group", "", "Open", "ControlType.Button",
                    "NetUIStickyButton", new RectI(835, 153, 16, 28), true, false, "Win32", SupportedPatterns: ["Invoke"])
            ], false, "ok", "materialized");

        var discovered = AutoRibbonCommandDiscovery.Discover(frame, activeTab);

        Assert.DoesNotContain(discovered, item => item.Observation.RuntimeId == "modal-open");
    }

    [Fact]
    public void DiscoverFindsOnlyChevronStyleRibbonControlsUnderTheActiveTab()
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
        var activeTab = new AutoTabCandidate(
            "home",
            "Home",
            true,
            false,
            new AutomationObservation("home", "root", "TabHome", "Home", "ControlType.TabItem", "NetUIRibbonTab", new RectI(72, 110, 64, 30), true, false, "Win32", SupportedPatterns: ["SelectionItem"], IsSelected: true));
        var frame = new FrameObservation(
            1,
            DateTimeOffset.UnixEpoch,
            "",
            window,
            [
                activeTab.Observation,
                new AutomationObservation("insert", "root", "TabInsert", "Insert", "ControlType.TabItem", "NetUIRibbonTab", new RectI(146, 110, 70, 30), true, false, "Win32", SupportedPatterns: ["SelectionItem"]),
                new AutomationObservation("paste-anchor", "group", "", "Paste", "ControlType.SplitButton", "NetUISplitButtonAnchor", new RectI(54, 152, 56, 56), true, false, "Win32", SupportedPatterns: ["ExpandCollapse", "Invoke"]),
                new AutomationObservation("paste-main", "paste-anchor", "Paste", "Paste", "ControlType.Button", "NetUIRibbonButton", new RectI(54, 152, 56, 28), true, false, "Win32", SupportedPatterns: ["Invoke"]),
                new AutomationObservation("paste-chevron", "paste-anchor", "PasteMenu_Dropdown", "More Options", "ControlType.MenuItem", "NetUIRibbonButton", new RectI(54, 180, 56, 28), true, false, "Win32", SupportedPatterns: ["ExpandCollapse"]),
                new AutomationObservation("font-box", "group", "Font", "Font", "ControlType.ComboBox", "NetUIComboboxAnchor", new RectI(122, 152, 84, 24), true, false, "Win32", SupportedPatterns: ["ExpandCollapse"]),
                new AutomationObservation("font-open", "font-box", "", "Open", "ControlType.Button", "NetUIStickyButton", new RectI(188, 153, 16, 22), true, false, "Win32", SupportedPatterns: ["Invoke"]),
                new AutomationObservation("orientation", "group", "OrientationMenu", "Orientation", "ControlType.MenuItem", "NetUIAnchor", new RectI(218, 152, 64, 28), true, false, "Win32", SupportedPatterns: ["ExpandCollapse"]),
                new AutomationObservation("formula", "root", "FormulaBar", "Formula Bar", "ControlType.Edit", "Edit", new RectI(300, 270, 240, 26), true, false, "Win32"),
                new AutomationObservation("search", "root", "TellMeControlAnchor", "Search", "ControlType.MenuItem", "NetUISearchBoxAnchor", new RectI(500, 42, 340, 34), true, false, "Win32")
            ],
            false,
            "ok",
            "materialized");

        var discovered = AutoRibbonCommandDiscovery.Discover(frame, activeTab);

        Assert.Equal(["paste-chevron", "font-open", "orientation"], discovered.Select(item => item.Observation.RuntimeId));
        Assert.DoesNotContain(discovered, item => item.DisplayName == "Formula Bar");
        Assert.DoesNotContain(discovered, item => item.DisplayName == "Search");
        Assert.Contains(discovered, item => item.DisplayName == "Orientation chevron");
    }

    [Fact]
    public void DiscoverReturnsEmptyForNonRibbonMenuSurfaces()
    {
        var window = new WindowObservation(
            100,
            100,
            7,
            "Notepad",
            "Untitled - Notepad",
            new RectI(0, 0, 800, 600),
            true,
            true,
            false,
            false,
            96);
        var activeTab = new AutoTabCandidate(
            "file",
            "File",
            true,
            false,
            new AutomationObservation("file", "root", "MenuFile", "File", "ControlType.MenuItem", "MenuItem", new RectI(10, 30, 32, 24), true, false, "Win32", SupportedPatterns: ["Invoke"], IsSelected: true));
        var frame = new FrameObservation(
            1,
            DateTimeOffset.UnixEpoch,
            "",
            window,
            [
                activeTab.Observation,
                new AutomationObservation("edit", "root", "MenuEdit", "Edit", "ControlType.MenuItem", "MenuItem", new RectI(48, 30, 32, 24), true, false, "Win32", SupportedPatterns: ["Invoke"]),
                new AutomationObservation("new", "popup", "New", "New", "ControlType.MenuItem", "MenuItem", new RectI(10, 58, 180, 24), true, false, "Win32", SupportedPatterns: ["Invoke"]),
                new AutomationObservation("open", "popup", "Open", "Open", "ControlType.MenuItem", "MenuItem", new RectI(10, 82, 180, 24), true, false, "Win32", SupportedPatterns: ["Invoke"])
            ],
            false,
            "ok",
            "materialized");

        var discovered = AutoRibbonCommandDiscovery.Discover(frame, activeTab);

        Assert.Empty(discovered);
    }

    [Fact]
    public void DiscoverFindsComboAndSplitChevronVariantsAcrossTheRibbon()
    {
        var window = new WindowObservation(
            100,
            100,
            7,
            "XLMAIN",
            "Workbook",
            new RectI(0, 0, 1900, 980),
            true,
            true,
            false,
            false,
            96);
        var activeTab = new AutoTabCandidate(
            "home",
            "Home",
            true,
            false,
            new AutomationObservation("home", "root", "TabHome", "Home", "ControlType.TabItem", "NetUIRibbonTab", new RectI(64, 104, 70, 30), true, false, "Win32", SupportedPatterns: ["SelectionItem"], IsSelected: true));
        var frame = new FrameObservation(
            1,
            DateTimeOffset.UnixEpoch,
            "",
            window,
            [
                activeTab.Observation,
                new AutomationObservation("font-family", "root", "FontName", "Aptos Narrow", "ControlType.ComboBox", "NetUIComboboxAnchor", new RectI(148, 150, 142, 32), true, false, "Win32", SupportedPatterns: ["ExpandCollapse"]),
                new AutomationObservation("font-family-chevron", "font-family", "", "", "ControlType.Button", "NetUIStickyButton", new RectI(266, 150, 22, 30), true, false, "Win32", SupportedPatterns: ["Invoke"]),
                new AutomationObservation("font-size", "root", "FontSize", "11", "ControlType.ComboBox", "NetUIComboboxAnchor", new RectI(294, 150, 64, 32), true, false, "Win32", SupportedPatterns: ["ExpandCollapse"]),
                new AutomationObservation("font-size-chevron", "font-size", "", "", "ControlType.Button", "NetUIStickyButton", new RectI(338, 150, 18, 30), true, false, "Win32", SupportedPatterns: ["Invoke"]),
                new AutomationObservation("number-format", "root", "NumberFormat", "General", "ControlType.ComboBox", "NetUIComboboxAnchor", new RectI(820, 150, 184, 32), true, false, "Win32", SupportedPatterns: ["ExpandCollapse"]),
                new AutomationObservation("delete", "root", "Delete", "Delete", "ControlType.SplitButton", "NetUISplitButtonAnchor", new RectI(1320, 146, 78, 58), true, false, "Win32", SupportedPatterns: ["ExpandCollapse", "Invoke"]),
                new AutomationObservation("delete-chevron", "delete", "", "", "ControlType.MenuItem", "NetUIRibbonButton", new RectI(1342, 176, 24, 24), true, false, "Win32", SupportedPatterns: ["ExpandCollapse"]),
                new AutomationObservation("format", "root", "Format", "Format", "ControlType.SplitButton", "NetUISplitButtonAnchor", new RectI(1404, 146, 74, 58), true, false, "Win32", SupportedPatterns: ["ExpandCollapse", "Invoke"]),
                new AutomationObservation("format-chevron", "format", "", "", "ControlType.MenuItem", "NetUIRibbonButton", new RectI(1450, 176, 24, 24), true, false, "Win32", SupportedPatterns: ["ExpandCollapse"]),
                new AutomationObservation("orientation", "root", "OrientationMenu", "Orientation", "ControlType.MenuItem", "NetUIAnchor", new RectI(420, 150, 70, 30), true, false, "Win32", SupportedPatterns: ["ExpandCollapse"]),
                new AutomationObservation("formula", "root", "FormulaBar", "Formula Bar", "ControlType.Edit", "Edit", new RectI(320, 278, 250, 28), true, false, "Win32")
            ],
            false,
            "ok",
            "materialized");

        var discovered = AutoRibbonCommandDiscovery.Discover(frame, activeTab);

        Assert.Contains(discovered, item => item.DisplayName == "Aptos Narrow chevron");
        Assert.Contains(discovered, item => item.DisplayName == "11 chevron");
        Assert.Contains(discovered, item => item.DisplayName == "General chevron");
        Assert.Contains(discovered, item => item.DisplayName == "Delete chevron");
        Assert.Contains(discovered, item => item.DisplayName == "Format chevron");
        Assert.DoesNotContain(discovered, item => item.DisplayName == "Orientation");
        Assert.DoesNotContain(discovered, item => item.DisplayName == "Formula Bar");
    }

    [Fact]
    public void DiscoverDoesNotTreatStickyPrimaryLeafAsChevron()
    {
        var window = new WindowObservation(
            100,
            100,
            7,
            "XLMAIN",
            "Workbook",
            new RectI(0, 0, 1200, 800),
            true,
            true,
            false,
            false,
            96);
        var activeTab = new AutoTabCandidate(
            "home",
            "Home",
            true,
            false,
            new AutomationObservation("home", "root", "TabHome", "Home", "ControlType.TabItem", "NetUIRibbonTab", new RectI(64, 104, 70, 30), true, false, "Win32", SupportedPatterns: ["SelectionItem"], IsSelected: true));
        var frame = new FrameObservation(
            1,
            DateTimeOffset.UnixEpoch,
            "",
            window,
            [
                activeTab.Observation,
                new AutomationObservation("borders-host", "root", "Borders", "Borders", "ControlType.SplitButton", "NetUISplitButtonAnchor", new RectI(236, 152, 54, 30), true, false, "Win32", SupportedPatterns: ["ExpandCollapse", "Invoke"]),
                new AutomationObservation("borders-main", "borders-host", "", "", "ControlType.Button", "NetUIStickyButton", new RectI(236, 152, 28, 28), true, false, "Win32", SupportedPatterns: ["Invoke"]),
                new AutomationObservation("borders-chevron", "borders-host", "", "", "ControlType.Button", "NetUIStickyButton", new RectI(264, 152, 22, 28), true, false, "Win32", SupportedPatterns: ["Invoke"])
            ],
            false,
            "ok",
            "materialized");

        var discovered = AutoRibbonCommandDiscovery.Discover(frame, activeTab);

        Assert.Single(discovered);
        Assert.Equal("Borders chevron", discovered[0].DisplayName);
        Assert.Equal("borders-chevron", discovered[0].Observation.RuntimeId);
    }

    [Fact]
    public void DiscoverDoesNotTreatLeftSplitLeafAsChevronWhenChevronIsSeparateSibling()
    {
        var window = new WindowObservation(
            100,
            100,
            7,
            "XLMAIN",
            "Workbook",
            new RectI(0, 0, 1200, 800),
            true,
            true,
            false,
            false,
            96);
        var activeTab = new AutoTabCandidate(
            "home",
            "Home",
            true,
            false,
            new AutomationObservation("home", "root", "TabHome", "Home", "ControlType.TabItem", "NetUIRibbonTab", new RectI(64, 104, 70, 30), true, false, "Win32", SupportedPatterns: ["SelectionItem"], IsSelected: true));
        var frame = new FrameObservation(
            1,
            DateTimeOffset.UnixEpoch,
            "",
            window,
            [
                activeTab.Observation,
                new AutomationObservation("font-color-host", "root", "FontColor", "Font Color", "ControlType.SplitButton", "NetUISplitButtonAnchor", new RectI(36, 146, 40, 30), true, false, "Win32", SupportedPatterns: ["ExpandCollapse", "Invoke"]),
                new AutomationObservation("font-color-main", "font-color-host", "", "", "ControlType.Button", "NetUIStickyButton", new RectI(36, 146, 24, 30), true, false, "Win32", SupportedPatterns: ["Invoke"]),
                new AutomationObservation("font-color-chevron", "font-color-host", "", "", "ControlType.Button", "NetUIStickyButton", new RectI(60, 146, 16, 30), true, false, "Win32", SupportedPatterns: ["Invoke"])
            ],
            false,
            "ok",
            "materialized");

        var discovered = AutoRibbonCommandDiscovery.Discover(frame, activeTab);

        Assert.Single(discovered);
        Assert.Equal("Font Color chevron", discovered[0].DisplayName);
        Assert.Equal("font-color-chevron", discovered[0].Observation.RuntimeId);
    }

    [Fact]
    public void DiscoverDoesNotDuplicateDropdownHostWhenDedicatedChevronChildExists()
    {
        var window = new WindowObservation(100, 100, 7, "XLMAIN", "Workbook",
            new RectI(0, 0, 1200, 800), true, true, false, false, 96);
        var activeTab = new AutoTabCandidate("insert", "Insert", true, false,
            new AutomationObservation("insert", "root", "TabInsert", "Insert", "ControlType.TabItem",
                "NetUIRibbonTab", new RectI(140, 60, 70, 38), true, false, "Win32",
                SupportedPatterns: ["SelectionItem"], IsSelected: true));
        var host = new AutomationObservation("pivot-host", "group", "InsertPivotTableDropdown", "PivotTable",
            "ControlType.SplitButton", "NetUISplitButtonAnchor", new RectI(25, 102, 74, 96),
            true, false, "Win32", SupportedPatterns: ["ExpandCollapse", "Invoke"]);
        var chevron = new AutomationObservation("pivot-chevron", "pivot-host", "InsertPivotTableDropdown_Dropdown", "More Options",
            "ControlType.MenuItem", "NetUIRibbonButton", new RectI(25, 148, 74, 50),
            true, false, "Win32", SupportedPatterns: ["ExpandCollapse"]);
        var frame = new FrameObservation(1, DateTimeOffset.UnixEpoch, "", window,
            [activeTab.Observation, host, chevron], false, "ok", "materialized");

        var discovered = AutoRibbonCommandDiscovery.Discover(frame, activeTab);

        var candidate = Assert.Single(discovered);
        Assert.Equal("pivot-chevron", candidate.Observation.RuntimeId);
    }

    [Fact]
    public void DiscoverFindsExpandableRibbonButtonHostsWithoutDedicatedChevronChildren()
    {
        var window = new WindowObservation(
            100,
            100,
            7,
            "XLMAIN",
            "Workbook",
            new RectI(0, 0, 1500, 900),
            true,
            true,
            false,
            false,
            96);
        var activeTab = new AutoTabCandidate(
            "home",
            "Home",
            true,
            false,
            new AutomationObservation("home", "root", "TabHome", "Home", "ControlType.TabItem", "NetUIRibbonTab", new RectI(64, 104, 70, 30), true, false, "Win32", SupportedPatterns: ["SelectionItem"], IsSelected: true));
        var frame = new FrameObservation(
            1,
            DateTimeOffset.UnixEpoch,
            "",
            window,
            [
                activeTab.Observation,
                new AutomationObservation("conditional", "root", "ConditionalFormatting", "Conditional Formatting", "ControlType.MenuItem", "NetUIRibbonButton", new RectI(42, 150, 70, 60), true, false, "Win32", SupportedPatterns: ["ExpandCollapse"]),
                new AutomationObservation("format-table", "root", "FormatAsTable", "Format as Table", "ControlType.MenuItem", "NetUIRibbonButton", new RectI(132, 150, 70, 60), true, false, "Win32", SupportedPatterns: ["ExpandCollapse"]),
                new AutomationObservation("cell-styles", "root", "CellStyles", "Cell Styles", "ControlType.Button", "NetUIRibbonButton", new RectI(218, 150, 54, 60), true, false, "Win32", SupportedPatterns: ["ExpandCollapse"]),
                new AutomationObservation("delete", "root", "Delete", "Delete", "ControlType.MenuItem", "NetUIRibbonButton", new RectI(358, 150, 56, 60), true, false, "Win32", SupportedPatterns: ["ExpandCollapse"]),
                new AutomationObservation("format", "root", "Format", "Format", "ControlType.MenuItem", "NetUIRibbonButton", new RectI(420, 150, 56, 60), true, false, "Win32", SupportedPatterns: ["ExpandCollapse"])
            ],
            false,
            "ok",
            "materialized");

        var discovered = AutoRibbonCommandDiscovery.Discover(frame, activeTab);

        Assert.Contains(discovered, item => item.DisplayName == "Conditional Formatting chevron");
        Assert.Contains(discovered, item => item.DisplayName == "Format as Table chevron");
        Assert.Contains(discovered, item => item.DisplayName == "Cell Styles chevron");
        Assert.Contains(discovered, item => item.DisplayName == "Delete chevron");
        Assert.Contains(discovered, item => item.DisplayName == "Format chevron");
    }

    [Fact]
    public void DiscoverFindsTallAnchorHostsWithIntegratedChevron()
    {
        var window = new WindowObservation(
            100,
            100,
            7,
            "XLMAIN",
            "Workbook",
            new RectI(0, 0, 1500, 900),
            true,
            true,
            false,
            false,
            96);
        var activeTab = new AutoTabCandidate(
            "home",
            "Home",
            true,
            false,
            new AutomationObservation("home", "root", "TabHome", "Home", "ControlType.TabItem", "NetUIRibbonTab", new RectI(64, 104, 70, 30), true, false, "Win32", SupportedPatterns: ["SelectionItem"], IsSelected: true));
        var frame = new FrameObservation(
            1,
            DateTimeOffset.UnixEpoch,
            "",
            window,
            [
                activeTab.Observation,
                new AutomationObservation("sort-filter", "root", "SortFilterMenu", "Sort & Filter", "ControlType.MenuItem", "NetUIAnchor", new RectI(542, 146, 72, 60), true, false, "Win32", SupportedPatterns: ["ExpandCollapse"]),
                new AutomationObservation("find-select", "root", "FindSelectMenu", "Find & Select", "ControlType.MenuItem", "NetUIAnchor", new RectI(624, 146, 78, 60), true, false, "Win32", SupportedPatterns: ["ExpandCollapse"]),
                new AutomationObservation("orientation", "root", "OrientationMenu", "Orientation", "ControlType.MenuItem", "NetUIAnchor", new RectI(420, 150, 70, 30), true, false, "Win32", SupportedPatterns: ["ExpandCollapse"])
            ],
            false,
            "ok",
            "materialized");

        var discovered = AutoRibbonCommandDiscovery.Discover(frame, activeTab);

        Assert.Contains(discovered, item => item.DisplayName == "Sort & Filter chevron");
        Assert.Contains(discovered, item => item.DisplayName == "Find & Select chevron");
        Assert.Contains(discovered, item => item.DisplayName == "Orientation chevron");
    }

    [Fact]
    public void DiscoverFindsCompactExcelMenuAnchorsWithoutChevronChildren()
    {
        var window = new WindowObservation(100, 100, 7, "XLMAIN", "Workbook",
            new RectI(0, 0, 1900, 980), true, true, false, false, 96);
        var activeTab = new AutoTabCandidate("home", "Home", true, false,
            new AutomationObservation("home", "root", "TabHome", "Home", "ControlType.TabItem",
                "NetUIRibbonTab", new RectI(64, 104, 70, 30), true, false, "Win32",
                SupportedPatterns: ["SelectionItem"], IsSelected: true));
        var frame = new FrameObservation(1, DateTimeOffset.UnixEpoch, "", window,
            [
                activeTab.Observation,
                new AutomationObservation("fill", "editing", "FillMenu", "Fill", "ControlType.MenuItem",
                    "NetUIAnchor", new RectI(1452, 134, 50, 30), true, false, "Win32",
                    SupportedPatterns: ["ExpandCollapse"]),
                new AutomationObservation("clear", "editing", "ClearMenu", "Clear", "ControlType.MenuItem",
                    "NetUIAnchor", new RectI(1452, 166, 50, 30), true, false, "Win32",
                    SupportedPatterns: ["ExpandCollapse"])
            ], false, "ok", "materialized");

        var discovered = AutoRibbonCommandDiscovery.Discover(frame, activeTab);

        Assert.Contains(discovered, item => item.DisplayName == "Fill chevron");
        Assert.Contains(discovered, item => item.DisplayName == "Clear chevron");
    }
}
