using UiAtlas.Core.Build;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Reader;
using UiAtlas.Core.Recording;
using UiAtlas.Core.Storage;

namespace UiAtlas.Core.Tests;

public sealed class RawObservationEligibilityTests
{
    private static WindowObservation Window(long hwnd, long owner, string kind, int z, int process = 42) =>
        new(hwnd, 1, process, kind, kind, new(10, 20, 500, 500), true, true, false, false, 96,
            OwnerHwnd: owner, ZOrder: z, Style: kind == "Net UI Tool Window" ? 0x80000000L : 0x00C40000L);
    private static AutomationObservation Control(string id, string parent, bool hidden = false, long hwnd = 1) =>
        new(id, parent, "", id, "ControlType.ListItem", "MSAA.Role34", new(30, 40, 120, 14), true, hidden, "Win32", hwnd);
    private static FrameObservation Frame(params WindowObservation[] windows)
    {
        var now = DateTimeOffset.UtcNow;
        return new(1, now, "raw/frames/frame-000001.png", windows[0], [], false, "ok", "test", windows,
            Acquisition: new(RawObservationSemantics.Version, "observation:1", now, now, "partial", "screen", "stable"),
            RawStreams: windows.Select(window => new RawDataStreamObservation($"msaa:{window.Hwnd}", $"msaa/0x{window.Hwnd:X}",
                now, now, "ok", [Control("root", "", hwnd: window.Hwnd), Control("child", "root", hwnd: window.Hwnd)],
                TargetWindowHwnd: window.Hwnd)).ToArray());
    }

    [Fact]
    public void FrontDialogExcludesOwnerButOwnedMenusDoNotExcludeTheirWindow()
    {
        var main = Window(1, 0, "OpusApp", 4);
        var menu = Window(2, 1, "Net UI Tool Window", 3);
        var font = Window(3, 1, "bosa_sdm_msword", 2);
        var colors = Window(4, 3, "#32770", 0);
        var frame = Frame(main, menu);
        Assert.Null(RawVariantPresentation.ExclusionReason(frame, main.Hwnd));
        Assert.Null(RawVariantPresentation.ExclusionReason(frame, menu.Hwnd));
        frame = Frame(main, menu, font, colors);
        foreach (var hwnd in new long[] { 1, 2, 3 }) Assert.StartsWith("Background window", RawVariantPresentation.ExclusionReason(frame, hwnd));
        Assert.Null(RawVariantPresentation.ExclusionReason(frame, colors.Hwnd));
        // Foreground normal windows also hide earlier normal windows, even when
        // their rectangles do not overlap. Other processes do not select our frame.
        Assert.StartsWith("Background window", RawVariantPresentation.ExclusionReason(Frame(main,
            Window(8, 0, "OtherDocument", 0) with { Bounds = new(2000, 20, 500, 500) }), 1));
        Assert.Null(RawVariantPresentation.ExclusionReason(Frame(main, Window(8, 0, "OtherApp", 0, 99)), 1));
        Assert.Null(RawVariantPresentation.ExclusionReason(Frame(main, colors with { IsVisible = false }), 1));
    }

    [Theory]
    [InlineData("interrupted", "changed")]
    [InlineData("cancelled", "stable")]
    [InlineData("ok", "unknown")]
    public void IncompleteCapturesRetainForegroundEvidenceWithoutSynchronizedOverlays(string status, string stability)
    {
        var frame = Frame(Window(1, 0, "OpusApp", 0));
        frame = frame with { Acquisition = frame.Acquisition! with { Status = status, StabilityStatus = stability } };
        var reason = RawVariantPresentation.ExclusionReason(frame, 1);
        Assert.Null(reason);
        Assert.False(RawVariantPresentation.AllowsOverlay(frame));
        Assert.False(RawVariantPresentation.PolicyFor(UiMapProjectionMode.Controls, frame).ShowsControlCrops);
        var variant = new UiMapVariantView("one", "one", "bundle", 1, 20, null, []);
        Assert.Single(RawVariantPresentation.Filter([variant], true, false, _ => reason));
        Assert.Single(RawVariantPresentation.Filter([variant], false, false, _ => reason));

        var colors = Window(2, 1, "bosa_sdm_msword", 0);
        var dialogs = Frame(Window(1, 0, "bosa_sdm_msword", 1), colors);
        dialogs = dialogs with { Acquisition = frame.Acquisition };
        Assert.StartsWith("Background window", RawVariantPresentation.ExclusionReason(dialogs, 1));
        Assert.Null(RawVariantPresentation.ExclusionReason(dialogs, 2));
        Assert.False(RawVariantPresentation.AllowsOverlay(dialogs));
    }

    [Fact]
    public void EmptyAndHiddenOnlyWindowsAreExcludedButMsaaAndImageOnlyPopupsRemainUsable()
    {
        var frame = Frame(Window(1, 0, "OpusApp", 1), Window(2, 1, "Net UI Tool Window", 0));
        Assert.Null(RawVariantPresentation.ExclusionReason(frame, 1)); // MSAA is a real stream, not UIA fusion
        frame = frame with { RawStreams = [] };
        Assert.StartsWith("No visible native", RawVariantPresentation.ExclusionReason(frame, 1));
        Assert.Null(RawVariantPresentation.ExclusionReason(frame, 2));
        var populated = Frame(frame.Window);
        frame = populated with { RawStreams = [populated.RawStreams![0] with {
            Controls = [Control("root", ""), Control("hidden", "root", true), Control("child", "hidden")] }] };
        Assert.StartsWith("No visible native", RawVariantPresentation.ExclusionReason(frame, 1));
    }

    [Fact]
    public void HiddenAnimationChildrenStayInEvidenceButNotCanvasAndAttemptsNeverShareParents()
    {
        using var temp = new TempDirectory();
        using var bundle = RecordingBundle.Open(SyntheticBundleFactory.Create(temp.Path, includeScreenshot: true));
        var manifest = bundle.ReadJson<RecordingManifest>("manifest.json");
        manifest = manifest with { Target = manifest.Target with { SelectedHwnd = 1, RootOwnerHwnd = 1, ProcessId = 42 } };
        var frame = Frame(Window(1, 0, "OpusApp", 0));
        // The animation children say visible, but their parent says hidden.
        // A second provider reuses both IDs and reports a visible parent.
        var hidden = new[] { Control("root", ""), Control("animations", "root", true),
            Control("blink", "animations"), Control("sparkle", "animations"), Control("font", "root") };
        frame = frame with { RawStreams = [frame.RawStreams![0] with { Controls = hidden },
            frame.RawStreams[0] with { StreamId = "second", Controls = hidden.Select(control => control with { IsOffscreen = false }).ToArray() }] };
        var graph = new RecordingGraphBuilder().Build(new RecordingGraphInput(manifest, [frame], []));
        var path = Path.Combine(temp.Path, "visibility.db");
        SqliteGraphStore.Save(graph, path);
        var model = new UiMappingReadModel(SqliteGraphStore.Load(path));
        var controls = model.LayerFor(UiUnderstandingLevel.RawDataStreams).Controls;
        Assert.Equal(10, controls.Count);
        var visible = RawVariantPresentation.VisibleControlIds(controls);
        Assert.Equal(7, visible.Count);
        foreach (var control in controls)
        {
            var attempt = control.Source.Properties.Single(property => property.Name == "rawStreamId").Value;
            var fact = Assert.Single(control.Source.Observations!);
            var expected = attempt == "second" || fact.Control.RuntimeId is "root" or "font";
            Assert.Equal(expected, fact.EffectivelyVisible);
            Assert.Equal(expected, visible.Contains(control.Id));
        }
        // Even an older graph with wrong visibility metadata is repaired for
        // display from its recorded ancestry, without rewriting the graph.
        var legacy = controls.Select(control => control with { Source = control.Source with {
            Observations = control.Source.Observations!.Select(fact => fact with { EffectivelyVisible = true }).ToArray() } }).ToArray();
        Assert.Equal(visible.Order(), RawVariantPresentation.VisibleControlIds(legacy).Order());
    }

    [Fact]
    public void CyclicVisibilityAncestryCannotProduceUsableWindowControls()
    {
        var frame = Frame(Window(1, 0, "OpusApp", 0));
        frame = frame with { RawStreams = [frame.RawStreams![0] with {
            Controls = [Control("a", "b"), Control("b", "a")] }] };
        Assert.StartsWith("No visible native", RawVariantPresentation.ExclusionReason(frame, 1));
    }
}
