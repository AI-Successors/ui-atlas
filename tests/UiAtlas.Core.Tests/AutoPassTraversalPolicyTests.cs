using UiAtlas.Core.Cli;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Recording.Windows;

namespace UiAtlas.Core.Tests;

public sealed class AutoPassTraversalPolicyTests
{
    [Fact]
    public void RibbonCommandsAlwaysRunLeftToRightThenTopToBottomRegardlessOfPredictionOrder()
    {
        AutoRibbonCommandCandidate[] candidates =
        [
            Command("far-right", 620, 70),
            Command("left-lower", 40, 110),
            Command("middle", 310, 80),
            Command("left-upper", 40, 55)
        ];

        var ordered = AutoPassTraversalPolicy.OrderCommandsInVisualSequence(candidates);

        Assert.Equal(["left-upper", "left-lower", "middle", "far-right"],
            ordered.Select(candidate => candidate.StableKey));
    }

    [Fact]
    public void DialogLaunchersUseTheSameStableVisualTraversal()
    {
        AutoRibbonDialogLauncherCandidate[] candidates =
        [
            DialogLauncher("right", 700, 120),
            DialogLauncher("left-lower", 100, 140),
            DialogLauncher("left-upper", 100, 90),
            DialogLauncher("middle", 400, 120)
        ];

        var ordered = AutoPassTraversalPolicy.OrderDialogLaunchersInVisualSequence(candidates);

        Assert.Equal(["left-upper", "left-lower", "middle", "right"],
            ordered.Select(candidate => candidate.StableKey));
    }

    [Fact]
    public void TopLevelNavigationAlwaysRunsLeftToRightRegardlessOfInputOrder()
    {
        AutoTabCandidate[] candidates =
        [
            Tab("reports", isSelected: false, x: 500),
            Tab("shifts", isSelected: true, x: 350),
            Tab("reservations", isSelected: false, x: 20),
            Tab("rooms", isSelected: false, x: 180)
        ];

        var ordered = AutoPassTraversalPolicy.OrderTabsInVisualSequence(candidates);

        Assert.Equal(["reservations", "rooms", "shifts", "reports"],
            ordered.Select(candidate => candidate.StableKey));
    }

    [Fact]
    public void BackstageRemainsLastEvenWhenItIsLeftmost()
    {
        var file = Tab("file", isSelected: false, x: 5) with { IsBackstage = true };

        var ordered = AutoPassTraversalPolicy.OrderTabsInVisualSequence(
            [file, Tab("home", isSelected: false, x: 60), Tab("insert", isSelected: false, x: 140)]);

        Assert.Equal(["home", "insert", "file"], ordered.Select(candidate => candidate.StableKey));
    }

    [Fact]
    public void InitiallyActiveTabStillRequiresAnExplicitAutoPassVisit()
    {
        var recorded = new[] { "home", "insert" };

        var visited = AutoPassTraversalPolicy.PrepareVisitedTabsForExplicitSweep(recorded, "home");

        Assert.DoesNotContain("home", visited);
        Assert.Contains("insert", visited);
    }

    [Fact]
    public void ActiveTabChevronsRunBeforeTheNextTab()
    {
        var visited = new HashSet<string>(["home"], StringComparer.Ordinal);
        var swept = new HashSet<string>(StringComparer.Ordinal);

        Assert.True(AutoPassTraversalPolicy.ShouldSweepCommandsForActiveTab("home", visited, swept));
        Assert.False(AutoPassTraversalPolicy.ShouldSweepCommandsForActiveTab("insert", visited, swept));

        swept.Add("home");
        Assert.False(AutoPassTraversalPolicy.ShouldSweepCommandsForActiveTab("home", visited, swept));
    }

    [Fact]
    public void ExplicitlyActivatedTabWinsOverStaleSelectedProviderState()
    {
        AutoTabCandidate[] tabs =
        [
            Tab("home", isSelected: true, x: 20),
            Tab("insert", isSelected: false, x: 100)
        ];

        var active = AutoPassTraversalPolicy.ResolveActiveTab(tabs, "home", "insert");

        Assert.NotNull(active);
        Assert.Equal("insert", active.StableKey);
    }

    [Fact]
    public void UnknownInitialSelectionDoesNotPretendFirstTabIsActive()
    {
        AutoTabCandidate[] tabs =
        [
            Tab("create", isSelected: false, x: 20),
            Tab("modify", isSelected: false, x: 100)
        ];

        var active = AutoPassTraversalPolicy.ResolveActiveTab(
            tabs, TabHighlightLayerResolver.GlobalLayerKey, explicitlyActivatedTabKey: null);

        Assert.Null(active);
    }

    private static AutoTabCandidate Tab(string key, bool isSelected, int x) =>
        new(key, key, isSelected, false,
            Observation(key, new RectI(x, 20, 70, 30), "ControlType.TabItem", "NetUIRibbonTab"));

    private static AutoRibbonCommandCandidate Command(string key, int x, int y) =>
        new(key, key, Observation(key, new RectI(x, y, 20, 20), "ControlType.Button", "NetUISimpleButton"));

    private static AutoRibbonDialogLauncherCandidate DialogLauncher(string key, int x, int y) =>
        new(key, key, Observation(key, new RectI(x, y, 12, 12), "ControlType.Button", "NetUISimpleButton"));

    private static AutomationObservation Observation(string key, RectI bounds, string controlType, string className) =>
        new(key, "", key, key, controlType, className, bounds, true, false, "Win32", 1);
}
