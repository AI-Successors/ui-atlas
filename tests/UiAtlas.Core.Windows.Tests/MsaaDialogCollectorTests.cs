using UiAtlas.Core.Contracts;
using UiAtlas.Core.Recording.Windows;

namespace UiAtlas.Core.Windows.Tests;

public sealed class MsaaDialogCollectorTests
{
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
