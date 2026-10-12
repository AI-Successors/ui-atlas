using UiAtlas.Core.Contracts;
using UiAtlas.Core.Recording;

namespace UiAtlas.Core.Reader;

public sealed record RawSurfaceImageLayout(RectI SourcePixels, RectI WindowLocalBounds, string Coverage)
{
    // A raw image may include an owned menu without making its automation records
    // children of the owner. Follow only the recorded immediate HWND owner chain;
    // a dialog is a boundary, not a route to the application's root window.
    public static IReadOnlyList<WindowObservation> WindowContext(FrameObservation frame, WindowObservation window)
    {
        var result = new List<WindowObservation> { window };
        var candidates = (frame.ScopedWindows ?? [frame.Window]).Where(other =>
            other.Hwnd != window.Hwnd && other.ProcessId == window.ProcessId &&
            other.IsVisible && !other.IsMinimized && !other.IsCloaked &&
            ControlGeometry.IsRenderable(other.Bounds) &&
            RawObservationSemantics.NativeKind(other) is "borderless-window" or "borderless-tool-window").ToArray();
        for (var index = 0; index < result.Count; index++)
            foreach (var candidate in candidates.Where(other => other.OwnerHwnd == result[index].Hwnd))
                if (result.All(other => other.Hwnd != candidate.Hwnd)) result.Add(candidate);
        return result;
    }

    public static RectI ContextBounds(IReadOnlyList<WindowObservation> context)
    {
        var left = context.Min(window => window.Bounds.X);
        var top = context.Min(window => window.Bounds.Y);
        var right = context.Max(window => (long)window.Bounds.X + window.Bounds.Width);
        var bottom = context.Max(window => (long)window.Bounds.Y + window.Bounds.Height);
        return new(left, top, checked((int)(right - left)), checked((int)(bottom - top)));
    }

    public static IReadOnlyList<RectI> VisibleContextRegions(FrameObservation frame, IReadOnlyList<WindowObservation> context)
    {
        var windows = frame.ScopedWindows ?? [frame.Window];
        var regions = new List<RectI>();
        foreach (var window in context)
        {
            IEnumerable<RectI> visible = [window.Bounds];
            foreach (var other in windows.Where(other => context.All(item => item.Hwnd != other.Hwnd) &&
                         other.IsVisible && !other.IsMinimized && !other.IsCloaked &&
                         (other.ZOrder < window.ZOrder || other.OwnerHwnd == window.Hwnd)))
                visible = visible.SelectMany(region => Subtract(region, other.Bounds)).ToArray();
            regions.AddRange(visible);
        }
        return regions;
    }

    private static IEnumerable<RectI> Subtract(RectI region, RectI covering)
    {
        var overlap = RawObservationSemantics.Intersection(region, covering);
        if (overlap is null) { yield return region; yield break; }
        if (overlap.Y > region.Y) yield return region with { Height = overlap.Y - region.Y };
        var bottom = (long)region.Y + region.Height;
        var overlapBottom = (long)overlap.Y + overlap.Height;
        if (overlapBottom < bottom) yield return new(region.X, (int)overlapBottom, region.Width, (int)(bottom - overlapBottom));
        if (overlap.X > region.X) yield return new(region.X, overlap.Y, overlap.X - region.X, overlap.Height);
        var right = (long)region.X + region.Width;
        var overlapRight = (long)overlap.X + overlap.Width;
        if (overlapRight < right) yield return new((int)overlapRight, overlap.Y, (int)(right - overlapRight), overlap.Height);
    }

    public static IReadOnlyList<RectI> Occluders(FrameObservation frame, WindowObservation window) =>
        (frame.ScopedWindows ?? [frame.Window])
        .Where(other => other.Hwnd != window.Hwnd && other.IsVisible && !other.IsMinimized && !other.IsCloaked &&
            (other.ZOrder < window.ZOrder || other.OwnerHwnd == window.Hwnd))
        .Select(other => RawObservationSemantics.Intersection(window.Bounds, other.Bounds))
        .Where(bounds => bounds is not null).Cast<RectI>().ToArray();

    public static IReadOnlyList<RectI> VisibleControlRegions(FrameObservation frame, WindowObservation window, RectI bounds)
    {
        IEnumerable<RectI> regions = ControlGeometry.IsRenderable(bounds) ? [bounds] : [];
        foreach (var occluder in Occluders(frame, window))
            regions = regions.SelectMany(region => Subtract(region, occluder)).ToArray();
        return regions.ToArray();
    }

    public static bool CanCropControl(UiEvidenceImage image, RectI bounds) =>
        ControlGeometry.IsRenderable(bounds) &&
        RawObservationSemantics.Intersection(bounds, image.ScreenshotBounds) == bounds &&
        !(image.OccludedRegions ?? []).Any(region => RawObservationSemantics.Intersection(bounds, region) is not null);

    public static RawSurfaceImageLayout? Create(RectI window, RectI image, int pixelWidth, int pixelHeight)
    {
        var overlap = RawObservationSemantics.Intersection(window, image);
        if (overlap is null || pixelWidth <= 0 || pixelHeight <= 0) return null;
        var scaleX = pixelWidth / (double)image.Width;
        var scaleY = pixelHeight / (double)image.Height;
        var left = Math.Clamp((int)Math.Floor(((long)overlap.X - image.X) * scaleX), 0, pixelWidth);
        var top = Math.Clamp((int)Math.Floor(((long)overlap.Y - image.Y) * scaleY), 0, pixelHeight);
        var right = Math.Clamp((int)Math.Ceiling(((long)overlap.X + overlap.Width - image.X) * scaleX), 0, pixelWidth);
        var bottom = Math.Clamp((int)Math.Ceiling(((long)overlap.Y + overlap.Height - image.Y) * scaleY), 0, pixelHeight);
        if (right <= left || bottom <= top) return null;
        return new(new(left, top, right - left, bottom - top),
            overlap with { X = overlap.X - window.X, Y = overlap.Y - window.Y },
            RawObservationSemantics.Coverage(window, image));
    }
}

public static class RawVariantPresentation
{
    public static string? ExclusionReason(FrameObservation frame, long hwnd)
    {
        var windows = frame.ScopedWindows ?? [frame.Window];
        var window = windows.FirstOrDefault(item => item.Hwnd == hwnd);
        if (window is null) return "Window is not present in this frame.";
        if (!window.IsVisible || window.IsMinimized || window.IsCloaked)
            return "Window was not visible.";
        static bool Standalone(WindowObservation item) => RawObservationSemantics.NativeKind(item)
            is "normal-window" or "dialog-window" or "tool-window";
        var front = windows.Where(item => item.ProcessId == window.ProcessId && item.IsVisible &&
            !item.IsMinimized && !item.IsCloaked && Standalone(item))
            .OrderBy(item => item.ZOrder).FirstOrDefault();
        // Menus belong to the immediate owner chain; never follow a root owner
        // through a foreground dialog back to a background application window.
        var owner = window;
        var visited = new HashSet<long>();
        while (!Standalone(owner) && visited.Add(owner.Hwnd) &&
               windows.FirstOrDefault(item => item.Hwnd == owner.OwnerHwnd && item.ProcessId == owner.ProcessId) is { } parent)
            owner = parent;
        if (front is not null && owner.Hwnd != front.Hwnd && front.ZOrder < owner.ZOrder)
            return $"Background window; {(string.IsNullOrEmpty(front.Title) ? front.ClassName : front.Title)} (0x{front.Hwnd:X}) is in front.";
        // Acquisition timing controls image/geometry pairing, not whether a
        // successful provider response exists. Keep foreground evidence even
        // when another attempt was interrupted; AllowsOverlay gates the pairing.
        // Image-only menus/tooltips are still valid raw surfaces. Normal windows
        // and dialogs need a usable native response, not just a root rectangle.
        if (Standalone(window) && !StreamsForWindow(frame, hwnd).Any(stream =>
                stream.Status is "ok" or "partial" && AutomationObservationVisibility.FilterEffectivelyVisible(stream.Controls)
                    .Any(control => control.WindowHwnd == hwnd && control.ScopeStatus is not ("outside-target" or "unverified") &&
                        !string.IsNullOrEmpty(control.ParentRuntimeId) && RawObservationSemantics.Intersection(control.Bounds, window.Bounds) is not null)))
            return "No visible native control records were returned for this window.";
        return null;
    }

    public static IReadOnlySet<string> VisibleControlIds(IEnumerable<UiMapControlView> controls)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var attempt in controls.GroupBy(control => control.Source.Properties
                     .FirstOrDefault(property => property.Name == "rawStreamId")?.Value ?? "legacy"))
        {
            var facts = attempt.Select(control => (View: control, Fact: control.Source.Observations?.SingleOrDefault()?.Control)).ToArray();
            var visible = AutomationObservationVisibility.FilterEffectivelyVisible(facts.Where(item => item.Fact is not null)
                .Select(item => item.Fact!)).ToHashSet();
            foreach (var item in facts)
                if (item.Fact is not null ? visible.Contains(item.Fact) :
                    !item.View.Source.Properties.Any(property => property.Name is "offscreen" && property.Value == "True" ||
                        property.Name == "effectivelyVisible" && property.Value == "False")) result.Add(item.View.Id);
        }
        return result;
    }

    public static IReadOnlyList<RawDataStreamObservation> StreamsForWindow(FrameObservation frame, long hwnd) =>
        (frame.RawStreams ?? []).Where(stream =>
            RawObservationSemantics.Kind(stream.Source) == "native-automation" &&
            (stream.TargetWindowHwnd is { } target ? target == hwnd :
             stream.Source.EndsWith($"/0x{hwnd:X}", StringComparison.OrdinalIgnoreCase) ||
             stream.Controls.Any(control => (control.QueryWindowHwnd ?? control.WindowHwnd) == hwnd))).ToArray();

    public static string ScopeSummary(IEnumerable<AutomationObservation> controls, long hwnd)
    {
        var entries = controls.ToArray();
        var verified = entries.Count(control => control.QueryWindowHwnd == hwnd && control.ScopeStatus == "verified");
        var rejected = entries.Count(control => control.ScopeStatus is "outside-target" or "unverified" ||
            control.QueryWindowHwnd is { } target && target != hwnd);
        return $"Query target 0x{hwnd:X} • {verified} verified • {rejected} rejected/unresolved • " +
            $"{entries.Length - verified - rejected} without ownership verification";
    }

    public static UiMapProjectionPolicy PolicyFor(UiMapProjectionMode mode, FrameObservation? frame, bool showLabels = false) =>
        UiMapPresentation.PolicyFor(mode) with {
            ShowsControlLabels = showLabels && UiMapPresentation.PolicyFor(mode).ShowsControlGeometry,
            ShowsControlCrops = mode == UiMapProjectionMode.Controls && AllowsOverlay(frame) };

    public static bool AllowsOverlay(FrameObservation? frame) =>
        frame?.Acquisition is { StabilityStatus: "stable", Status: "ok" or "partial" };

    public static string? CaptureWarning(FrameObservation? frame) => AllowsOverlay(frame) ? null :
        $"Incomplete capture — {frame?.Acquisition?.Status ?? "timing unknown"}. " +
        "The screenshot and control records may show different moments. " +
        "Controls and Structure show geometry only; overlays and control images are withheld. " +
        "Choose a stable observation to inspect paired evidence. All attempts remain in Frame streams.";

    public static UiMapVariantView? SelectionInScope(UiMapVariantView? current,
        IEnumerable<UiMapSurfaceView> surfaces) => current is null ? null : surfaces
        .SelectMany(surface => surface.Variants)
        .FirstOrDefault(variant => variant.Id == current.Id);

    public static IReadOnlyList<UiMapVariantView> Filter(IEnumerable<UiMapVariantView> variants,
        bool skipEmpty, bool includeSupplementary, Func<UiMapVariantView, string?>? exclusionReason = null) => variants
        .Where(variant => includeSupplementary || variant.RecordKind != "supplementary")
        .Where(variant => !skipEmpty || (exclusionReason is null ? variant.ControlCount > 0 : exclusionReason(variant) is null))
        .ToArray();
}
