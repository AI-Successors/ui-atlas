using UiAtlas.Core.Contracts;
using UiAtlas.Core.Recording.Windows;

namespace UiAtlas.Core.Windows.Tests;

public sealed class MsaaDialogCollectorTests
{
    [Fact]
    public void CompactTreeReconnectsChildrenOfDuplicateAncestorsAndPreservesNativePopupContent()
    {
        var bounds = new RectI(0, 0, 500, 500);
        var root = Observation("root", "", "ControlType.Window", bounds);
        var outer = Observation("outer", "root", "ControlType.Pane", bounds) with { Name = "" };
        var inner = outer with { RuntimeId = "inner", ParentRuntimeId = "outer" };
        var item = Observation("item", "inner", "ControlType.MenuItem", new RectI(5, 5, 100, 25));
        var compact = MsaaDialogCollector.CompactTree([root, outer, inner, item]);
        var ids = compact.Select(control => control.RuntimeId).ToHashSet();
        Assert.All(compact.Where(control => control.ParentRuntimeId.Length > 0),
            control => Assert.Contains(control.ParentRuntimeId, ids));
        var popup = new WindowTarget(Hwnd, Hwnd, 1, "test", DateTimeOffset.UnixEpoch, "", "Net UI Tool Window", bounds);
        Assert.Contains(AdaptiveCaptureCoordinator.NormalizePopupAutomation(popup, compact),
            control => control.RuntimeId == "item");
        Assert.Equal("outer", compact.Single(control => control.RuntimeId == "item").ParentRuntimeId);
        Assert.Equal("inner", item.ParentRuntimeId);
    }

    [Fact]
    public void CompactTreeDeduplicatesRepeatedLeavesWithoutConflatingState()
    {
        var item = Observation("item", "root", "ControlType.MenuItem", new RectI(5, 5, 100, 25));
        var same = item with { RuntimeId = "duplicate" };
        var selected = same with { RuntimeId = "selected", IsSelected = true };
        var otherParent = same with { RuntimeId = "other-parent", ParentRuntimeId = "other" };
        var compact = MsaaDialogCollector.CompactTree([item, same, selected, otherParent]);
        Assert.Equal(2, compact.Count);
        Assert.Equal("root", compact[0].ParentRuntimeId);
    }

    [Fact]
    public void OwnerDrawnCustomWithDefaultActionIsClassifiedAsButton()
    {
        Assert.Equal("ControlType.Button",
            MsaaDialogCollector.ControlTypeForRole(0, "Owner-drawn action", "Press"));
    }

    [Theory]
    [InlineData("Sign out of this account")]
    [InlineData("Sign out options for Irina")]
    [InlineData("Switch to Irina (irina@example.com) account")]
    [InlineData("Add a new account or sign in to a different account")]
    public void OfficeAccountActionsAreClassifiedAsButtonsWhenProviderOmitsDefaultAction(string name)
    {
        Assert.Equal("ControlType.Button", MsaaDialogCollector.ControlTypeForRole(0, name));
    }

    [Fact]
    public void OrdinaryOwnerDrawnContainerRemainsCustom()
    {
        Assert.Equal("ControlType.Custom", MsaaDialogCollector.ControlTypeForRole(0, "Account pane"));
    }

    [Fact]
    public void InfersScrollbarWhenListItemsFillTheViewport()
    {
        var controls = ListWithRows(100, 120, 140, 160, 180);

        var scrollbar = Assert.Single(MsaaDialogCollector.InferListScrollBars(controls, Hwnd));

        Assert.Equal("ControlType.ScrollBar", scrollbar.ControlType);
        Assert.Equal("list", scrollbar.ParentRuntimeId);
        Assert.Equal("OfficeDialogInferredScrollBar", scrollbar.ClassName);
        Assert.Equal(new RectI(276, 100, 24, 100), scrollbar.Bounds);
    }

    [Fact]
    public void DoesNotInferScrollbarWhenListHasUnusedVerticalSpace()
    {
        var controls = ListWithRows(100, 120, 140);

        Assert.Empty(MsaaDialogCollector.InferListScrollBars(controls, Hwnd));
    }

    [Fact]
    public void DoesNotDuplicateScrollbarExposedByProvider()
    {
        var controls = ListWithRows(100, 120, 140, 160, 180).ToList();
        controls.Add(Observation(
            "scroll", "list", "ControlType.ScrollBar", new RectI(284, 100, 16, 100)));

        Assert.Empty(MsaaDialogCollector.InferListScrollBars(controls, Hwnd));
    }

    private const long Hwnd = 42;

    private static IReadOnlyList<AutomationObservation> ListWithRows(params int[] rowTops)
    {
        var controls = new List<AutomationObservation>
        {
            Observation("root", "", "ControlType.Window", new RectI(0, 0, 500, 500)),
            Observation("list", "root", "ControlType.List", new RectI(100, 100, 200, 100))
        };
        controls.AddRange(rowTops.Select((top, index) => Observation(
            $"item-{index}", "list", "ControlType.ListItem", new RectI(100, top, 176, 20))));
        return controls;
    }

    private static AutomationObservation Observation(
        string runtimeId,
        string parentRuntimeId,
        string controlType,
        RectI bounds) =>
        new(runtimeId, parentRuntimeId, runtimeId, runtimeId, controlType, "MSAA", bounds,
            IsEnabled: true, IsOffscreen: false, FrameworkId: "Win32", WindowHwnd: Hwnd);
}
