using UiAtlas.Core.Contracts;
using UiAtlas.Core.Recording.Windows;

namespace UiAtlas.Core.Windows.Tests;

public sealed class AutoTabDiscoveryTests
{
    [Fact]
    public void DiscoverOrdersTopLevelTabsLeftToRightAndPlacesBackstageLast()
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
        var frame = new FrameObservation(
            1,
            DateTimeOffset.UnixEpoch,
            "",
            window,
            [
                new AutomationObservation("save", "root", "FileSave", "Save", "ControlType.Button", "NetUIRibbonButton", new RectI(8, 16, 26, 26), true, false, "Win32"),
                new AutomationObservation("file", "root", "FileTabButton", "File Tab", "ControlType.Button", "NetUIRibbonTab", new RectI(8, 52, 54, 24), true, false, "Win32"),
                new AutomationObservation("home", "root", "HomeTab", "Home", "ControlType.TabItem", "NetUIRibbonTab", new RectI(70, 52, 62, 24), true, false, "Win32", SupportedPatterns: ["SelectionItem"], IsSelected: true),
                new AutomationObservation("insert", "root", "InsertTab", "Insert", "ControlType.TabItem", "NetUIRibbonTab", new RectI(142, 52, 62, 24), true, false, "Win32"),
                new AutomationObservation("draw", "root", "DrawTab", "Draw", "ControlType.TabItem", "NetUIRibbonTab", new RectI(214, 52, 58, 24), true, false, "Win32"),
                new AutomationObservation("disabled", "root", "DisabledTab", "Disabled", "ControlType.TabItem", "NetUIRibbonTab", new RectI(286, 52, 70, 24), false, false, "Win32"),
                new AutomationObservation("lower", "root", "LowerButton", "Not a tab", "ControlType.Button", "Button", new RectI(70, 260, 90, 28), true, false, "Win32")
            ],
            false,
            "ok",
            "initial");

        var discovered = AutoTabDiscovery.Discover(frame);

        Assert.Equal(["Home", "Insert", "Draw", "File Tab"], discovered.Select(item => item.DisplayName));
        Assert.True(discovered.Single(item => item.DisplayName == "Home").IsSelected);
        Assert.True(discovered[^1].IsBackstage);
        Assert.DoesNotContain(discovered, item => item.DisplayName == "Save");
    }

    [Fact]
    public void DiscoverPrefersActualTabRowOverToolbarChrome()
    {
        var window = new WindowObservation(
            100,
            100,
            7,
            "XLMAIN",
            "Workbook",
            new RectI(298, 85, 1475, 842),
            true,
            true,
            false,
            false,
            96);
        var frame = new FrameObservation(
            1,
            DateTimeOffset.UnixEpoch,
            "",
            window,
            [
                new AutomationObservation("system", "root", "Item 1", "System", "ControlType.MenuItem", "", new RectI(307, 94, 28, 28), true, false, "Win32"),
                new AutomationObservation("search", "root", "TellMeControlAnchor", "Type to search and use the up and down arrow keys to navigate", "ControlType.MenuItem", "NetUISearchBoxAnchor", new RectI(847, 96, 397, 40), true, false, "Win32"),
                new AutomationObservation("quick", "root", "", "Customize Quick Access Toolbar", "ControlType.MenuItem", "NetUIAnchor", new RectI(618, 97, 34, 38), true, false, "Win32"),
                new AutomationObservation("file", "root", "FileTabButton", "File Tab", "ControlType.Button", "NetUIRibbonTab", new RectI(304, 146, 68, 38), true, false, "Win32", SupportedPatterns: ["Invoke"]),
                new AutomationObservation("home", "root", "TabHome", "Home", "ControlType.TabItem", "NetUIRibbonTab", new RectI(372, 146, 70, 38), true, false, "Win32", SupportedPatterns: ["SelectionItem"]),
                new AutomationObservation("insert", "root", "TabInsert", "Insert", "ControlType.TabItem", "NetUIRibbonTab", new RectI(443, 146, 66, 38), true, false, "Win32", SupportedPatterns: ["SelectionItem"]),
                new AutomationObservation("draw", "root", "TabDrawInk", "Draw", "ControlType.TabItem", "NetUIRibbonTab", new RectI(510, 146, 63, 38), true, false, "Win32", SupportedPatterns: ["SelectionItem"]),
                new AutomationObservation("share", "root", "", "Share", "ControlType.MenuItem", "NetUIAnchor", new RectI(1667, 146, 90, 38), true, false, "Win32"),
                new AutomationObservation("display", "root", "", "Ribbon Display Options", "ControlType.MenuItem", "NetUIAnchor", new RectI(1732, 278, 25, 25), true, false, "Win32")
            ],
            false,
            "ok",
            "initial");

        var discovered = AutoTabDiscovery.Discover(frame);

        Assert.Equal(["Home", "Insert", "Draw", "File Tab"], discovered.Select(item => item.DisplayName));
        Assert.True(discovered[^1].IsBackstage);
        Assert.DoesNotContain(discovered, item => item.DisplayName == "System");
        Assert.DoesNotContain(discovered, item => item.DisplayName.StartsWith("Type to search", StringComparison.Ordinal));
        Assert.DoesNotContain(discovered, item => item.DisplayName == "Share");
    }

    [Fact]
    public void DiscoverTreatsTraditionalApplicationMenuBarAsSafeTopLevelNavigation()
    {
        var window = new WindowObservation(
            100,
            100,
            7,
            "Premiere Pro",
            "Adobe Premiere Pro",
            new RectI(0, 0, 1920, 1040),
            true,
            true,
            false,
            false,
            96);
        var names = new[] { "File", "Edit", "Clip", "Sequence", "Markers", "Graphics and Titles", "View", "Window", "Help" };
        var controls = names.Select((name, index) => new AutomationObservation(
            $"menu.{index}",
            "menubar",
            $"Item {index + 1}",
            name,
            "ControlType.MenuItem",
            "",
            new RectI(index * 68, 29, 64, 24),
            true,
            false,
            "Win32",
            100,
            ["ExpandCollapsePatternIdentifiers.Pattern"])).ToArray();
        var frame = new FrameObservation(1, DateTimeOffset.UnixEpoch, "", window, controls,
            false, "ok", "initial");

        var discovered = AutoTabDiscovery.Discover(frame);

        Assert.Equal(names, discovered.Select(item => item.DisplayName));
        Assert.All(discovered, item => Assert.True(AutoTabDiscovery.IsApplicationMenu(item, window.Bounds)));
    }

    [Fact]
    public void DiscoverTreatsRevitButtonRowAsTabsAndIgnoresQuickAccessToolbar()
    {
        var window = new WindowObservation(100, 100, 7, "Window", "Autodesk Revit",
            new RectI(0, 0, 1900, 1009), true, true, false, false, 96);
        var controls = new[]
        {
            RevitButton("open", "ID_Open", "Open", 70, 7, 30, 28),
            RevitButton("save", "ID_Save", "Save", 100, 7, 30, 28),
            RevitButton("undo", "ID_Undo_HistoryButtonExecute", "Undo", 175, 7, 30, 28),
            RevitButton("architecture", "Architecture", "Architecture", 62, 40, 104, 25),
            RevitButton("structure", "Structure", "Structure", 166, 40, 84, 25),
            RevitButton("concrete", "Concrete", "Concrete", 250, 40, 84, 25),
            RevitButton("steel", "Steel", "Steel", 334, 40, 58, 25),
            RevitButton("systems", "Systems", "Systems", 392, 40, 78, 25)
        };
        var frame = new FrameObservation(1, DateTimeOffset.UnixEpoch, "", window, controls,
            false, "ok", "initial");

        var discovered = AutoTabDiscovery.Discover(frame);

        Assert.Equal(["Architecture", "Structure", "Concrete", "Steel", "Systems"],
            discovered.Select(item => item.DisplayName));
        Assert.DoesNotContain(discovered, item => item.DisplayName is "Open" or "Save" or "Undo");
    }

    [Fact]
    public void DiscoverTreatsSafeLegacyButtonPanesAsNavigationWhenRootOwnerIsHidden()
    {
        var hiddenOwner = new WindowObservation(100, 100, 7, "TApplication", "Legacy app",
            new RectI(-10, -10, 1940, 0), true, true, false, false, 96);
        var visibleSurface = hiddenOwner with
        {
            Hwnd = 101,
            ClassName = "TfrmMain",
            Title = "Legacy app [Trial Version]",
            Bounds = new RectI(3606, 186, 1280, 780)
        };
        var names = new[]
        {
            "Reservations...", "Stays...", "Rooms Calendar", "New Order", "Orders...",
            "Tables Plan", "Clients...", "Shifts...", "End of Day", "Reports...", "Email Sender...", "Log Off"
        };
        var controls = names.Select((name, index) => new AutomationObservation(
            $"legacy.{index}", "", (1000 + index).ToString(), name,
            "ControlType.Pane", "TAbacreButton",
            new RectI(3620 + index * 84, 280, 80, 45),
            true, false, "Win32", 101)).ToArray();
        var frame = new FrameObservation(
            1, DateTimeOffset.UnixEpoch, "", hiddenOwner, controls,
            false, "ok", "initial", [hiddenOwner, visibleSurface]);

        var discovered = AutoTabDiscovery.Discover(frame);

        Assert.Equal(
            ["Reservations...", "Stays...", "Rooms Calendar", "Orders...", "Tables Plan", "Clients...", "Shifts...", "Reports..."],
            discovered.Select(item => item.DisplayName));
        Assert.All(discovered, item => Assert.True(AutoTabDiscovery.IsLegacyNavigationButton(item)));
    }

    [Fact]
    public void BackstageDiscoveryReturnsOnlySafeNavigationPages()
    {
        var window = new WindowObservation(100, 100, 7, "XLMAIN", "Workbook",
            new RectI(0, 0, 1200, 800), true, true, false, false, 96);
        var frame = new FrameObservation(2, DateTimeOffset.UnixEpoch, "", window,
            [
                Backstage("info", "Info", "ControlType.TabItem", 90, isSelected: true),
                Backstage("new", "New", "ControlType.ListItem", 130),
                Backstage("open", "Open", "ControlType.Button", 170, className: "BackstageNavigationButton"),
                Backstage("account", "Account", "ControlType.MenuItem", 210),
                Backstage("save", "Save", "ControlType.TabItem", 250),
                Backstage("options", "Options", "ControlType.Button", 290, className: "BackstageNavigationButton"),
                Backstage("browse", "Browse", "ControlType.Button", 330, className: "BackstageNavigationButton")
            ], false, "ok", "auto-backstage");

        var discovered = AutoTabDiscovery.DiscoverBackstageNavigation(frame);

        Assert.Equal(["Info", "New", "Open", "Account"], discovered.Select(item => item.DisplayName));
        Assert.True(discovered[0].IsSelected);
    }

    [Fact]
    public void BackstageSectionMatchRequiresTheRequestedNavigationItemToBeSelected()
    {
        AutomationObservation[] controls =
        [
            Backstage("new", "New", "ControlType.TabItem", 130, isSelected: true),
            Backstage("open", "Open", "ControlType.TabItem", 170)
        ];

        Assert.True(AutoTabDiscovery.IsBackstageSectionSelected(controls, "New"));
        Assert.False(AutoTabDiscovery.IsBackstageSectionSelected(controls, "Open"));
        Assert.False(AutoTabDiscovery.IsBackstageSectionSelected(controls, "Info"));
    }

    private static AutomationObservation Backstage(
        string id,
        string name,
        string type,
        int y,
        bool isSelected = false,
        string className = "NetUIBackstageTab") =>
        new(id, "backstage", id, name, type, className, new RectI(20, y, 180, 32),
            true, false, "Win32", 100, ["SelectionItem"], IsSelected: isSelected);

    private static AutomationObservation RevitButton(
        string id, string automationId, string name, int x, int y, int width, int height) =>
        new(id, "", automationId, name, "ControlType.Button", "Button",
            new RectI(x, y, width, height), true, false, "WPF", 100,
            ["InvokePatternIdentifiers.Pattern"]);
}
