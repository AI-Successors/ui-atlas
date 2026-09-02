using UiAtlas.Core.Contracts;
using UiAtlas.Core.Recording;

namespace UiAtlas.Core.Tests;

public sealed class AutomationObservationVisibilityTests
{
    [Fact]
    public void VisibleChildOfOffscreenDialogPageIsNotEffectivelyVisible()
    {
        AutomationObservation[] controls =
        [
            Observation("dialog", "", "Format Cells", "Window", false),
            Observation("hidden-page", "dialog", "Category", "List", true),
            Observation("stale-item", "hidden-page", "General", "ListItem", false),
            Observation("font-list", "dialog", "Fonts", "List", false),
            Observation("font-item", "font-list", "Aptos Narrow", "ListItem", false)
        ];

        var visible = AutomationObservationVisibility.FilterEffectivelyVisible(controls);

        Assert.DoesNotContain(visible, control => control.RuntimeId is "hidden-page" or "stale-item");
        Assert.Contains(visible, control => control.RuntimeId == "font-item");
    }

    [Fact]
    public void MissingParentDoesNotDiscardOtherwiseVisibleProviderResult()
    {
        var orphan = Observation("orphan", "provider-shell", "Apply", "Button", false);

        Assert.Single(AutomationObservationVisibility.FilterEffectivelyVisible([orphan]));
    }

    private static AutomationObservation Observation(
        string runtimeId,
        string parentRuntimeId,
        string name,
        string type,
        bool offscreen) => new(
            runtimeId,
            parentRuntimeId,
            runtimeId,
            name,
            "ControlType." + type,
            "MSAA.Role",
            new RectI(100, 100, 120, 24),
            true,
            offscreen,
            "Win32",
            2);
}
