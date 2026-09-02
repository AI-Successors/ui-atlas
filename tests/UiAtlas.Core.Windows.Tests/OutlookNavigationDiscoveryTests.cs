using UiAtlas.Core.Contracts;
using UiAtlas.Core.Recording.Windows;

namespace UiAtlas.Core.Windows.Tests;

public sealed class OutlookNavigationDiscoveryTests
{
    [Fact]
    public void DiscoverReturnsEveryBottomModuleButtonIncludingEllipsis()
    {
        var frame = Frame("rctrl_renwnd32",
        [
            Button("mail", "Mail", 20),
            Button("calendar", "Calendar", 68),
            Button("people", "People", 116),
            Button("tasks", "Tasks", 164, selected: true),
            Button("more", "", 212, automationId: "NavigationOptions"),
            new AutomationObservation("filter", "status", "", "Filter applied", "ControlType.Text", "",
                new RectI(20, 873, 78, 22), true, false, "Win32", 100),
            new AutomationObservation("reminders", "status", "", "Reminders: 3", "ControlType.Button", "",
                new RectI(110, 873, 100, 22), true, false, "Win32", 100)
        ]);

        var discovered = OutlookNavigationDiscovery.Discover(frame);

        Assert.Equal(["Mail", "Calendar", "People", "Tasks", "NavigationOptions"],
            discovered.Select(candidate => candidate.DisplayName));
        Assert.True(discovered.Single(candidate => candidate.DisplayName == "Tasks").IsSelected);
        Assert.True(discovered[^1].OpensPopup);
    }

    [Fact]
    public void DiscoverRejectsSimilarBottomButtonsOutsideOutlook()
    {
        var frame = Frame("XLMAIN",
        [
            Button("mail", "Mail", 20),
            Button("calendar", "Calendar", 68),
            Button("people", "People", 116),
            Button("tasks", "Tasks", 164),
            Button("more", "More", 212)
        ], title: "Book1 - Excel");

        Assert.Empty(OutlookNavigationDiscovery.Discover(frame));
    }

    [Fact]
    public void DiscoverRequiresARecognizableModuleRow()
    {
        var frame = Frame("rctrl_renwnd32",
        [
            Button("one", "Previous", 20),
            Button("two", "Next", 68),
            Button("three", "Zoom out", 116),
            Button("four", "Zoom in", 164)
        ]);

        Assert.Empty(OutlookNavigationDiscovery.Discover(frame));
    }

    [Fact]
    public void ResolveActiveUsesCurrentOutlookTitleInsteadOfStaleSelectionState()
    {
        var frame = Frame("rctrl_renwnd32",
        [
            Button("mail", "Mail", 20, selected: true),
            Button("calendar", "Calendar", 68),
            Button("people", "People", 116),
            Button("tasks", "Tasks", 164),
            Button("more", "More", 212)
        ], title: "Contacts - account@example.com - Outlook");

        var active = OutlookNavigationDiscovery.ResolveActive(frame);

        Assert.NotNull(active);
        Assert.Equal("People", active.DisplayName);
    }

    private static FrameObservation Frame(
        string className,
        IReadOnlyList<AutomationObservation> controls,
        string title = "Inbox - Microsoft Outlook") =>
        new(1, DateTimeOffset.UnixEpoch, "",
            new WindowObservation(100, 100, 7, className, title, new RectI(0, 0, 1400, 900),
                true, true, false, false, 96),
            controls, false, "ok", "initial");

    private static AutomationObservation Button(
        string runtimeId,
        string name,
        int x,
        string? automationId = null,
        bool selected = false) =>
        new(runtimeId, "module-switcher", automationId ?? runtimeId, name,
            "ControlType.Button", "NetUIRibbonButton", new RectI(x, 820, 42, 42),
            true, false, "Win32", 100, ["InvokePatternIdentifiers.Pattern"], IsSelected: selected);
}
