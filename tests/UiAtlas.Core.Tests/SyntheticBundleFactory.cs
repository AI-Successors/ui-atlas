using System.Text.Json;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Recording;

namespace UiAtlas.Core.Tests;

internal static class SyntheticBundleFactory
{
    public static string Create(string directory, string name = "fixture.mlrec", bool includeScreenshot = false,
        string? screenshotEntry = null, bool invalidHealthUtf8 = false, string sessionId = "golden",
        string windowTitle = "Fixture title", IReadOnlyList<long>? representativeFrames = null,
        bool wordLikeBorderlessPopup = false, bool rootReportedPopupSubtree = false,
        RecordingOutcome outcome = RecordingOutcome.Complete,
        IReadOnlyList<string>? markers = null,
        bool popupDelta = false,
        bool controlDelta = false,
        bool emptyScreenshotBoundsWithoutImage = false,
        bool contaminatedPopupDelta = false,
        bool unreadyFullRootPopup = false,
        bool manualPointerUp = false,
        bool dialogDelta = false,
        bool detachedDialogRootOwner = false,
        bool peerRootDelta = false,
        bool unreadyPeerRootDelta = false,
        bool emptyDialogDelta = false,
        bool valueListPopupDelta = false,
        bool worksheetControls = false,
        bool popupInteractionSource = false,
        bool legacyEmbeddedPopupDelta = false,
        bool interactionTrace = false,
        InteractionOutcome interactionOutcome = InteractionOutcome.Succeeded,
        string interactionOperationId = "operation-1",
        string firstTrigger = "initial",
        bool firstAutomationTimedOut = false,
        string firstAutomationStatus = "ok",
        bool hoverShadowPromotion = false,
        bool pointerObservedSourceAfterClick = false,
        bool foreignOwnedPopup = false,
        bool visualFallback = false)
    {
        var output = Path.Combine(directory, name);
        var staging = Path.Combine(directory, Guid.NewGuid().ToString("N"));
        using var writer = new RecordingBundleWriter(staging);
        var started = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var options = new JsonSerializerOptions(JsonDefaults.Options) { WriteIndented = false };
        var events = new List<InputEvent>
        {
            new(1, started, manualPointerUp ? InputEventKind.PointerUp : InputEventKind.PointerDown,
                manualPointerUp ? 25 : 1, manualPointerUp ? 20 : 1, 0)
        };
        if (markers is not null)
        {
            for (var index = 0; index < markers.Count; index++)
            {
                events.Add(new(
                    events.Count + 1,
                    started.AddMilliseconds(index + 1),
                    InputEventKind.Marker,
                    0,
                    0,
                    0,
                    markers[index],
                    0,
                    1,
                    1));
            }
        }
        writer.WriteText("raw/input-events.jsonl", string.Concat(events.Select(item => JsonSerializer.Serialize(item, options) + "\n")));
        if (invalidHealthUtf8) writer.WriteBytes("raw/capture-health.jsonl", new byte[] { 0xC3, 0x28 });
        else writer.WriteText("raw/capture-health.jsonl", "");
        var firstWindow = new WindowObservation(1, 1, 7, "SyntheticWindow", windowTitle, new(0, 0, 800, 600), true, true, false, false, 96,
            Style: 0x10CF0000, ExStyle: 0);
        var secondWindow = firstWindow with { Bounds = new(0, 0, 1200, 900), Dpi = 144 };
        var popup = new WindowObservation(2, 1, 7, wordLikeBorderlessPopup ? "Net UI Tool Window" : "#32768", "Synthetic popup", new(840, 120, 240, 300), true, true, false, false, 144,
            OwnerHwnd: 1, ZOrder: 1, Style: unchecked((int)0x90000000), ExStyle: 0x88, IsToolWindow: true, IsTopMost: true);
        var foreignOwner = new WindowObservation(3, 3, 7, "HiddenOwner", "", new(0, 0, 18, 18), false, true, false, false, 96);
        if (foreignOwnedPopup)
            popup = popup with { RootOwnerHwnd = foreignOwner.Hwnd, OwnerHwnd = foreignOwner.Hwnd };
        if (dialogDelta)
            popup = popup with
            {
                ClassName = "#32770", Title = "Format Cells", Bounds = new RectI(240, 140, 560, 520),
                RootOwnerHwnd = detachedDialogRootOwner ? foreignOwner.Hwnd : popup.RootOwnerHwnd,
                OwnerHwnd = detachedDialogRootOwner ? foreignOwner.Hwnd : popup.OwnerHwnd,
                Style = 0x10C80000, ExStyle = 0, IsToolWindow = false, IsTopMost = false
            };
        if (peerRootDelta)
            popup = popup with
            {
                RootOwnerHwnd = popup.Hwnd,
                OwnerHwnd = 0,
                ClassName = "rctrl_renwnd32",
                Title = "Untitled - Field Service Mission",
                Bounds = new RectI(180, 90, 620, 540),
                Style = 0x16CF0000,
                ExStyle = 0x40100,
                IsToolWindow = false,
                IsTopMost = false
            };
        var firstFrameEntry = includeScreenshot ? screenshotEntry ?? "raw/frames/frame-000001.png" : "";
        if (includeScreenshot)
            writer.WriteBytes(firstFrameEntry, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Wl2nE0AAAAASUVORK5CYII="));
        var firstControls = new List<AutomationObservation>
        {
            new("1", "", "root", "Document", "Pane", "RootPane", new(0, 0, 800, 600), true, false, "Synthetic", 1),
            new("1.1", "1", "save", "Save", "Button", "Button", new(10, 10, 80, 24), true, false, "Synthetic", 1, ["Invoke"]),
            new("1.2", "1", "", "Save", "Button", "Button", new(110, 10, 80, 24), false, true, "Synthetic", 1)
        };
        if (hoverShadowPromotion)
            firstControls.Add(new("shadow-hover:one", "1", "shadow:one", "Unverified control",
                "ControlType.Custom", "UiAtlas.HoverRegion", new(240, 40, 28, 28), false, true,
                "UiAtlas.Shadow.Hover", 1));
        AutomationObservation? firstVisual = null;
        if (visualFallback)
        {
            firstVisual = new("visual:v3:0123456789abcdef", "", "visual:v3:0123456789abcdef", "Save",
                "ControlType.Button", "UiAtlas.VisualControlRegion", new(300, 100, 100, 32), false, true,
                "UiAtlas.Visual.Ocr", 1, VisualRole: "button", OcrText: "Save");
            firstControls.Add(firstVisual);
        }
        if (worksheetControls)
        {
            firstControls.Add(new("1.col-a", "1", "", "A", "ControlType.DataItem", "XLGridColumnHeader",
                new RectI(32, 120, 80, 24), true, false, "Win32", 1,
                ["GridItemPatternIdentifiers.Pattern", "SelectionItemPatternIdentifiers.Pattern"]));
            firstControls.Add(new("1.row-1", "1", "", "1", "ControlType.DataItem", "XLGridRowHeader",
                new RectI(0, 144, 32, 24), true, false, "Win32", 1,
                ["GridItemPatternIdentifiers.Pattern", "SelectionItemPatternIdentifiers.Pattern"]));
            firstControls.Add(new("1.cell-a1", "1", "A1", "A1", "ControlType.DataItem", "XLSpreadsheetCell",
                new RectI(32, 144, 80, 24), true, false, "Win32", 1,
                ["GridItemPatternIdentifiers.Pattern", "SelectionItemPatternIdentifiers.Pattern"]));
            firstControls.Add(new("1.fx", "1", "15", "Insert Function", "ControlType.Button", "Button",
                new RectI(220, 88, 44, 28), true, false, "Win32", 1, ["InvokePatternIdentifiers.Pattern"]));
        }
        var firstExtraction = firstVisual is null ? null : VisualExtraction(firstVisual, "candidate-1", "evidence-1", .52);
        writer.WriteJson("raw/observations/frame-000001.json", new FrameObservation(1, started, firstFrameEntry, firstWindow,
            firstControls,
            firstAutomationTimedOut, firstAutomationStatus, firstTrigger, [firstWindow], Extraction: firstExtraction));
        IReadOnlyList<AutomationObservation> secondControls = rootReportedPopupSubtree
            ? [new("1.popup.1", "1.popup", "choice", "Choice", "ListItem", "ListItem", new(870, 180, 180, 36), true, false, "Synthetic", 1, ["SelectionItem"], IsSelected: true),
               new("1.popup", "1", "popup", "Synthetic popup", "Menu", "Popup", new(840, 120, 240, 300), true, false, "Synthetic", 1),
               new("1.2", "1", "", "Save", "Button", "Button", new(165, 15, 120, 36), true, false, "Synthetic", 1),
               new("1", "", "root", "Document changed", "Pane", "RootPane", new(0, 0, 1200, 900), true, false, "Synthetic", 1),
               new("2", "", "popup", "Synthetic popup", "Window", "Popup", new(840, 120, 240, 300), true, false, "Synthetic", 2),
               new("1.1", "1", "save", "Save As", "Button", "Button", new(15, 15, 120, 36), false, false, "Synthetic", 1, ["Invoke"])]
            : [new("2.1", "2", "choice", "Choice", "ListItem", "ListItem", new(870, 180, 180, 36), true, false, "Synthetic", 2, ["SelectionItem"], IsSelected: true),
               new("1.2", "1", "", "Save", "Button", "Button", new(165, 15, 120, 36), true, false, "Synthetic", 1),
               new("1", "", "root", "Document changed", "Pane", "RootPane", new(0, 0, 1200, 900), true, false, "Synthetic", 1),
               new("2", "", "popup", "Synthetic popup", "Window", "Popup", new(840, 120, 240, 300), true, false, "Synthetic", 2),
               new("1.1", "1", "save", "Save As", "Button", "Button", new(15, 15, 120, 36), false, false, "Synthetic", 1, ["Invoke"])];
        if (hoverShadowPromotion)
            secondControls = secondControls.Concat([
                new AutomationObservation("shadow-hover:one", "1", "shadow:one", "Unverified control",
                    "ControlType.Button", "UiAtlas.HoverRegion", new(240, 40, 28, 28), true, false,
                    "UiAtlas.Pointer", 1, ["InvokePatternIdentifiers.Pattern"])
            ]).ToArray();
        if (pointerObservedSourceAfterClick)
            secondControls = secondControls.Concat([
                new AutomationObservation("ui-atlas:pointer:225:260", "", "", "Observed canvas target",
                    "CanvasItem", "UiAtlas.ObservedCanvasTarget", new(225, 260, 18, 18), true, false,
                    "UiAtlas.Pointer", 1, ["SelectionItem"])
            ]).ToArray();
        AutomationObservation? secondVisual = null;
        if (visualFallback)
        {
            secondVisual = new("visual:v3:0123456789abcdef", "", "visual:v3:0123456789abcdef", "Save",
                "ControlType.Button", "UiAtlas.VisualControlRegion", new(500, 200, 150, 48), true, false,
                "UiAtlas.Pointer", 1, ["Invoke"], VisualRole: "button", OcrText: "Save");
            secondControls = secondControls.Concat([secondVisual]).ToArray();
        }
        var secondFrameEntry = includeScreenshot && (popupDelta || peerRootDelta) ? "raw/frames/frame-000002.png" : "";
        if (secondFrameEntry.Length > 0)
            writer.WriteBytes(secondFrameEntry, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Wl2nE0AAAAASUVORK5CYII="));
        var deltaScope = controlDelta ? "control-delta" : popupDelta ? "popup-delta" : "full-root";
        var frameUnready = unreadyFullRootPopup || unreadyPeerRootDelta;
        var frameAutomation = frameUnready
            ? Array.Empty<AutomationObservation>()
            : dialogDelta || peerRootDelta
            ? emptyDialogDelta
                ? new AutomationObservation[]
                {
                    new("1", "", "root", "Document changed", "ControlType.Pane", "RootPane", new(0, 0, 1200, 900), true, false, "Synthetic", 1),
                    new("1.1", "1", "save", "Save As", "ControlType.Button", "Button", new(15, 15, 120, 36), true, false, "Synthetic", 1, ["Invoke"])
                }
                : new AutomationObservation[]
            {
                new("1", "", "root", "Document changed", "ControlType.Pane", "RootPane", new(0, 0, 1200, 900), true, false, "Synthetic", 1),
                new("1.1", "1", "save", "Save As", "ControlType.Button", "Button", new(15, 15, 120, 36), true, false, "Synthetic", 1, ["Invoke"]),
                new("2", "", "ExactWindow", popup.Title, "ControlType.Window", popup.ClassName, popup.Bounds, true, false, "Win32", popup.Hwnd),
                new("2.tabs", "2", "FormatTabs", "Format categories", "ControlType.Tab", "SysTabControl32", new RectI(260, 180, 500, 36), true, false, "Win32", popup.Hwnd),
                new("2.number", "2.tabs", "Number", "Number", "ControlType.TabItem", "SysTabControl32", new RectI(260, 180, 70, 32), true, false, "Win32", popup.Hwnd, ["SelectionItem"], IsSelected: true),
                new("2.ok", "2", "OK", "OK", "ControlType.Button", "Button", new RectI(600, 610, 90, 28), true, false, "Win32", popup.Hwnd, ["Invoke"]),
                new("2.cancel", "2", "Cancel", "Cancel", "ControlType.Button", "Button", new RectI(700, 610, 90, 28), true, false, "Win32", popup.Hwnd, ["Invoke"])
            }
            : controlDelta
            ? secondControls.Where(control => control.WindowHwnd == secondWindow.Hwnd).ToArray()
            : popupDelta
                ? valueListPopupDelta
                    ? new AutomationObservation[]
                    {
                        new("2", "", "values", "Values", "ControlType.List", "NetUIList",
                            popup.Bounds, true, false, "Win32", popup.Hwnd),
                        new("2.value", "2", "value-11", "11", "ControlType.Text", "NetUIValue",
                            new RectI(870, 160, 150, 26), true, false, "Win32", popup.Hwnd),
                        new("2.scroll", "2", "scroll", "Vertical", "ControlType.ScrollBar", "ScrollBar",
                            new RectI(1045, 145, 20, 250), true, false, "Win32", popup.Hwnd, ["RangeValue"]),
                        new("2.thumb", "2.scroll", "thumb", "Position", "ControlType.Thumb", "ScrollBarThumb",
                            new RectI(1045, 200, 20, 55), true, false, "Win32", popup.Hwnd, ["Transform"])
                    }
                    : contaminatedPopupDelta
                    ? new AutomationObservation[]
                    {
                        new("2", "", "popup", "Synthetic popup", "ControlType.Menu", "Net UI Tool Window",
                            popup.Bounds, true, false, "Win32", popup.Hwnd),
                        new("2.cell", "2", "L3", "L3", "ControlType.DataItem", "XLSpreadsheetCell",
                            new RectI(870, 180, 80, 24), true, false, "Win32", popup.Hwnd)
                    }
                    : secondControls.Where(control => control.WindowHwnd == popup.Hwnd)
                        .Select(control => control.RuntimeId == "2"
                            ? control with { ControlType = "ControlType.Menu", ClassName = "Net UI Tool Window" }
                            : control)
                        .ToArray()
                : secondControls;
        writer.WriteJson("raw/observations/frame-000002.json", new FrameObservation(2, started.AddSeconds(1), secondFrameEntry, secondWindow,
            frameAutomation,
            frameUnready, frameUnready ? "timeout" : "ok",
            dialogDelta ? "adaptive-dialog:" + popup.Title : "pointer",
            legacyEmbeddedPopupDelta ? [secondWindow] : foreignOwnedPopup ? [foreignOwner, secondWindow, popup] : [secondWindow, popup],
            ObservationScope: deltaScope,
            ObservedWindowHwnds: controlDelta ? [secondWindow.Hwnd] : popupDelta ? [popup.Hwnd] :
                detachedDialogRootOwner ? [popup.Hwnd] : dialogDelta || peerRootDelta ? [secondWindow.Hwnd, popup.Hwnd] : null,
            ScreenshotBounds: emptyScreenshotBoundsWithoutImage ? new RectI(0, 0, 0, 0) :
                popupDelta ? legacyEmbeddedPopupDelta ? secondWindow.Bounds : popup.Bounds : peerRootDelta ? popup.Bounds : null,
            BaseFrameSequence: popupDelta || controlDelta ? 1 : null,
            InteractionSource: popupDelta && popupInteractionSource ? firstControls[1] : null,
            Extraction: secondVisual is null ? null : VisualExtraction(secondVisual, "candidate-2", "evidence-2", .91)));
        if (interactionTrace)
        {
            var interactionSource = hoverShadowPromotion
                ? firstControls.Single(control => control.AutomationId == "shadow:one")
                : pointerObservedSourceAfterClick
                    ? secondControls.Single(control => control.RuntimeId == "ui-atlas:pointer:225:260")
                : firstControls[1];
            writer.WriteText("raw/interactions.jsonl", JsonSerializer.Serialize(new InteractionObservation(
                "interaction-1", interactionOperationId, 1, 1, InteractionActor.User,
                InteractionGestureKind.Click, InteractionActionKind.Invoke, 1, interactionSource, [], [2],
                started.AddMilliseconds(500), started.AddSeconds(1), interactionOutcome, "captured"), options) + "\n");
        }
        writer.WriteJson("derived/statebook.json", new DerivedStatebook("statebook/1", representativeFrames ?? [1, 2], [new("e1", 1, 1, 2, "pointer", "input-correlated")]));
        writer.WriteJson("manifest.json", new RecordingManifest(FormatVersions.RecordingBundle, FormatVersions.Tool, sessionId, started, started.AddSeconds(1),
            outcome, new(1, 1, 7, "Synthetic", started.AddHours(-1), ProductVersion: "1.2.3",
                OriginalFilename: "Synthetic.exe", CompanyName: "Example Corp", ProductName: "Synthetic Target"),
            new(ScreenshotsRetained: includeScreenshot), new(), true, events.Count, 2,
            Files: writer.DescribeEntries()));
        writer.Complete(output);
        return output;
    }

    private static AdaptiveExtractionSnapshot VisualExtraction(
        AutomationObservation control,
        string candidateId,
        string evidenceId,
        double confidence) =>
        new(
            "adaptive-extraction/1",
            [new ExtractionSourceResult(
                ControlEvidenceSource.Visual,
                "surface-root",
                [new ControlEvidenceObservation(evidenceId, ControlEvidenceSource.Visual, "surface-root", control, confidence)],
                "ok",
                1)],
            [new MergedControlCandidate(
                candidateId,
                "surface-root",
                control,
                [evidenceId],
                [ControlEvidenceSource.Visual],
                confidence,
                control.IsOffscreen ? ExtractionCoverageStatus.Partial : ExtractionCoverageStatus.Confirmed)],
            [],
            ExtractionCoverageStatus.Partial,
            "test",
            1,
            1);
}
