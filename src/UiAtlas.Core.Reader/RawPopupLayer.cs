using UiAtlas.Core.Contracts;
using UiAtlas.Core.Recording;

namespace UiAtlas.Core.Reader;

// A presentation layer keeps the original window, variant and provider identity.
// It is never added to the owner's graph or combined with another attempt.
public sealed record RawPopupLayer(WindowObservation Window, UiMapSurfaceView? Surface,
    UiMapVariantView? Variant, IReadOnlyList<RawDataStreamObservation> Streams,
    IReadOnlyList<UiMapControlView> Controls)
{
    public static IReadOnlyList<RawPopupLayer> ForContext(UiMapLayerView raw, FrameObservation frame,
        string bundleId, long ownerHwnd)
    {
        var owner = (frame.ScopedWindows ?? [frame.Window]).FirstOrDefault(window => window.Hwnd == ownerHwnd);
        if (owner is null) return [];
        return RawSurfaceImageLayout.WindowContext(frame, owner).Skip(1).Select(window =>
        {
            var match = raw.Surfaces.SelectMany(surface => surface.Variants.Select(variant => (surface, variant)))
                .FirstOrDefault(item => item.variant.BundleId == bundleId && item.variant.FrameSequence == frame.Sequence &&
                    UiMappingReadModel.Property(item.surface.Source, "hwnd") == $"0x{window.Hwnd:X}");
            var controls = match.surface is null ? [] : raw.ControlsForSurface(match.surface.Id)
                .Where(control => match.variant.ControlIds.Contains(control.Id))
                .Select(control => UiMapPresentation.ForVariant(control, match.variant)).ToArray();
            return new RawPopupLayer(window, match.surface, match.variant,
                RawVariantPresentation.StreamsForWindow(frame, window.Hwnd), controls);
        }).ToArray();
    }

    public static RawDataStreamObservation? PreferredAttempt(IReadOnlyList<RawDataStreamObservation> streams,
        IReadOnlyList<UiMapControlView> controls)
    {
        var visible = RawVariantPresentation.VisibleControlIds(controls);
        return streams.OrderByDescending(stream => controls.Any(control => visible.Contains(control.Id) &&
                UiMappingReadModel.Property(control.Source, "rawStreamId") == stream.StreamId &&
                !string.IsNullOrEmpty(UiMappingReadModel.Property(control.Source, "parentRuntimeId"))))
            .ThenBy(stream => stream.Status == "ok" ? 0 : 1)
            .ThenBy(stream => stream.Controls.Any(control => control.ScopeStatus is "outside-target" or "unverified") ? 1 : 0)
            .ThenBy(stream => stream.Source.StartsWith("uia.raw/", StringComparison.Ordinal) ? 0 :
                stream.Source.StartsWith("msaa/", StringComparison.Ordinal) ? 1 : 2)
            .ThenByDescending(stream => stream.Controls.Count).FirstOrDefault();
    }
}
