using UiAtlas.Core.Contracts;

namespace UiAtlas.Core.Reader;

public enum UiUnderstandingLevel
{
    RawDataStreams = 0,
    RawWorld = 1,
    SemanticWorld = 2
}

public sealed record UiMapVariantView(
    string Id,
    string DisplayName,
    string BundleId,
    long FrameSequence,
    int ControlCount,
    EvidenceRef? Evidence,
    IReadOnlyList<string> ControlIds,
    bool IsVisibleByDefault = true,
    string Reason = "observed",
    string DedupedIntoVariantId = "",
    string ObservationScope = "");

public enum UiMapProjectionMode
{
    Window,
    Controls,
    Overlay,
    Structure,
    StructureOverlay,
    Trace,
    Routes
}

public sealed record UiMapProjectionPolicy(
    bool ShowsScene,
    double SceneOpacity,
    bool ShowsControlCrops,
    bool ShowsControlGeometry,
    bool ShowsControlLabels,
    double BlueprintFillOpacity);

public static class UiMapPresentation
{
    public static UiMapVariantView? ResolveControlVariant(
        UiMapControlView control,
        IReadOnlyList<UiMapVariantView> variants,
        UiMapVariantView? current = null)
    {
        ArgumentNullException.ThrowIfNull(control);
        ArgumentNullException.ThrowIfNull(variants);

        static bool ContainsControlEvidence(UiMapControlView candidate, UiMapVariantView variant) =>
            candidate.Evidence.Any(evidence =>
                evidence.FrameSequence == variant.FrameSequence &&
                string.Equals(evidence.BundleId, variant.BundleId, StringComparison.Ordinal));

        if (current is not null)
        {
            var matchingCurrent = variants.FirstOrDefault(variant =>
                variant.FrameSequence == current.FrameSequence &&
                string.Equals(variant.BundleId, current.BundleId, StringComparison.Ordinal));
            if (matchingCurrent is not null && ContainsControlEvidence(control, matchingCurrent))
                return matchingCurrent;
        }

        return variants
            .Where(variant => ContainsControlEvidence(control, variant))
            .OrderByDescending(variant => control.Evidence.Any(evidence =>
                evidence.FrameSequence == variant.FrameSequence &&
                string.Equals(evidence.BundleId, variant.BundleId, StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(evidence.ScreenshotEntry)))
            .ThenByDescending(variant => !string.IsNullOrWhiteSpace(variant.Evidence?.ScreenshotEntry))
            .ThenByDescending(variant => variant.FrameSequence)
            .FirstOrDefault();
    }

    public static RectI ResolveControlBounds(
        UiMapControlView control,
        long? frameSequence,
        string? bundleId,
        IReadOnlyList<UiMapControlView>? siblingControls = null)
    {
        if (frameSequence is null) return control.Bounds;
        return control.Evidence.FirstOrDefault(evidence =>
                   evidence.FrameSequence == frameSequence.Value &&
                   (bundleId is null || string.Equals(evidence.BundleId, bundleId, StringComparison.Ordinal)))
               ?.Bounds ?? control.Bounds;
    }

    public static bool IsRedundantCaptionButton(
        UiMapControlView control,
        long? frameSequence,
        string? bundleId,
        IReadOnlyList<UiMapControlView> siblingControls)
    {
        ArgumentNullException.ThrowIfNull(control);
        ArgumentNullException.ThrowIfNull(siblingControls);
        var role = CaptionButtonRole(control);
        if (role is null) return false;

        var bounds = ResolveControlBounds(control, frameSequence, bundleId);
        var preferred = siblingControls
            .Where(candidate => candidate.OwnerSurfaceId == control.OwnerSurfaceId &&
                                CaptionButtonRole(candidate) == role &&
                                HasEvidenceForFrame(candidate, frameSequence, bundleId))
            .Select(candidate => new
            {
                Control = candidate,
                Bounds = ResolveControlBounds(candidate, frameSequence, bundleId)
            })
            .Where(candidate => LooksLikeSameCaptionButtonCluster(bounds, candidate.Bounds))
            .OrderByDescending(candidate => CaptionButtonProviderPriority(candidate.Control))
            .ThenByDescending(candidate => (long)candidate.Bounds.Width * candidate.Bounds.Height)
            .ThenBy(candidate => candidate.Control.Id, StringComparer.Ordinal)
            .FirstOrDefault();
        return preferred is not null && !string.Equals(preferred.Control.Id, control.Id, StringComparison.Ordinal);
    }

    private static bool HasEvidenceForFrame(UiMapControlView control, long? frameSequence, string? bundleId) =>
        frameSequence is null || control.Evidence.Any(evidence =>
            evidence.FrameSequence == frameSequence.Value &&
            (bundleId is null || string.Equals(evidence.BundleId, bundleId, StringComparison.Ordinal)));

    private static string? CaptionButtonRole(UiMapControlView control)
    {
        if (!control.CanonicalKind.Equals("Button", StringComparison.OrdinalIgnoreCase)) return null;
        var identity = SourceProperty(control.Source, "automationId");
        if (string.IsNullOrWhiteSpace(identity)) identity = control.DisplayName;
        return identity switch
        {
            "Minimize" => "minimize",
            "Maximize" or "Restore" or "Restore Down" => "maximize-restore",
            "Close" => "close",
            _ => null
        };
    }

    private static int CaptionButtonProviderPriority(UiMapControlView control)
    {
        var priority = IsFalse(SourceProperty(control.Source, "offscreen")) ? 1_000 : 0;
        if (!SourceProperty(control.Source, "frameworkId").Equals("UiAtlas.Cached", StringComparison.OrdinalIgnoreCase))
            priority += 500;
        var className = SourceProperty(control.Source, "className");
        if (className.Equals("NetUIAppFrameHelper", StringComparison.OrdinalIgnoreCase)) priority += 250;
        else if (!string.IsNullOrWhiteSpace(className)) priority += 100;
        return priority;
    }

    private static bool LooksLikeSameCaptionButtonCluster(RectI first, RectI second)
    {
        var firstCenterX = first.X + first.Width / 2.0;
        var firstCenterY = first.Y + first.Height / 2.0;
        var secondCenterX = second.X + second.Width / 2.0;
        var secondCenterY = second.Y + second.Height / 2.0;
        return Math.Abs(firstCenterX - secondCenterX) <= Math.Max(first.Width, second.Width) * 2 &&
               Math.Abs(firstCenterY - secondCenterY) <= Math.Max(first.Height, second.Height) * 2;
    }

    public static bool ShouldShowModeLabels(double availableWidth, double expandedToolbarWidth) =>
        availableWidth <= 0 || expandedToolbarWidth <= 0 || availableWidth >= expandedToolbarWidth + 4;

    public static bool ShouldCropSceneToSurface(UiMapProjectionMode mode) =>
        mode is not (UiMapProjectionMode.Window or UiMapProjectionMode.Trace);

    public static UiMapProjectionPolicy PolicyFor(UiMapProjectionMode mode) => mode switch
    {
        UiMapProjectionMode.Window => new(true, 1.0, false, false, false, 0.0),
        UiMapProjectionMode.Controls => new(false, 0.0, true, true, false, 0.96),
        UiMapProjectionMode.Overlay => new(true, 0.68, false, true, false, 0.0),
        UiMapProjectionMode.Structure => new(false, 0.0, false, true, true, 0.94),
        UiMapProjectionMode.StructureOverlay => new(true, 0.68, false, true, false, 0.20),
        UiMapProjectionMode.Trace => new(true, 1.0, false, false, false, 0.0),
        UiMapProjectionMode.Routes => new(false, 0.0, false, false, false, 0.0),
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };

    public static bool ShouldUseControlCrop(UiMapControlView control, UiMapSurfaceView surface)
        => HasReliableBounds(control, surface) &&
           !IsLargeStructuralControl(control, surface) &&
           LooksInteractive(control, surface);

    public static int ControlRenderPriority(UiMapControlView control, UiMapSurfaceView surface)
    {
        if (IsLargeStructuralControl(control, surface)) return 0;
        return LooksInteractive(control, surface) ? 20 : 10;
    }

    public static bool ShouldRenderControl(
        UiMapControlView control,
        UiMapSurfaceView surface,
        UiMapProjectionMode mode,
        bool isSelected = false)
    {
        if (!HasReliableBounds(control, surface) || HasEstimatedGeometry(control)) return false;
        // Raw Data Streams intentionally retain provider evidence for hidden pages,
        // but drawing it over the screenshot makes every dialog tab appear at once.
        // Keep the evidence inspectable while rendering only the effective visible
        // tree. Older maps do not have effectivelyVisible, so honor offscreen too.
        if (!IsVisualCandidate(control.Source) &&
            !IsCachedControl(control.Source) &&
            (IsFalse(SourceProperty(control.Source, "effectivelyVisible")) ||
             IsTrue(SourceProperty(control.Source, "offscreen"))))
            return false;
        if (isSelected && mode != UiMapProjectionMode.Controls) return true;

        return mode switch
        {
            UiMapProjectionMode.Structure or UiMapProjectionMode.StructureOverlay => true,
            UiMapProjectionMode.Controls => ShouldUseControlCrop(control, surface),
            UiMapProjectionMode.Overlay => LooksInteractive(control, surface),
            UiMapProjectionMode.Window or UiMapProjectionMode.Trace or UiMapProjectionMode.Routes => false,
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };
    }

    public static bool IsLargeStructuralControl(UiMapControlView control, UiMapSurfaceView surface)
    {
        if (!HasReliableBounds(control, surface)) return false;
        if (surface.SurfaceKind.Contains("Popup", StringComparison.OrdinalIgnoreCase) && LooksInteractive(control, surface))
            return false;

        var surfaceArea = Math.Max(1d, (double)surface.Bounds.Width * surface.Bounds.Height);
        var controlArea = Math.Max(1d, (double)control.Bounds.Width * control.Bounds.Height);
        if (controlArea / surfaceArea > 0.18) return true;
        if (control.Bounds.Width > surface.Bounds.Width * 0.62 && control.Bounds.Height > surface.Bounds.Height * 0.18)
            return true;

        var text = $"{control.CanonicalKind} {SourceProperty(control.Source, "role")}";
        return ContainsAny(text, "window", "pane", "document", "page", "group", "layout");
    }

    private static bool LooksInteractive(UiMapControlView control, UiMapSurfaceView surface)
    {
        var text = $"{control.CanonicalKind} {SourceProperty(control.Source, "role")} {control.DisplayName}";
        if (ContainsAny(text, "button", "splitbutton", "combobox", "combo box", "menuitem", "menu item",
                "checkbox", "radio", "tab", "edit", "text box", "hyperlink", "slider", "spinner",
                "dataitem", "data item", "headeritem", "header item", "listitem", "list item",
                "treeitem", "tree item", "scrollbar", "scroll bar", "thumb", "canvasitem", "canvas item"))
            return true;
        // Office account flyouts expose actionable tiles as owner-drawn Custom
        // elements with no Invoke pattern. Their accessible names still describe
        // the action precisely, so render the tile while keeping its child text
        // labels non-interactive and avoiding duplicate outlines.
        if (control.CanonicalKind.Contains("custom", StringComparison.OrdinalIgnoreCase) &&
            LooksLikeOfficeAccountAction(control.DisplayName))
            return true;
        if (!surface.SurfaceKind.Contains("Popup", StringComparison.OrdinalIgnoreCase)) return false;
        return control.Bounds.Height <= Math.Max(42d, surface.Bounds.Height * 0.45) &&
               control.Bounds.Width >= Math.Max(24d, surface.Bounds.Width * 0.35) &&
               ContainsAny(text, "menu", "item", "select", "selection", "formatting");
    }

    private static bool LooksLikeOfficeAccountAction(string value)
    {
        var text = value?.Trim() ?? string.Empty;
        return text.Equals("Sign out of this account", StringComparison.OrdinalIgnoreCase) ||
               text.StartsWith("Sign out options for ", StringComparison.OrdinalIgnoreCase) ||
               text.StartsWith("Add a new account or sign in", StringComparison.OrdinalIgnoreCase) ||
               text.StartsWith("Switch to ", StringComparison.OrdinalIgnoreCase) &&
               text.EndsWith(" account", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasReliableBounds(UiMapControlView control, UiMapSurfaceView surface)
        => control.Bounds.Width > 0 && control.Bounds.Height > 0 && surface.Bounds.Width > 0 && surface.Bounds.Height > 0 &&
           ProjectToSurface(control.Bounds, surface.Bounds) is not null;

    private static bool HasEstimatedGeometry(UiMapControlView control) =>
        SourceProperty(control.Source, "frameworkId") is "UiAtlas.Estimated" or "UiAtlas.SurfaceAnchor" ||
        SourceProperty(control.Source, "className").Equals("RevitPropertyGridRow", StringComparison.OrdinalIgnoreCase);

    internal static bool IsVisualCandidate(GraphNode node) =>
        SourceProperty(node, "className") is "UiAtlas.VisualControlRegion" or "UiAtlas.HoverRegion";

    internal static bool IsCachedControl(GraphNode node) =>
        SourceProperty(node, "frameworkId").Equals("UiAtlas.Cached", StringComparison.OrdinalIgnoreCase);

    private static string SourceProperty(GraphNode node, string name) => node.Properties
        .FirstOrDefault(property => string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))?.Value ?? string.Empty;

    private static bool IsTrue(string value) => bool.TryParse(value, out var parsed) && parsed;
    private static bool IsFalse(string value) => bool.TryParse(value, out var parsed) && !parsed;

    private static bool ContainsAny(string value, params string[] candidates)
        => candidates.Any(candidate => value.Contains(candidate, StringComparison.OrdinalIgnoreCase));

    public static RectI? ProjectToSurface(RectI absoluteBounds, RectI surfaceBounds)
    {
        if (absoluteBounds.Width <= 0 || absoluteBounds.Height <= 0 ||
            surfaceBounds.Width <= 0 || surfaceBounds.Height <= 0)
            return null;

        var left = Math.Max(absoluteBounds.X, surfaceBounds.X);
        var top = Math.Max(absoluteBounds.Y, surfaceBounds.Y);
        var right = Math.Min((long)absoluteBounds.X + absoluteBounds.Width,
            (long)surfaceBounds.X + surfaceBounds.Width);
        var bottom = Math.Min((long)absoluteBounds.Y + absoluteBounds.Height,
            (long)surfaceBounds.Y + surfaceBounds.Height);
        if (right <= left || bottom <= top) return null;
        return new RectI(
            checked(left - surfaceBounds.X),
            checked(top - surfaceBounds.Y),
            checked((int)(right - left)),
            checked((int)(bottom - top)));
    }
}

public sealed record UiMapSurfaceView(
    string Id,
    UiUnderstandingLevel Level,
    string DisplayName,
    string SurfaceKind,
    string ParentId,
    RectI Bounds,
    int ControlCount,
    IReadOnlyList<UiMapVariantView> Variants,
    IReadOnlyList<EvidenceRef> Evidence,
    GraphNode Source);

public sealed record UiMapControlView(
    string Id,
    UiUnderstandingLevel Level,
    string DisplayName,
    string CanonicalKind,
    string OwnerSurfaceId,
    string ParentControlId,
    RectI Bounds,
    IReadOnlyList<EvidenceRef> Evidence,
    GraphNode Source);

public sealed record UiMapLayerView(
    UiUnderstandingLevel Level,
    string DisplayName,
    IReadOnlyList<UiMapSurfaceView> Surfaces,
    IReadOnlyList<UiMapControlView> Controls)
{
    public IReadOnlyList<UiMapControlView> ControlsForSurface(string surfaceId, long? frameSequence = null, string? bundleId = null) => Controls
        .Where(control => string.Equals(control.OwnerSurfaceId, surfaceId, StringComparison.Ordinal))
        .Where(control => frameSequence is null || control.Evidence.Any(evidence =>
            evidence.FrameSequence == frameSequence &&
            (bundleId is null || string.Equals(evidence.BundleId, bundleId, StringComparison.Ordinal))))
        .OrderBy(control => control.DisplayName, StringComparer.OrdinalIgnoreCase)
        .ThenBy(control => control.Id, StringComparer.Ordinal)
        .ToArray();
}

public sealed record UiHierarchyGroupView(
    UiUnderstandingLevel Level,
    string DisplayName,
    IReadOnlyList<UiMapSurfaceView> Surfaces);

public enum UiPipelineNodeKind
{
    Application,
    Process,
    NativeSurface,
    RawSurface,
    SemanticSurface
}

public sealed record UiPipelineNodeView(
    string Id,
    UiPipelineNodeKind Kind,
    int Column,
    int Row,
    string DisplayName,
    string Subtitle,
    string? SurfaceId,
    UiUnderstandingLevel? InspectionLevel,
    IReadOnlyList<string> SourceIds);

public sealed record UiPipelineEdgeView(string SourceId, string TargetId, string DisplayName);

public sealed record UiInteractionStepView(
    string Id,
    string BundleId,
    long Sequence,
    string OperationId,
    int Attempt,
    InteractionActor Actor,
    InteractionGestureKind Gesture,
    InteractionActionKind Action,
    InteractionOutcome Outcome,
    string SourceStateId,
    string SourceControlId,
    string TargetStateId,
    long SourceFrameSequence,
    IReadOnlyList<long> ResultFrameSequences,
    string DiagnosticCode,
    EvidenceRef? Evidence,
    IReadOnlyList<string>? TargetStateIds = null)
{
    public IReadOnlyList<string> EffectiveTargetStateIds => TargetStateIds is { Count: > 0 }
        ? TargetStateIds
        : [TargetStateId];
}

public sealed record UiRouteView(
    string SourceStateId,
    string SourceControlId,
    InteractionActionKind Action,
    string TargetStateId,
    int ObservedCount,
    int SuccessCount,
    int FailureCount,
    IReadOnlyList<UiInteractionStepView> Steps);

public sealed record UiAffordanceView(string ControlId, InteractionActionKind Action, bool WasObserved);

public sealed record UiPipelineView(
    IReadOnlyList<UiPipelineNodeView> Nodes,
    IReadOnlyList<UiPipelineEdgeView> Edges,
    IReadOnlyDictionary<int, string> ColumnHeaders);

public sealed class UiMappingReadModel
{
    private readonly IReadOnlyDictionary<UiUnderstandingLevel, UiMapLayerView> _layers;

    public UiMappingReadModel(UiKnowledgeGraph graph)
    {
        Graph = graph;
        Application = graph.Nodes.Single(node => node.Kind == GraphNodeKind.Application);
        ApplicationDisplayName = Application.Label;
        _layers = new Dictionary<UiUnderstandingLevel, UiMapLayerView>
        {
            [UiUnderstandingLevel.RawDataStreams] = BuildLayer(graph, UiUnderstandingLevel.RawDataStreams, "raw-data-streams", "Raw Data Streams"),
            [UiUnderstandingLevel.RawWorld] = BuildLayer(graph, UiUnderstandingLevel.RawWorld, "raw-world", "Raw World"),
            [UiUnderstandingLevel.SemanticWorld] = BuildLayer(graph, UiUnderstandingLevel.SemanticWorld, "semantic-world", "Semantic World")
        };
        InteractionSteps = BuildInteractionSteps(graph);
        Routes = InteractionSteps
            .SelectMany(step => step.EffectiveTargetStateIds.Select(targetStateId => (Step: step, TargetStateId: targetStateId)))
            .GroupBy(item => (item.Step.SourceStateId, item.Step.SourceControlId, item.Step.Action, item.TargetStateId))
            .Select(group => new UiRouteView(
                group.Key.SourceStateId,
                group.Key.SourceControlId,
                group.Key.Action,
                group.Key.TargetStateId,
                group.Count(),
                group.Count(item => item.Step.Outcome == InteractionOutcome.Succeeded),
                group.Count(item => item.Step.Outcome is InteractionOutcome.Failed or InteractionOutcome.TimedOut or
                    InteractionOutcome.NoChange or InteractionOutcome.Cancelled),
                group.Select(item => item.Step).OrderBy(step => step.BundleId, StringComparer.Ordinal)
                    .ThenBy(step => step.Sequence).ToArray()))
            .OrderBy(route => route.SourceStateId, StringComparer.Ordinal)
            .ThenBy(route => route.SourceControlId, StringComparer.Ordinal)
            .ThenBy(route => route.Action)
            .ThenBy(route => route.TargetStateId, StringComparer.Ordinal)
            .ToArray();
        Affordances = BuildAffordances(_layers.Values.SelectMany(layer => layer.Controls), InteractionSteps);
    }

    public UiKnowledgeGraph Graph { get; }
    public GraphNode Application { get; }
    public string ApplicationDisplayName { get; }
    public IReadOnlyList<UiInteractionStepView> InteractionSteps { get; }
    public IReadOnlyList<UiRouteView> Routes { get; }
    public IReadOnlyList<UiAffordanceView> Affordances { get; }
    public IReadOnlyList<UiMapLayerView> Layers =>
        [LayerFor(UiUnderstandingLevel.RawDataStreams), LayerFor(UiUnderstandingLevel.RawWorld), LayerFor(UiUnderstandingLevel.SemanticWorld)];

    public UiMapLayerView LayerFor(UiUnderstandingLevel level) => _layers[level];

    private static IReadOnlyList<UiInteractionStepView> BuildInteractionSteps(UiKnowledgeGraph graph) => graph.Edges
        .Where(edge => edge.Kind == "interaction")
        .GroupBy(edge => (BundleId: EdgeProperty(edge, "sessionId") is { Length: > 0 } sessionId
                ? sessionId
                : edge.Evidence.FirstOrDefault()?.BundleId ?? string.Empty,
            InteractionId: EdgeProperty(edge, "interactionId") is { Length: > 0 } id ? id : edge.Id))
        .Select(group =>
        {
            var edge = group.OrderBy(item => item.ToId, StringComparer.Ordinal).First();
            var resultFrames = group.SelectMany(item => item.Properties.Where(property => property.Name == "resultFrameSequence")
                    .Select(property => long.TryParse(property.Value, out var value) ? value : 0))
                .Where(value => value > 0).Distinct().Order().ToArray();
            return new UiInteractionStepView(
                group.Key.InteractionId,
                group.Key.BundleId,
                LongProperty(edge, "sequence"),
                EdgeProperty(edge, "operationId"),
                (int)LongProperty(edge, "attempt"),
                EnumValue(EdgeProperty(edge, "actor"), InteractionActor.DerivedCandidate),
                EnumValue(EdgeProperty(edge, "gesture"), InteractionGestureKind.Click),
                EnumValue(EdgeProperty(edge, "action"), InteractionActionKind.Unknown),
                EnumValue(EdgeProperty(edge, "outcome"), InteractionOutcome.Unobserved),
                edge.FromId,
                EdgeProperty(edge, "sourceControlId"),
                edge.ToId,
                LongProperty(edge, "sourceFrameSequence"),
                resultFrames,
                EdgeProperty(edge, "diagnosticCode"),
                group.SelectMany(item => item.Evidence)
                    .OrderByDescending(item => resultFrames.Contains(item.FrameSequence))
                    .ThenBy(item => item.FrameSequence).FirstOrDefault(),
                group.Select(item => item.ToId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
        })
        .OrderBy(step => step.BundleId, StringComparer.Ordinal)
        .ThenBy(step => step.Sequence)
        .ThenBy(step => step.Id, StringComparer.Ordinal)
        .ToArray();

    private static IReadOnlyList<UiAffordanceView> BuildAffordances(
        IEnumerable<UiMapControlView> controls,
        IReadOnlyList<UiInteractionStepView> interactions)
    {
        var observed = interactions.Select(step => (step.SourceControlId, step.Action)).ToHashSet();
        return controls.SelectMany(control => control.Source.Properties
                .Where(property => property.Name == "affordance")
                .Select(property => new UiAffordanceView(
                    control.Id,
                    EnumValue(property.Value, InteractionActionKind.Unknown),
                    observed.Contains((control.Id, EnumValue(property.Value, InteractionActionKind.Unknown))))))
            .Where(item => item.Action != InteractionActionKind.Unknown)
            .Distinct()
            .OrderBy(item => item.ControlId, StringComparer.Ordinal)
            .ThenBy(item => item.Action)
            .ToArray();
    }

    private static string EdgeProperty(GraphEdge edge, string name) => edge.Properties
        .FirstOrDefault(property => property.Name == name)?.Value ?? string.Empty;

    private static long LongProperty(GraphEdge edge, string name) =>
        long.TryParse(EdgeProperty(edge, name), out var value) ? value : 0;

    private static T EnumValue<T>(string value, T fallback) where T : struct, Enum =>
        Enum.TryParse<T>(value, true, out var parsed) ? parsed : fallback;

    public IReadOnlyList<UiHierarchyGroupView> BuildHierarchy(UiUnderstandingLevel horizon) =>
        Enum.GetValues<UiUnderstandingLevel>()
            .Where(level => level <= horizon)
            .Select(level => new UiHierarchyGroupView(level, LevelDisplayName(level), LayerFor(level).Surfaces))
            .ToArray();

    public IReadOnlyList<UiMapSurfaceView> ResolvePipelineSurfaces(UiPipelineNodeView node)
    {
        if (node.InspectionLevel is null) return [];
        var layer = LayerFor(node.InspectionLevel.Value);
        var ids = node.SourceIds.Append(node.SurfaceId ?? string.Empty)
            .Where(id => id.Length > 0).ToHashSet(StringComparer.Ordinal);
        return layer.Surfaces.Where(surface => ids.Contains(surface.Id)).ToArray();
    }

    public IReadOnlyList<UiMapVariantView> VariantsFor(
        IReadOnlyList<UiMapSurfaceView> surfaces,
        UiMapControlView? requiredControl = null) => surfaces
        .SelectMany(surface => surface.Variants)
        .Where(variant => variant.IsVisibleByDefault ||
                          requiredControl is not null && requiredControl.Evidence.Any(evidence =>
                              evidence.FrameSequence == variant.FrameSequence &&
                              string.Equals(evidence.BundleId, variant.BundleId, StringComparison.Ordinal)))
        .GroupBy(variant => (variant.BundleId, variant.FrameSequence))
        .OrderBy(group => group.Key.BundleId, StringComparer.Ordinal)
        .ThenBy(group => group.Key.FrameSequence)
        .Select(group =>
        {
            var values = group.ToArray();
            var controlIds = values.SelectMany(value => value.ControlIds).Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToArray();
            return new UiMapVariantView(
                string.Join("+", values.Select(value => value.Id).OrderBy(id => id, StringComparer.Ordinal)),
                $"Observed frame {group.Key.FrameSequence}",
                group.Key.BundleId,
                group.Key.FrameSequence,
                controlIds.Length,
                values.Select(value => value.Evidence).FirstOrDefault(value => value is not null), controlIds);
        }).ToArray();

    public UiPipelineView BuildPipeline(UiUnderstandingLevel horizon)
    {
        var nodes = new List<UiPipelineNodeView>();
        var edges = new List<UiPipelineEdgeView>();
        var appNodeId = "pipeline:" + Application.Id;
        var processNodeId = appNodeId + ":process";
        var processName = Property(Application, "processName") ?? ApplicationDisplayName;
        nodes.Add(new(appNodeId, UiPipelineNodeKind.Application, 0, 0, processName,
            $"Application identity | {Application.Id}", null, null, [Application.Id]));
        nodes.Add(new(processNodeId, UiPipelineNodeKind.Process, 1, 0, processName,
            $"Process | {LayerFor(UiUnderstandingLevel.RawDataStreams).Surfaces.Count} Raw Data Streams variants", null, null, [Application.Id]));
        edges.Add(new(appNodeId, processNodeId, "process"));

        var rawStreams = LayerFor(UiUnderstandingLevel.RawDataStreams);
        var nativeGroups = rawStreams.Surfaces
            .GroupBy(surface => string.Join('|',
                Property(surface.Source, "nativeWindowType") ?? surface.SurfaceKind,
                Property(surface.Source, "className") ?? string.Empty,
                Property(surface.Source, "role") ??
                (Property(surface.Source, "ownerHwnd") is null ? "root" : "owned")), StringComparer.Ordinal)
            .OrderBy(group => group.Any(IsPrimaryPipelineSurface) ? 0 : 1)
            .ThenBy(group => group.Min(surface => surface.Evidence.FirstOrDefault()?.FrameSequence ?? long.MaxValue))
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .ToArray();
        var nativeNodeByRdsSurface = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var nativeRow = 0; nativeRow < nativeGroups.Length; nativeRow++)
        {
            var group = nativeGroups[nativeRow];
            var surfaces = group.OrderBy(surface => surface.Evidence.FirstOrDefault()?.FrameSequence ?? long.MaxValue).ToArray();
            var representative = surfaces.OrderByDescending(surface => surface.ControlCount).First();
            var id = "pipeline:native:" + StableSuffix(group.Key);
            var nativeKind = Property(representative.Source, "nativeWindowType") ?? representative.SurfaceKind;
            var nativeRole = Property(representative.Source, "role") ?? string.Empty;
            var display = nativeRole == "peer-root" && !string.IsNullOrWhiteSpace(representative.DisplayName)
                ? representative.DisplayName.Trim()
                : NativeDisplayName(nativeKind, representative.DisplayName);
            nodes.Add(new(id, UiPipelineNodeKind.NativeSurface, 2, nativeRow, display,
                $"{NativeKindDisplay(nativeKind)} | {surfaces.Length} raw variants", representative.Id,
                UiUnderstandingLevel.RawDataStreams,
                surfaces.Select(surface => surface.Id).ToArray()));
            edges.Add(new(processNodeId, id, "raw data stream"));
            foreach (var surface in surfaces) nativeNodeByRdsSurface[surface.Id] = id;
        }

        if (horizon >= UiUnderstandingLevel.RawWorld)
        {
            var rawSurfaces = LayerFor(UiUnderstandingLevel.RawWorld).Surfaces
                .OrderBy(surface => IsPrimaryPipelineSurface(surface) ? 0 : 1)
                .ThenBy(surface => surface.Evidence.FirstOrDefault()?.FrameSequence ?? long.MaxValue)
                .ThenBy(surface => surface.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(surface => surface.Id, StringComparer.Ordinal)
                .ToArray();
            for (var rawRow = 0; rawRow < rawSurfaces.Length; rawRow++)
            {
                var surface = rawSurfaces[rawRow];
                var id = "pipeline:raw:" + surface.Id;
                var visibleVariantCount = surface.Variants.Count(variant => variant.IsVisibleByDefault);
                nodes.Add(new(id, UiPipelineNodeKind.RawSurface, 3, rawRow, surface.DisplayName,
                    $"{surface.SurfaceKind} | {surface.ControlCount} controls | {visibleVariantCount} observed variants",
                    surface.Id, UiUnderstandingLevel.RawWorld, [surface.Id]));
                var nativeSources = Properties(surface.Source, "sourceRawDataStreamSurfaceId")
                    .Select(source => nativeNodeByRdsSurface.GetValueOrDefault(source))
                    .Where(source => source is not null).Cast<string>().Distinct(StringComparer.Ordinal).ToArray();
                foreach (var source in nativeSources.Length > 0 ? nativeSources : nativeNodeByRdsSurface.Values.Take(1))
                    edges.Add(new(source, id, "Raw World"));
            }
        }

        if (horizon >= UiUnderstandingLevel.SemanticWorld)
        {
            var rawRows = nodes.Where(node => node.Kind == UiPipelineNodeKind.RawSurface && node.SurfaceId is not null)
                .ToDictionary(node => node.SurfaceId!, node => node.Row, StringComparer.Ordinal);
            var usedRows = new HashSet<int>();
            var nextSemanticRow = 0;
            var semanticSurfaces = LayerFor(UiUnderstandingLevel.SemanticWorld).Surfaces
                .OrderBy(surface => Properties(surface.Source, "sourceRawSurfaceId")
                    .Select(source => rawRows.GetValueOrDefault(source, int.MaxValue)).DefaultIfEmpty(int.MaxValue).Min())
                .ThenBy(surface => IsPrimaryPipelineSurface(surface) ? 0 : 1)
                .ThenBy(surface => surface.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(surface => surface.Id, StringComparer.Ordinal)
                .ToArray();
            foreach (var surface in semanticSurfaces)
            {
                var id = "pipeline:semantic:" + surface.Id;
                var inheritedRow = Properties(surface.Source, "sourceRawSurfaceId")
                    .Select(source => rawRows.GetValueOrDefault(source, -1))
                    .Where(row => row >= 0)
                    .Select(row => (int?)row)
                    .FirstOrDefault() ?? -1;
                var semanticRow = inheritedRow >= 0 && usedRows.Add(inheritedRow)
                    ? inheritedRow
                    : Enumerable.Range(nextSemanticRow, semanticSurfaces.Length + usedRows.Count + 1).First(usedRows.Add);
                nextSemanticRow = Math.Max(nextSemanticRow, semanticRow + 1);
                nodes.Add(new(id, UiPipelineNodeKind.SemanticSurface, 4, semanticRow, surface.DisplayName,
                    $"{surface.SurfaceKind} | {surface.ControlCount} controls", surface.Id, UiUnderstandingLevel.SemanticWorld, [surface.Id]));
                foreach (var sourceRawId in Properties(surface.Source, "sourceRawSurfaceId"))
                    edges.Add(new("pipeline:raw:" + sourceRawId, id, "Semantic World"));
            }
        }

        return new(nodes, edges, new Dictionary<int, string>
        {
            [0] = "Application",
            [1] = "Process",
            [2] = "Raw Data Streams",
            [3] = "Raw World",
            [4] = "Semantic World"
        });
    }

    private static bool IsPrimaryPipelineSurface(UiMapSurfaceView surface)
    {
        if (surface.SurfaceKind.Equals("RawWindow", StringComparison.OrdinalIgnoreCase) ||
            surface.SurfaceKind.Equals("SemanticWindow", StringComparison.OrdinalIgnoreCase))
            return true;
        var nativeType = Property(surface.Source, "nativeWindowType") ?? string.Empty;
        if (nativeType.Equals("Normal", StringComparison.OrdinalIgnoreCase) || nativeType.Equals("RawWindow", StringComparison.OrdinalIgnoreCase))
            return true;
        var semanticClass = Property(surface.Source, "semanticClass") ?? string.Empty;
        return semanticClass.Equals("SemanticWindow", StringComparison.OrdinalIgnoreCase);
    }

    private static UiMapLayerView BuildLayer(UiKnowledgeGraph graph, UiUnderstandingLevel level, string layer, string displayName)
    {
        var surfaceKind = level == UiUnderstandingLevel.RawDataStreams ? GraphNodeKind.Window : GraphNodeKind.Surface;
        var surfaceNodes = graph.Nodes
            .Where(node => node.Kind == surfaceKind && Property(node, "layer") == layer)
            .Where(node => level != UiUnderstandingLevel.SemanticWorld || Property(node, "semanticSurfaceKind") != "PopupFamily")
            .OrderBy(node => node.Label, StringComparer.OrdinalIgnoreCase)
            .ThenBy(node => node.Id, StringComparer.Ordinal)
            .ToArray();
        var controls = graph.Nodes
            .Where(node => node.Kind == GraphNodeKind.Control && Property(node, "layer") == layer)
            .Select(node => new UiMapControlView(
                node.Id,
                level,
                node.Label,
                Property(node, "controlType") ?? "Control",
                level switch
                {
                    UiUnderstandingLevel.RawDataStreams => Property(node, "rawDataStreamSurfaceId") ?? FindSurfaceParent(graph, node, layer),
                    UiUnderstandingLevel.RawWorld => Property(node, "rawSurfaceId") ?? FindSurfaceParent(graph, node, layer),
                    _ => Property(node, "semanticSurfaceId") ?? FindSurfaceParent(graph, node, layer)
                },
                graph.Nodes.Any(parent => parent.Id == node.ParentId && parent.Kind == GraphNodeKind.Control) ? node.ParentId : string.Empty,
                node.Evidence.FirstOrDefault()?.Bounds ?? new RectI(0, 0, 0, 0),
                node.Evidence,
                node))
            .ToArray();
        var controlsBySurface = controls.GroupBy(control => control.OwnerSurfaceId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var rawStatesBySurface = graph.Nodes
            .Where(node => node.Kind == GraphNodeKind.State && Property(node, "layer") == "raw-world")
            .GroupBy(node => node.ParentId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var surfaces = surfaceNodes.Select(node =>
        {
            var stateSurfaceId = level == UiUnderstandingLevel.SemanticWorld
                ? Property(node, "sourceRawSurfaceId") ?? node.Id
                : node.Id;
            var sourceStates = rawStatesBySurface.GetValueOrDefault(stateSurfaceId) ?? [];
            var statesByFrame = sourceStates
                .SelectMany(state => state.Evidence.Select(evidence => new { evidence.BundleId, evidence.FrameSequence, State = state }))
                .GroupBy(value => (value.BundleId, value.FrameSequence))
                .ToDictionary(group => group.Key, group => group.First().State);
            var suppressVariants = level == UiUnderstandingLevel.SemanticWorld &&
                string.Equals(Property(node, "semanticSurfaceKind"), "PopupVariant", StringComparison.Ordinal);
            var variants = suppressVariants ? [] : node.Evidence
                .Where(evidence => evidence.FrameSequence > 0)
                .GroupBy(evidence => (evidence.BundleId, evidence.FrameSequence))
                .OrderBy(group => group.Key.BundleId, StringComparer.Ordinal)
                .ThenBy(group => group.Key.FrameSequence)
                .Select(group =>
                {
                    var evidence = group.First();
                    var frameControls = controls.Where(control =>
                        string.Equals(control.OwnerSurfaceId, node.Id, StringComparison.Ordinal) &&
                        control.Evidence.Any(item => item.FrameSequence == group.Key.FrameSequence &&
                                                     string.Equals(item.BundleId, group.Key.BundleId, StringComparison.Ordinal)));
                    if (level == UiUnderstandingLevel.RawDataStreams)
                        frameControls = frameControls.Where(IsPresentableRawControl);
                    var visibleFrameControls = frameControls.ToArray();
                    var sourceState = statesByFrame.GetValueOrDefault(group.Key);
                    return new UiMapVariantView(
                        $"{node.Id}:frame:{group.Key.BundleId}:{group.Key.FrameSequence}",
                        sourceState?.Label ?? $"Observed frame {group.Key.FrameSequence}",
                        group.Key.BundleId,
                        group.Key.FrameSequence,
                        visibleFrameControls.Length,
                        evidence,
                        visibleFrameControls.Select(control => control.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray(),
                        ObservationScope: sourceState is null
                            ? string.Empty
                            : Property(sourceState, "observationScope") ?? string.Empty);
                }).ToArray();
            return new UiMapSurfaceView(
                node.Id,
                level,
                node.Label,
                Property(node, level == UiUnderstandingLevel.SemanticWorld ? "semanticClass" : "surfaceClass") ??
                    Property(node, level == UiUnderstandingLevel.SemanticWorld ? "semanticSurfaceKind" : "surfaceClass") ??
                    Property(node, "nativeWindowType") ?? "Surface",
                node.ParentId,
                node.Evidence.FirstOrDefault()?.Bounds ?? new RectI(0, 0, 0, 0),
                controlsBySurface.GetValueOrDefault(node.Id),
                variants,
                node.Evidence,
                node);
        }).ToArray();
        if (level is UiUnderstandingLevel.RawWorld or UiUnderstandingLevel.SemanticWorld)
        {
            var popupFrames = surfaces
                .Where(surface => surface.SurfaceKind.Contains("Popup", StringComparison.OrdinalIgnoreCase))
                .SelectMany(surface => surface.Evidence)
                .Where(evidence => evidence.FrameSequence > 0)
                .Select(evidence => FrameKey(evidence.BundleId, evidence.FrameSequence))
                .ToHashSet();
            surfaces = surfaces.Select(surface => surface with
            {
                Variants = ClassifyHigherWorldVariants(surface, controls, popupFrames)
            }).ToArray();
        }
        return new(level, displayName, surfaces, controls);
    }

    private static bool IsPresentableRawControl(UiMapControlView control)
    {
        // Visual and hover candidates deliberately use offscreen/effectivelyVisible=false
        // to mean "not yet confirmed by an accessibility provider". They still have
        // observed screen geometry and must remain visible in the raw frame variant.
        if (UiMapPresentation.IsVisualCandidate(control.Source) ||
            UiMapPresentation.IsCachedControl(control.Source))
            return control.Bounds.Width > 0 && control.Bounds.Height > 0;

        var effective = Property(control.Source, "effectivelyVisible");
        if (bool.TryParse(effective, out var isEffective) && !isEffective) return false;
        return !bool.TryParse(Property(control.Source, "offscreen"), out var isOffscreen) || !isOffscreen;
    }

    private static IReadOnlyList<UiMapVariantView> ClassifyHigherWorldVariants(
        UiMapSurfaceView surface,
        IReadOnlyList<UiMapControlView> controls,
        IReadOnlySet<string> popupFrames)
    {
        if (surface.SurfaceKind.Contains("Popup", StringComparison.OrdinalIgnoreCase)) return [];

        var controlsById = controls.ToDictionary(control => control.Id, StringComparer.Ordinal);
        var acceptedStable = new List<(string Id, HashSet<string> Keys)>();
        var acceptedVisual = new List<(string Id, HashSet<string> Keys)>();
        var result = new List<UiMapVariantView>(surface.Variants.Count);
        foreach (var variant in surface.Variants.OrderBy(item => item.BundleId, StringComparer.Ordinal).ThenBy(item => item.FrameSequence))
        {
            var frameControls = variant.ControlIds
                .Select(id => controlsById.GetValueOrDefault(id))
                .Where(control => control is not null)
                .Cast<UiMapControlView>()
                .ToArray();
            var stable = frameControls.Select(ControlStableKey)
                .Where(key => key.Length > 0)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var visual = frameControls.Select(control => ControlVisualKey(control, surface.Bounds))
                .Where(key => key.Length > 0)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var visible = true;
            var reason = "observed";
            var dedupedInto = string.Empty;
            if (frameControls.Length == 0)
            {
                visible = false;
                reason = "empty_frame";
            }
            else if (string.Equals(variant.ObservationScope, "control-delta", StringComparison.Ordinal))
            {
                // A control delta records click evidence only. It deliberately
                // reuses the preceding screenshot and can contain a unique
                // pointer marker or focus state, so similarity checks alone are
                // not sufficient to keep it out of the screen carousel.
                visible = false;
                reason = "control_delta";
            }
            else if (popupFrames.Contains(FrameKey(variant.BundleId, variant.FrameSequence)))
            {
                visible = false;
                reason = "popup_effective_owner_frame";
            }
            else if (TryFindNearDuplicate(stable, acceptedStable, out dedupedInto))
            {
                visible = false;
                reason = "duplicate_content";
            }
            else if (TryFindNearDuplicate(visual, acceptedVisual, out dedupedInto))
            {
                visible = false;
                reason = "duplicate_visual_content";
            }
            if (visible)
            {
                acceptedStable.Add((variant.Id, stable));
                acceptedVisual.Add((variant.Id, visual));
            }
            result.Add(variant with
            {
                IsVisibleByDefault = visible,
                Reason = reason,
                DedupedIntoVariantId = dedupedInto
            });
        }
        return result;
    }

    private static string ControlStableKey(UiMapControlView control)
    {
        var selectors = Properties(control.Source, "stableSelector").OrderBy(value => value, StringComparer.OrdinalIgnoreCase);
        return string.Join('|', new[]
        {
            control.Id,
            control.CanonicalKind,
            Property(control.Source, "automationId") ?? string.Empty,
            Property(control.Source, "className") ?? string.Empty,
            Property(control.Source, "controlPath") ?? string.Empty
        }.Concat(selectors)).ToLowerInvariant();
    }

    private static string ControlVisualKey(UiMapControlView control, RectI surfaceBounds)
    {
        var local = UiMapPresentation.ProjectToSurface(control.Bounds, surfaceBounds);
        if (local is null) return string.Empty;
        static int Quantize(int value) => (int)Math.Round(value / 4d, MidpointRounding.AwayFromZero) * 4;
        return string.Join('|',
            control.CanonicalKind.Trim().ToLowerInvariant(),
            control.DisplayName.Trim().ToLowerInvariant(),
            Quantize(local.X), Quantize(local.Y), Quantize(local.Width), Quantize(local.Height));
    }

    private static bool TryFindNearDuplicate(
        HashSet<string> keys,
        IReadOnlyList<(string Id, HashSet<string> Keys)> accepted,
        out string duplicateId)
    {
        duplicateId = string.Empty;
        if (keys.Count == 0) return false;
        foreach (var candidate in accepted)
        {
            if (candidate.Keys.Count == 0) continue;
            var intersection = keys.Intersect(candidate.Keys, StringComparer.OrdinalIgnoreCase).Count();
            var union = keys.Union(candidate.Keys, StringComparer.OrdinalIgnoreCase).Count();
            if (union > 0 && intersection / (double)union >= 0.94)
            {
                duplicateId = candidate.Id;
                return true;
            }
        }
        return false;
    }

    private static bool TryFindNearSubset(
        HashSet<string> keys,
        IReadOnlyList<(string Id, HashSet<string> Keys)> accepted,
        out string duplicateId)
    {
        duplicateId = string.Empty;
        if (keys.Count < 2) return false;
        foreach (var candidate in accepted)
        {
            if (candidate.Keys.Count < keys.Count) continue;
            var intersection = keys.Intersect(candidate.Keys, StringComparer.OrdinalIgnoreCase).Count();
            if (intersection / (double)keys.Count < .94) continue;
            duplicateId = candidate.Id;
            return true;
        }
        return false;
    }

    private static string FrameKey(string bundleId, long frameSequence) =>
        $"{bundleId}\u001f{frameSequence}";

    private static string FindSurfaceParent(UiKnowledgeGraph graph, GraphNode node, string layer)
    {
        var byId = graph.Nodes.ToDictionary(candidate => candidate.Id, StringComparer.Ordinal);
        var current = node;
        for (var depth = 0; depth < 64 && byId.TryGetValue(current.ParentId, out var parent); depth++)
        {
            if ((parent.Kind is GraphNodeKind.Window or GraphNodeKind.Surface) && Property(parent, "layer") == layer) return parent.Id;
            current = parent;
        }
        return string.Empty;
    }

    public static string? Property(GraphNode node, string name) =>
        node.Properties.FirstOrDefault(property => property.Name == name)?.Value;

    public static IReadOnlyList<string> Properties(GraphNode node, string name) =>
        node.Properties.Where(property => property.Name == name).Select(property => property.Value).Distinct(StringComparer.Ordinal).ToArray();

    private static string StableSuffix(string value) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)))[..24].ToLowerInvariant();

    private static string NativeDisplayName(string kind, string fallback) => kind switch
    {
        "RawWindow" => "Main Window",
        "RawDialogWindow" => "Dialog Window",
        "RawPopupWindow" => "Popup Window",
        "RawToolWindow" => "Tool Window",
        _ => fallback
    };

    private static string NativeKindDisplay(string kind) => kind switch
    {
        "RawWindow" => "Normal window",
        "RawDialogWindow" => "Dialog window",
        "RawPopupWindow" => "Popup window",
        "RawToolWindow" => "Tool window",
        _ => kind
    };

    private static string LevelDisplayName(UiUnderstandingLevel level) => level switch
    {
        UiUnderstandingLevel.RawDataStreams => "Raw Data Streams",
        UiUnderstandingLevel.RawWorld => "Raw World",
        _ => "Semantic World"
    };
}
