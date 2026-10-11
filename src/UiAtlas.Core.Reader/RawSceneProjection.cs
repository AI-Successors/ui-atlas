using UiAtlas.Core.Contracts;
using UiAtlas.Core.Recording;

namespace UiAtlas.Core.Reader;

/// <summary>Contextual scene for a raw delta; source records remain immutable and inspectable.</summary>
public static class RawSceneProjection
{
    public static IReadOnlyList<UiMapControlView> Project(
        IReadOnlyList<UiMapControlView> rawControls, ObservationScene scene, string bundleId,
        long ownerHwnd, string surfaceId)
    {
        var sources = rawControls.SelectMany(control => (control.Source.Observations ?? [])
                .Where(fact => fact.BundleId == bundleId)
                .Select(fact => (Control: control, Fact: fact)))
            .ToLookup(item => (item.Fact.FrameSequence, item.Fact.Control.WindowHwnd, item.Fact.Control.RuntimeId));
        return scene.ObservedControls.Concat(scene.RetainedControls)
            .Where(item => item.EffectiveOwnerHwnd == ownerHwnd)
            .SelectMany(item => sources[(item.SourceFrameSequence, item.Control.WindowHwnd, item.Control.RuntimeId)]
                .Select(source => UiMapPresentation.ForVariant(source.Control, new UiMapVariantView(
                    "scene", "Scene", bundleId, scene.FrameSequence, 1, null, [source.Control.Id],
                    ControlSourceFrames: new Dictionary<string, long> { [source.Control.Id] = item.SourceFrameSequence }))
                    with { OwnerSurfaceId = surfaceId }))
            .DistinctBy(control => control.Id).ToArray();
    }
}
