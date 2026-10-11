using UiAtlas.Core.Contracts;

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
            $"{entries.Length - verified - rejected} legacy scope unverified";
    }

    public static UiMapProjectionPolicy PolicyFor(UiMapProjectionMode mode, FrameObservation? frame, bool showLabels = false) =>
        UiMapPresentation.PolicyFor(mode) with {
            ShowsControlLabels = showLabels && UiMapPresentation.PolicyFor(mode).ShowsControlGeometry,
            ShowsControlCrops = mode == UiMapProjectionMode.Controls && AllowsOverlay(frame) };

    public static bool AllowsOverlay(FrameObservation? frame) =>
        frame?.Acquisition is { StabilityStatus: "stable", Status: "ok" or "partial" };

    public static UiMapVariantView? SelectionInScope(UiMapVariantView? current,
        IEnumerable<UiMapSurfaceView> surfaces) => current is null ? null : surfaces
        .SelectMany(surface => surface.Variants)
        .FirstOrDefault(variant => variant.Id == current.Id);

    public static IReadOnlyList<UiMapVariantView> Filter(IEnumerable<UiMapVariantView> variants,
        bool skipEmpty, bool includeSupplementary) => variants
        .Where(variant => includeSupplementary || variant.RecordKind != "supplementary")
        // Failed/unknown coverage is never hidden by the convenience empty filter.
        .Where(variant => !skipEmpty || variant.ControlCount > 0 || variant.CaptureStatus != "ok")
        .ToArray();
}
