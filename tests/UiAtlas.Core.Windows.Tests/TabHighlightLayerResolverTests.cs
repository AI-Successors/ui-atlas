using UiAtlas.Core.Contracts;
using UiAtlas.Core.Recording.Windows;

namespace UiAtlas.Core.Windows.Tests;

public sealed class TabHighlightLayerResolverTests
{
    [Fact]
    public void ResolveLayerKey_ReturnsSelectedTabForRibbonBounds()
    {
        var frame = CreateExcelLikeFrame(selectedTabName: "Home");
        var expectedLayerKey = AutoTabDiscovery.Discover(frame).Single(item => item.DisplayName == "Home").StableKey;

        var layerKey = TabHighlightLayerResolver.ResolveLayerKey(
            frame,
            [new RectI(76, 160, 48, 50)]);

        Assert.Equal(expectedLayerKey, layerKey);
    }

    [Fact]
    public void ResolveLayerKey_ReturnsGlobalForTopLevelTabBounds()
    {
        var frame = CreateExcelLikeFrame(selectedTabName: "Home");

        var layerKey = TabHighlightLayerResolver.ResolveLayerKey(
            frame,
            [new RectI(68, 110, 58, 26)]);

        Assert.Equal(TabHighlightLayerResolver.GlobalLayerKey, layerKey);
    }

    [Fact]
    public void ResolveLayerKey_ReturnsGlobalForBodyBounds()
    {
        var frame = CreateExcelLikeFrame(selectedTabName: "Home");

        var layerKey = TabHighlightLayerResolver.ResolveLayerKey(
            frame,
            [new RectI(320, 420, 120, 34)]);

        Assert.Equal(TabHighlightLayerResolver.GlobalLayerKey, layerKey);
    }

    [Fact]
    public void ResolveLayerKey_ReturnsSelectedTabForTallRibbonBoundsNearTheTabRow()
    {
        var frame = CreateExcelLikeFrame(selectedTabName: "Home");
        var expectedLayerKey = AutoTabDiscovery.Discover(frame).Single(item => item.DisplayName == "Home").StableKey;

        var layerKey = TabHighlightLayerResolver.ResolveLayerKey(
            frame,
            [new RectI(364, 136, 96, 74)]);

        Assert.Equal(expectedLayerKey, layerKey);
    }

    [Fact]
    public void ResolveVisibleLayerKey_ReturnsSelectedTabStableKey()
    {
        var frame = CreateExcelLikeFrame(selectedTabName: "Insert");
        var expectedLayerKey = AutoTabDiscovery.Discover(frame).Single(item => item.DisplayName == "Insert").StableKey;

        var layerKey = TabHighlightLayerResolver.ResolveVisibleLayerKey(frame.Window, frame.Automation);

        Assert.Equal(expectedLayerKey, layerKey);
    }

    [Fact]
    public void ResolveLayerKey_UsesFallbackLayerForRibbonBoundsWhenSelectionIsUnavailable()
    {
        var frame = CreateExcelLikeFrame(selectedTabName: null);
        const string fallbackLayerKey = "draw-layer";

        var layerKey = TabHighlightLayerResolver.ResolveLayerKey(
            frame,
            [new RectI(364, 136, 96, 74)],
            fallbackLayerKey);

        Assert.Equal(fallbackLayerKey, layerKey);
    }

    [Fact]
    public void ResolveVisibleLayerKey_ReturnsClickedTabWhenSelectionIsUnavailable()
    {
        var frame = CreateExcelLikeFrame(selectedTabName: null);
        var expectedLayerKey = AutoTabDiscovery.Discover(frame).Single(item => item.DisplayName == "Draw").StableKey;

        var layerKey = TabHighlightLayerResolver.ResolveVisibleLayerKey(
            frame,
            [new RectI(220, 108, 58, 30)],
            fallbackLayerKey: "home-layer");

        Assert.Equal(expectedLayerKey, layerKey);
    }

    [Fact]
    public void ResolveVisibleLayerKey_PreservesFallbackWhenSelectionIsUnavailable()
    {
        var frame = CreateExcelLikeFrame(selectedTabName: null);
        const string fallbackLayerKey = "home-layer";

        var layerKey = TabHighlightLayerResolver.ResolveVisibleLayerKey(
            frame.Window,
            frame.Automation,
            fallbackLayerKey);

        Assert.Equal(fallbackLayerKey, layerKey);
    }

    [Fact]
    public void OutlookBodyHighlightUsesCurrentModuleLayer()
    {
        var window = new WindowObservation(
            100, 100, 7, "rctrl_renwnd32", "Contacts - account@example.com - Outlook",
            new RectI(0, 0, 1400, 900), true, true, false, false, 96);
        AutomationObservation[] controls =
        [
            CreateTab("home", "Home", new RectI(64, 60, 66, 30), "Home"),
            new("mail", "switcher", "mail", "Mail", "ControlType.Button", "NetUI",
                new RectI(20, 820, 42, 42), true, false, "Win32", 100),
            new("calendar", "switcher", "calendar", "Calendar", "ControlType.Button", "NetUI",
                new RectI(68, 820, 42, 42), true, false, "Win32", 100),
            new("people", "switcher", "people", "People", "ControlType.Button", "NetUI",
                new RectI(116, 820, 42, 42), true, false, "Win32", 100),
            new("tasks", "switcher", "tasks", "Tasks", "ControlType.Button", "NetUI",
                new RectI(164, 820, 42, 42), true, false, "Win32", 100),
            new("more", "switcher", "more", "More", "ControlType.Button", "NetUI",
                new RectI(212, 820, 42, 42), true, false, "Win32", 100)
        ];
        var frame = new FrameObservation(1, DateTimeOffset.UnixEpoch, "", window, controls,
            false, "ok", "materialized");
        var activeModule = OutlookNavigationDiscovery.ResolveActive(frame)!;

        var bodyLayer = TabHighlightLayerResolver.ResolveLayerKey(
            frame, [new RectI(700, 400, 120, 30)]);
        var visibleLayer = TabHighlightLayerResolver.ResolveVisibleLayerKey(frame.Window, frame.Automation);

        Assert.Equal(OutlookNavigationDiscovery.ModuleLayerKey(activeModule), bodyLayer);
        Assert.True(OutlookNavigationDiscovery.TryGetModuleLayerKey(visibleLayer, out var visibleModule));
        Assert.Equal(bodyLayer, visibleModule);
    }

    private static FrameObservation CreateExcelLikeFrame(string? selectedTabName)
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

        return new FrameObservation(
            1,
            DateTimeOffset.UnixEpoch,
            "",
            window,
            [
                CreateTab("home", "Home", new RectI(64, 108, 66, 30), selectedTabName),
                CreateTab("insert", "Insert", new RectI(140, 108, 70, 30), selectedTabName),
                CreateTab("draw", "Draw", new RectI(220, 108, 58, 30), selectedTabName),
                new AutomationObservation("paste", "home-group", "PasteMenu_Dropdown", "Paste", "ControlType.MenuItem", "NetUIRibbonButton", new RectI(72, 154, 54, 54), true, false, "Win32", SupportedPatterns: ["ExpandCollapse"]),
                new AutomationObservation("font", "home-group", "Font", "Font", "ControlType.ComboBox", "NetUIComboboxAnchor", new RectI(144, 156, 110, 26), true, false, "Win32", SupportedPatterns: ["ExpandCollapse"]),
                new AutomationObservation("formula", "insert-group", "LookupAndReferenceMenu", "Lookup & Reference", "ControlType.MenuItem", "NetUIRibbonButton", new RectI(330, 154, 64, 54), true, false, "Win32", SupportedPatterns: ["ExpandCollapse"]),
                new AutomationObservation("equation", "draw-group", "Equation", "Equation", "ControlType.Button", "NetUIRibbonButton", new RectI(1080, 154, 52, 54), true, false, "Win32", SupportedPatterns: ["Invoke"]),
                new AutomationObservation("grid", "root", "GridBody", "Worksheet", "ControlType.Pane", "EXCEL7", new RectI(0, 220, 1440, 680), true, false, "Win32")
            ],
            false,
            "ok",
            "materialized");
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
