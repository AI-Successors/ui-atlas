using UiAtlas.Core.Contracts;
using UiAtlas.Core.Recording.Windows;

namespace UiAtlas.Core.Windows.Tests;

public sealed class ManualTargetInputWaiterTests
{
    [Fact]
    public async Task TwoClicksWaitsForTwoReleasedButtonsBeforeCompleting()
    {
        var states = new Queue<ManualButtonState>(
        [new(false, false, false), new(true, false, false), new(false, false, false),
         new(false, true, false), new(false, false, false)]);
        var progress = new List<int>();
        var waiter = CreateWaiter(states, () => true);

        await waiter.WaitForClicksAsync(2, (observed, _) => progress.Add(observed), CancellationToken.None);

        Assert.Equal([1, 2], progress);
        Assert.Empty(states);
    }

    [Fact]
    public async Task ReleaseOutsideTargetScopeDoesNotCount()
    {
        var states = new Queue<ManualButtonState>(
        [new(false, false, false), new(true, false, false), new(false, false, false),
         new(true, false, false), new(false, false, false)]);
        var scopes = new Queue<bool>([false, true]);
        var progress = new List<int>();
        var waiter = CreateWaiter(states, () => scopes.Dequeue());

        await waiter.WaitForClicksAsync(1, (observed, _) => progress.Add(observed), CancellationToken.None);

        Assert.Equal([1], progress);
        Assert.Empty(states);
        Assert.Empty(scopes);
    }

    [Fact]
    public async Task ButtonAlreadyHeldWhileArmingMustBeReleasedBeforeBaseline()
    {
        var states = new Queue<ManualButtonState>(
        [new(true, false, false), new(false, false, false),
         new(true, false, false), new(false, false, false)]);
        var scopeChecks = 0;
        var waiter = CreateWaiter(states, () => { scopeChecks++; return true; });

        await waiter.WaitForClicksAsync(1, null, CancellationToken.None);

        Assert.Equal(1, scopeChecks);
        Assert.Empty(states);
    }

    [Fact]
    public async Task FastClickBetweenPollsIsStillObserved()
    {
        var states = new Queue<ManualButtonState>(
        [new(false, false, false), new(false, false, false, LeftPressedSinceLastRead: true)]);
        var waiter = CreateWaiter(states, () => true);

        await waiter.WaitForClicksAsync(1, null, CancellationToken.None);

        Assert.Empty(states);
    }

    private static ManualTargetInputWaiter CreateWaiter(
        Queue<ManualButtonState> states,
        Func<bool> scope) =>
        new(new WindowTarget(1, 1, 1, "Synthetic", DateTimeOffset.UnixEpoch, "", "", new RectI(0, 0, 1, 1)),
            () => states.Dequeue(), scope, TimeSpan.Zero);
}

public sealed class LowLevelInputMonitorTests
{
    [Fact]
    public void RecordedClickQueuePreservesClicksThatArrivedWhilePreviousCaptureWasBusy()
    {
        var start = DateTimeOffset.UnixEpoch;
        InputEvent[] events =
        [
            new(1, start.AddMilliseconds(10), InputEventKind.PointerUp, 10, 20, 0, RootOwnerHwnd: 7),
            new(2, start.AddMilliseconds(20), InputEventKind.PointerUp, 30, 40, 0, RootOwnerHwnd: 7),
            new(3, start.AddMilliseconds(30), InputEventKind.PointerUp, 50, 60, 0, RootOwnerHwnd: 8)
        ];

        var first = ManualRecordingSession.SelectRecordedTargetClicks(events, 7, start, 1);
        var second = ManualRecordingSession.SelectRecordedTargetClicks(events, 7, first[0].TimestampUtc, 1);

        Assert.Equal((10, 20), (first[0].X, first[0].Y));
        Assert.Equal((30, 40), (second[0].X, second[0].Y));
    }

    [Fact]
    public void PausedCaptureDoesNotRecordInput()
    {
        Assert.False(LowLevelInputMonitor.ShouldRecordInput(inputCapturePaused: true));
        Assert.True(LowLevelInputMonitor.ShouldRecordInput(inputCapturePaused: false));
    }

    [Theory]
    [InlineData(0x0201)]
    [InlineData(0x0204)]
    [InlineData(0x0207)]
    public void MouseDownFocusesInactiveTargetBeforeDelivery(int message)
    {
        Assert.True(LowLevelInputMonitor.ShouldFocusTargetBeforeMouseMessage(message, targetIsForeground: false));
        Assert.False(LowLevelInputMonitor.ShouldFocusTargetBeforeMouseMessage(message, targetIsForeground: true));
    }

    [Theory]
    [InlineData(0x0200)]
    [InlineData(0x0202)]
    [InlineData(0x020A)]
    public void NonPressMouseMessagesDoNotChangeFocus(int message)
    {
        Assert.False(LowLevelInputMonitor.ShouldFocusTargetBeforeMouseMessage(message, targetIsForeground: false));
    }
}

public sealed class ManualRecordingHighlightResolverTests
{
    [Fact]
    public void ResolvePrefersInteractiveControlOverNestedText()
    {
        var frame = Frame(
            [
                new("button", "", "save", "Save", "Button", "Button", new RectI(10, 20, 100, 40), true, false, "Synthetic", 1, ["Invoke"]),
                new("text", "button", "", "Save", "Text", "Text", new RectI(20, 30, 42, 18), true, false, "Synthetic", 1)
            ],
            [new WindowObservation(1, 1, 1, "SyntheticWindow", "Target", new RectI(0, 0, 200, 120), true, true, false, false, 96)]);

        var highlights = ManualRecordingHighlightResolver.Resolve(frame,
            [new InputEvent(1, DateTimeOffset.UtcNow, InputEventKind.PointerUp, 26, 34, 0, WindowFromPointHwnd: 1, RootOwnerHwnd: 1)]);

        Assert.Equal([new RectI(10, 20, 100, 40)], highlights);
    }

    [Fact]
    public void ResolveHighlightsTheClickedWorksheetCellInsteadOfTheGridSurface()
    {
        var cell = new RectI(34, 318, 80, 24);
        var frame = Frame(
            [
                new("grid", "", "Grid", "Grid", "ControlType.DataGrid", "XLSpreadsheetGrid",
                    new RectI(0, 294, 1894, 669), true, false, "Win32", 1),
                new("cell-a1", "grid", "A1", "A1", "ControlType.DataItem", "XLSpreadsheetCell",
                    cell, true, false, "Win32", 1,
                    ["GridItemPatternIdentifiers.Pattern", "SelectionItemPatternIdentifiers.Pattern"])
            ],
            [new WindowObservation(1, 1, 1, "XLMAIN", "Book1", new RectI(0, 0, 1920, 1003), true, true, false, false, 96)]);

        var highlights = ManualRecordingHighlightResolver.Resolve(frame,
            [new InputEvent(1, DateTimeOffset.UtcNow, InputEventKind.PointerUp, 70, 330, 0,
                WindowFromPointHwnd: 1, RootOwnerHwnd: 1)]);

        Assert.Equal([cell], highlights);
    }

    [Fact]
    public void ResolveFallsBackToContainingWindowWhenAutomationIsUnavailable()
    {
        var root = new WindowObservation(1, 1, 1, "RootWindow", "Target", new RectI(0, 0, 800, 600), true, true, false, false, 96);
        var popup = new WindowObservation(2, 1, 1, "PopupWindow", "Popup", new RectI(540, 120, 180, 140), true, true, false, false, 96, OwnerHwnd: 1);
        var frame = Frame([], [root, popup]);

        var highlights = ManualRecordingHighlightResolver.Resolve(frame,
            [new InputEvent(1, DateTimeOffset.UtcNow, InputEventKind.PointerUp, 610, 180, 0, WindowFromPointHwnd: 2, RootOwnerHwnd: 1)]);

        Assert.Equal([popup.Bounds], highlights);
    }

    [Fact]
    public void ResolveFallsBackToPointMarkerWhenNothingMatches()
    {
        var frame = Frame([], [new WindowObservation(1, 1, 1, "RootWindow", "Target", new RectI(0, 0, 100, 100), true, true, false, false, 96)]);

        var highlights = ManualRecordingHighlightResolver.Resolve(frame,
            [new InputEvent(1, DateTimeOffset.UtcNow, InputEventKind.PointerUp, 300, 400, 0, WindowFromPointHwnd: 0, RootOwnerHwnd: 1)]);

        Assert.Equal([new RectI(291, 391, 18, 18)], highlights);
    }

    [Fact]
    public void ResolveMapsChildWindowHandleBackToRootScopedControl()
    {
        var frame = Frame(
            [
                new("home", "", "TabHome", "Home", "ControlType.TabItem", "NetUIRibbonTab", new RectI(372, 146, 70, 38), true, false, "Win32", 1, ["SelectionItem"])
            ],
            [new WindowObservation(1, 1, 1, "RootWindow", "Target", new RectI(298, 85, 1475, 842), true, true, false, false, 96)]);

        var highlights = ManualRecordingHighlightResolver.Resolve(frame,
            [new InputEvent(1, DateTimeOffset.UtcNow, InputEventKind.PointerUp, 407, 165, 0, WindowFromPointHwnd: 99, RootOwnerHwnd: 1)]);

        Assert.Equal([new RectI(372, 146, 70, 38)], highlights);
    }

    [Fact]
    public void ResolveUsesPointMarkerInsteadOfTintingEntireRootWindowWhenOnlyRootMatches()
    {
        var frame = Frame([], [new WindowObservation(1, 1, 1, "RootWindow", "Target", new RectI(298, 85, 1475, 842), true, true, false, false, 96)]);

        var highlights = ManualRecordingHighlightResolver.Resolve(frame,
            [new InputEvent(1, DateTimeOffset.UtcNow, InputEventKind.PointerUp, 407, 165, 0, WindowFromPointHwnd: 99, RootOwnerHwnd: 1)]);

        Assert.Equal([new RectI(398, 156, 18, 18)], highlights);
    }

    [Fact]
    public void ResolveUsesPointMarkerWhenOnlyWindowSizedContainerMatches()
    {
        var frame = Frame(
            [
                new("document", "", "", "Worksheet", "Pane", "EXCEL7", new RectI(298, 117, 1475, 810), true, false, "Synthetic", 1)
            ],
            [new WindowObservation(1, 1, 1, "RootWindow", "Target", new RectI(298, 85, 1475, 842), true, true, false, false, 96)]);

        var highlights = ManualRecordingHighlightResolver.Resolve(frame,
            [new InputEvent(1, DateTimeOffset.UtcNow, InputEventKind.PointerUp, 407, 165, 0, WindowFromPointHwnd: 1, RootOwnerHwnd: 1)]);

        Assert.Equal([new RectI(398, 156, 18, 18)], highlights);
    }

    [Fact]
    public void ResolveUsesPointMarkerWhenOnlyLargeInteractiveSurfaceMatchesRootClick()
    {
        var frame = Frame(
            [
                new("sheet", "", "", "Worksheet", "Table", "EXCEL7", new RectI(298, 117, 1475, 810), true, false, "Synthetic", 1, ["Grid", "Table"])
            ],
            [new WindowObservation(1, 1, 1, "RootWindow", "Target", new RectI(298, 85, 1475, 842), true, true, false, false, 96)]);

        var highlights = ManualRecordingHighlightResolver.Resolve(frame,
            [new InputEvent(1, DateTimeOffset.UtcNow, InputEventKind.PointerUp, 407, 165, 0, WindowFromPointHwnd: 1, RootOwnerHwnd: 1)]);

        Assert.Equal([new RectI(398, 156, 18, 18)], highlights);
    }

    [Fact]
    public void ResolveFallsBackToPopupWindowWhenLargeRootSurfaceMatchesPopupClick()
    {
        var root = new WindowObservation(1, 1, 1, "RootWindow", "Target", new RectI(298, 85, 1475, 842), true, true, false, false, 96);
        var popup = new WindowObservation(2, 1, 1, "PopupWindow", "Insert Function", new RectI(798, 225, 520, 340), true, true, false, false, 96, OwnerHwnd: 1);
        var frame = Frame(
            [
                new("sheet", "", "", "Worksheet", "Table", "EXCEL7", new RectI(298, 117, 1475, 810), true, false, "Synthetic", 0, ["Grid", "Table"])
            ],
            [root, popup]);

        var highlights = ManualRecordingHighlightResolver.Resolve(frame,
            [new InputEvent(1, DateTimeOffset.UtcNow, InputEventKind.PointerUp, 930, 410, 0, WindowFromPointHwnd: 2, RootOwnerHwnd: 1)]);

        Assert.Equal([popup.Bounds], highlights);
    }

    [Fact]
    public void ResolveUsesPointMarkerInsteadOfHugeDialogOrInteractiveContainer()
    {
        var root = new WindowObservation(1, 1, 1, "RootWindow", "Target",
            new RectI(0, 0, 1900, 1000), true, true, false, false, 96);
        var dialog = new WindowObservation(2, 1, 1, "DialogWindow", "Large dialog",
            new RectI(420, 80, 1100, 820), true, true, false, false, 96, OwnerHwnd: 1);
        var frame = Frame(
            [
                new("dialog-item", "", "", "Dialog", "ControlType.ListItem", "Pane",
                    dialog.Bounds, true, false, "Synthetic", 2, ["SelectionItem"])
            ],
            [root, dialog]);

        var highlights = ManualRecordingHighlightResolver.Resolve(frame,
            [new InputEvent(1, DateTimeOffset.UtcNow, InputEventKind.PointerUp, 900, 500, 0,
                WindowFromPointHwnd: 2, RootOwnerHwnd: 1)]);

        Assert.Equal([new RectI(891, 491, 18, 18)], highlights);
    }

    [Fact]
    public void RecorderKeepsUserInputButIgnoresTaggedProbeInput()
    {
        Assert.True(LowLevelInputMonitor.ShouldRecordInput(false, 0));
        Assert.False(LowLevelInputMonitor.ShouldRecordInput(false, SafeSyntheticInput.Marker));
        Assert.False(LowLevelInputMonitor.ShouldRecordInput(true, 0));
    }

    [Fact]
    public void SafeProbeInputCannotSendActivatingKeys()
    {
        Assert.True(SafeSyntheticInput.IsSafeProbeKey(0x12));
        Assert.True(SafeSyntheticInput.IsSafeProbeKey(0x09));
        Assert.True(SafeSyntheticInput.IsSafeProbeKey(0x1B));
        Assert.False(SafeSyntheticInput.IsSafeProbeKey(0x0D));
        Assert.False(SafeSyntheticInput.IsSafeProbeKey(0x20));
    }

    [Fact]
    public void CancellationMonitorIgnoresAReleasedSourceFromLateNativeInput()
    {
        var cancellation = new CancellationTokenSource();
        cancellation.Dispose();

        var exception = Record.Exception(() => UserInputCancellationMonitor.CancelSafely(cancellation));

        Assert.Null(exception);
    }

    [Fact]
    public void CancellationMonitorDisposeIsIdempotentBeforeStart()
    {
        var monitor = new UserInputCancellationMonitor();

        monitor.Dispose();
        var exception = Record.Exception(monitor.Dispose);

        Assert.Null(exception);
        Assert.True(monitor.Token.IsCancellationRequested);
    }

    private static FrameObservation Frame(
        IReadOnlyList<AutomationObservation> automation,
        IReadOnlyList<WindowObservation> windows)
    {
        var root = windows[0];
        return new FrameObservation(
            1,
            DateTimeOffset.UtcNow,
            "",
            root,
            automation,
            false,
            "ok",
            "manual-one-click",
            windows);
    }
}
