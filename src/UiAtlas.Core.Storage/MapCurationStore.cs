using System.Text.Json;
using UiAtlas.Core.Build;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Recording;

namespace UiAtlas.Core.Storage;

public sealed record NormalizedControlBounds(double X, double Y, double Width, double Height);

public sealed record ManualControlAnnotation(
    string Id,
    string SurfaceStableKey,
    string SurfaceId,
    string Label,
    string ControlKind,
    NormalizedControlBounds Bounds,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    string ActionVerificationStatus = "Unobserved");

public sealed record ControlCurationRule(
    string StableKey,
    string Action,
    DateTimeOffset UpdatedUtc);

public sealed record MapCurationDocument(
    string FormatVersion,
    string LogicalMapId,
    IReadOnlyList<ManualControlAnnotation> ManualControls,
    IReadOnlyList<ControlCurationRule> ControlRules)
{
    public static MapCurationDocument Empty(string logicalMapId) =>
        new("map-curation/1", logicalMapId, [], []);
}

public static class MapCurationStore
{
    private const int MaximumManualControls = 20_000;
    private const int MaximumRules = 100_000;

    public static string PathForMap(string mapPath) => Path.GetFullPath(mapPath) + ".curation.json";

    public static NormalizedControlBounds? NormalizeBounds(RectI controlBounds, RectI surfaceBounds)
    {
        if (surfaceBounds.Width <= 0 || surfaceBounds.Height <= 0) return null;
        var left = Math.Max(surfaceBounds.X, controlBounds.X);
        var top = Math.Max(surfaceBounds.Y, controlBounds.Y);
        var right = Math.Min((long)surfaceBounds.X + surfaceBounds.Width,
            (long)controlBounds.X + controlBounds.Width);
        var bottom = Math.Min((long)surfaceBounds.Y + surfaceBounds.Height,
            (long)controlBounds.Y + controlBounds.Height);
        if (right - left < 1 || bottom - top < 1) return null;
        return new(
            (left - surfaceBounds.X) / (double)surfaceBounds.Width,
            (top - surfaceBounds.Y) / (double)surfaceBounds.Height,
            (right - left) / (double)surfaceBounds.Width,
            (bottom - top) / (double)surfaceBounds.Height);
    }

    public static RectI ProjectBounds(NormalizedControlBounds bounds, RectI surfaceBounds) => new(
        surfaceBounds.X + (int)Math.Round(bounds.X * surfaceBounds.Width),
        surfaceBounds.Y + (int)Math.Round(bounds.Y * surfaceBounds.Height),
        Math.Max(1, (int)Math.Round(bounds.Width * surfaceBounds.Width)),
        Math.Max(1, (int)Math.Round(bounds.Height * surfaceBounds.Height)));

    public static MapCurationDocument Load(string mapPath, string logicalMapId)
    {
        var path = PathForMap(mapPath);
        if (!File.Exists(path)) return MapCurationDocument.Empty(logicalMapId);
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length > 64 * 1024 * 1024)
            throw new InvalidDataException("Map curation data exceeds the supported size.");
        var value = JsonSerializer.Deserialize<MapCurationDocument>(bytes, JsonDefaults.Options)
                    ?? throw new InvalidDataException("Map curation data is malformed.");
        Validate(value, logicalMapId);
        return value;
    }

    public static void Save(string mapPath, MapCurationDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        Validate(document, document.LogicalMapId);
        AtomicFile.Publish(PathForMap(mapPath), temporary =>
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(document, JsonDefaults.Options)));
    }

    public static MapCurationDocument? ResolveForImport(
        string sourceMapPath,
        UiKnowledgeGraph sourceGraph,
        string targetMapPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceMapPath);
        ArgumentNullException.ThrowIfNull(sourceGraph);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetMapPath);

        var source = Path.GetFullPath(sourceMapPath);
        var target = Path.GetFullPath(targetMapPath);
        if (File.Exists(PathForMap(source)))
            return Load(source, sourceGraph.Metadata.EffectiveLogicalMapId);

        // Re-importing a freshly rebuilt copy of the same logical map must not
        // discard the user's durable annotations merely because the producer
        // did not copy the optional sidecar next to the rebuilt database.
        if (!source.Equals(target, StringComparison.OrdinalIgnoreCase) &&
            File.Exists(target) && File.Exists(PathForMap(target)))
        {
            var targetGraph = SqliteGraphStore.Load(target);
            if (targetGraph.Metadata.EffectiveLogicalMapId.Equals(
                    sourceGraph.Metadata.EffectiveLogicalMapId,
                    StringComparison.Ordinal))
                return Load(target, targetGraph.Metadata.EffectiveLogicalMapId);
        }

        return null;
    }

    public static UiKnowledgeGraph Apply(UiKnowledgeGraph graph, MapCurationDocument document)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(document);
        Validate(document, graph.Metadata.EffectiveLogicalMapId);

        var current = graph;
        foreach (var rule in document.ControlRules.OrderBy(rule => rule.UpdatedUtc))
        {
            var target = current.Nodes.FirstOrDefault(node =>
                node.Kind == GraphNodeKind.Control && node.StableKey == rule.StableKey);
            if (target is null) continue;
            try
            {
                current = rule.Action switch
                {
                    "Confirm" when GraphControlConfirmation.IsConfirmableButtonCandidate(target) =>
                        GraphControlConfirmation.ConfirmButtonCandidate(current, target.Id, rule.UpdatedUtc),
                    "Suppress" when GraphControlConfirmation.IsRemovableButtonCandidate(target) =>
                        GraphControlConfirmation.RemoveButtonCandidate(current, target.Id),
                    _ => current
                };
            }
            catch (InvalidOperationException)
            {
                // A later native capture can supersede a visual candidate. Keep
                // the rule for future rebuilds, but never delete observed data.
            }
        }

        foreach (var annotation in document.ManualControls.OrderBy(item => item.CreatedUtc))
            current = ApplyManualControl(current, annotation);
        return current;
    }

    public static UiKnowledgeGraph Reapply(UiKnowledgeGraph graph, MapCurationDocument document)
    {
        ArgumentNullException.ThrowIfNull(graph);
        var standaloneIds = graph.Nodes
            .Where(node => node.Kind == GraphNodeKind.Control &&
                           (Property(node, "frameworkId") == "UiAtlas.UserAnnotation" ||
                            Property(node, "className") == "UiAtlas.UserDrawnControl"))
            .Select(node => node.Id)
            .ToHashSet(StringComparer.Ordinal);
        var nodes = graph.Nodes
            .Where(node => !standaloneIds.Contains(node.Id))
            .Select(node => Property(node, "manualAnnotationId") is null ? node : node with
            {
                Properties = node.Properties.Where(property => property.Name is not
                    ("manualAnnotationId" or "confirmationSource" or "geometrySource" or
                     "interactionMethod" or "actionVerificationStatus" or "annotationScope" or
                     "safeForAutoExplore")).ToArray()
            })
            .ToArray();
        var edges = graph.Edges
            .Where(edge => !standaloneIds.Contains(edge.FromId) && !standaloneIds.Contains(edge.ToId))
            .ToArray();
        return Apply(Rehash(graph, nodes, edges), document);
    }

    public static MapCurationDocument UpsertManualControl(
        MapCurationDocument document,
        ManualControlAnnotation annotation)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(annotation);
        var controls = document.ManualControls
            .Where(item => item.Id != annotation.Id)
            .Append(annotation)
            .OrderBy(item => item.CreatedUtc)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .ToArray();
        return document with { ManualControls = controls };
    }

    public static MapCurationDocument RemoveManualControl(MapCurationDocument document, string annotationId) =>
        document with
        {
            ManualControls = document.ManualControls
                .Where(item => item.Id != annotationId)
                .ToArray()
        };

    public static MapCurationDocument UpsertRule(
        MapCurationDocument document,
        string stableKey,
        string action,
        DateTimeOffset updatedUtc)
    {
        if (action is not ("Confirm" or "Suppress"))
            throw new ArgumentException("Unsupported curation action.", nameof(action));
        var rules = document.ControlRules
            .Where(rule => rule.StableKey != stableKey)
            .Append(new(stableKey, action, updatedUtc))
            .OrderBy(rule => rule.StableKey, StringComparer.Ordinal)
            .ToArray();
        return document with { ControlRules = rules };
    }

    private static UiKnowledgeGraph ApplyManualControl(
        UiKnowledgeGraph graph,
        ManualControlAnnotation annotation)
    {
        var semanticSurface = graph.Nodes.FirstOrDefault(node =>
            node.Kind == GraphNodeKind.Surface && Layer(node) == "semantic-world" &&
            (node.StableKey == annotation.SurfaceStableKey || node.Id == annotation.SurfaceId));
        if (semanticSurface is null) return graph;
        var rawSurfaceId = Property(semanticSurface, "sourceRawSurfaceId");
        if (string.IsNullOrWhiteSpace(rawSurfaceId)) return graph;
        var rawSurface = graph.Nodes.FirstOrDefault(node => node.Id == rawSurfaceId &&
                                                           node.Kind == GraphNodeKind.Surface &&
                                                           Layer(node) == "raw-world");
        if (rawSurface is null) return graph;

        var evidence = ProjectEvidence(semanticSurface.Evidence, annotation.Bounds);
        if (evidence.Count == 0) return graph;
        var successfulClicks = SuccessfulUserClicks(graph, rawSurface, semanticSurface, evidence);
        var actionObserved = successfulClicks.Count > 0;
        var effectiveAnnotation = actionObserved && annotation.ActionVerificationStatus != "Observed"
            ? annotation with { ActionVerificationStatus = "Observed" }
            : annotation;
        var matching = graph.Nodes.FirstOrDefault(node =>
            node.Kind == GraphNodeKind.Control && Layer(node) == "semantic-world" &&
            Property(node, "semanticSurfaceId") == semanticSurface.Id &&
            IsButton(node) && LabelsMatch(node.Label, annotation.Label) &&
            node.Evidence.Any(existing => evidence.Any(projected =>
                SameFrame(existing, projected) && existing.Bounds is { } left && projected.Bounds is { } right &&
                IntersectionOverUnion(left, right) >= .72)));
        if (matching is not null)
            return ConfirmMatchedControl(graph, matching, effectiveAnnotation);

        var rawId = StableIdentity.Create("control", "raw-world", graph.Metadata.EffectiveLogicalMapId, annotation.Id);
        var semanticId = StableIdentity.Create("control", "semantic-world", graph.Metadata.EffectiveLogicalMapId, annotation.Id);
        var raw = new GraphNode(
            rawId,
            GraphNodeKind.Control,
            rawSurface.Id,
            rawId,
            effectiveAnnotation.Label,
            ManualProperties("raw-world", effectiveAnnotation, rawSurface.Id, null, rawId),
            evidence);
        var semantic = new GraphNode(
            semanticId,
            GraphNodeKind.Control,
            semanticSurface.Id,
            semanticId,
            effectiveAnnotation.Label,
            ManualProperties("semantic-world", effectiveAnnotation, rawSurface.Id, semanticSurface.Id, rawId),
            evidence);
        var nodes = graph.Nodes.Where(node => node.Id != rawId && node.Id != semanticId)
            .Concat([raw, semantic])
            .ToArray();
        var successfulClickIds = successfulClicks.Select(edge => edge.Id).ToHashSet(StringComparer.Ordinal);
        var edges = graph.Edges.Where(edge => edge.ToId != rawId && edge.ToId != semanticId)
            .Select(edge => successfulClickIds.Contains(edge.Id) ? edge with
            {
                Properties = UpsertProperties(edge.Properties,
                [
                    new("sourceControlId", rawId),
                    new("manualAnnotationId", annotation.Id)
                ])
            } : edge)
            .Concat([
                Contains(rawSurface.Id, rawId, evidence[0]),
                Contains(semanticSurface.Id, semanticId, evidence[0])
            ])
            .ToArray();
        return Rehash(graph, nodes, edges);
    }

    private static UiKnowledgeGraph ConfirmMatchedControl(
        UiKnowledgeGraph graph,
        GraphNode semantic,
        ManualControlAnnotation annotation)
    {
        var related = new HashSet<string>(StringComparer.Ordinal) { semantic.Id };
        var rawId = Property(semantic, "sourceRawControlId");
        if (!string.IsNullOrWhiteSpace(rawId)) related.Add(rawId);
        var nodes = graph.Nodes.Select(node => related.Contains(node.Id)
            ? node with
            {
                Label = annotation.Label,
                Properties = UpsertProperties(node.Properties,
                [
                    new("name", annotation.Label, true),
                    new("verificationStatus", "Confirmed"),
                    new("confirmationSource", "User"),
                    new("geometrySource", "UserDrawn"),
                    new("affordance", "Invoke"),
                    new("interactionMethod", "VisualCoordinate"),
                    new("actionVerificationStatus", annotation.ActionVerificationStatus),
                    new("manualAnnotationId", annotation.Id),
                    new("safeForAutoExplore", Bool(annotation.ActionVerificationStatus == "Observed"))
                ])
            }
            : node).ToArray();
        return Rehash(graph, nodes, graph.Edges);
    }

    private static IReadOnlyList<GraphProperty> ManualProperties(
        string layer,
        ManualControlAnnotation annotation,
        string rawSurfaceId,
        string? semanticSurfaceId,
        string rawId)
    {
        var values = new List<GraphProperty>
        {
            new("layer", layer),
            new("name", annotation.Label, true),
            new("controlType", "Button"),
            new("className", "UiAtlas.UserDrawnControl"),
            new("frameworkId", "UiAtlas.UserAnnotation"),
            new("role", "button"),
            new("verificationStatus", "Confirmed"),
            new("confirmationSource", "User"),
            new("geometrySource", "UserDrawn"),
            new("affordance", "Invoke"),
            new("interactionMethod", "VisualCoordinate"),
            new("actionVerificationStatus", annotation.ActionVerificationStatus),
            new("safeForAutoExplore", Bool(annotation.ActionVerificationStatus == "Observed")),
            new("effectivelyVisible", "True"),
            new("offscreen", "False"),
            new("enabled", "Unknown"),
            new("manualAnnotationId", annotation.Id),
            new("annotationScope", "SemanticSurface"),
            new("controlPath", $"user-annotation/{annotation.Id}"),
            new("stableSelector", $"user-annotation:{annotation.Id}")
        };
        if (layer == "raw-world") values.Add(new("rawSurfaceId", rawSurfaceId));
        else
        {
            values.Add(new("semanticSurfaceId", semanticSurfaceId!));
            values.Add(new("sourceRawControlId", rawId));
            values.Add(new("semanticControlKind", "Button"));
        }
        return values;
    }

    private static IReadOnlyList<EvidenceRef> ProjectEvidence(
        IReadOnlyList<EvidenceRef> source,
        NormalizedControlBounds normalized) => source
        .Where(item => item.Bounds is { Width: > 0, Height: > 0 })
        .GroupBy(item => (item.BundleId, item.FrameSequence))
        .Select(group => group.First())
        .Select(item =>
        {
            var surface = item.Bounds!;
            var bounds = ProjectBounds(normalized, surface);
            return item with { Bounds = bounds };
        })
        .ToArray();

    private static IReadOnlyList<GraphProperty> UpsertProperties(
        IReadOnlyList<GraphProperty> source,
        IReadOnlyList<GraphProperty> replacements)
    {
        var names = replacements.Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
        return source.Where(property => !names.Contains(property.Name)).Concat(replacements).ToArray();
    }

    private static IReadOnlyList<GraphEdge> SuccessfulUserClicks(
        UiKnowledgeGraph graph,
        GraphNode rawSurface,
        GraphNode semanticSurface,
        IReadOnlyList<EvidenceRef> projectedEvidence)
    {
        var successfulEdges = graph.Edges
            .Where(edge => edge.Kind == "interaction" &&
                           Property(edge, "outcome") == "Succeeded" &&
                           Property(edge, "actor") == "User" &&
                           Property(edge, "gesture") == "Click")
            .ToArray();
        if (successfulEdges.Length == 0) return [];
        var controls = graph.Nodes.Where(node => node.Kind == GraphNodeKind.Control)
            .ToDictionary(node => node.Id, StringComparer.Ordinal);
        return successfulEdges.Where(edge =>
        {
            var sourceId = Property(edge, "sourceControlId");
            if (sourceId is null || !controls.TryGetValue(sourceId, out var node)) return false;
            if (Property(node, "rawSurfaceId") != rawSurface.Id &&
                Property(node, "semanticSurfaceId") != semanticSurface.Id)
                return false;
            return node.Evidence.Any(source => projectedEvidence.Any(target =>
                SameFrame(source, target) && source.Bounds is { } sourceBounds && target.Bounds is { } targetBounds &&
                ContainsCenter(targetBounds, sourceBounds)));
        }).ToArray();
    }

    private static bool ContainsCenter(RectI outer, RectI inner)
    {
        var centerX = inner.X + inner.Width / 2;
        var centerY = inner.Y + inner.Height / 2;
        return centerX >= outer.X && centerX < outer.X + outer.Width &&
               centerY >= outer.Y && centerY < outer.Y + outer.Height;
    }

    private static string Bool(bool value) => value ? bool.TrueString : bool.FalseString;

    private static GraphEdge Contains(string parentId, string childId, EvidenceRef evidence) => new(
        StableIdentity.Create("edge", parentId, childId, "contains"),
        "contains",
        parentId,
        childId,
        [],
        [evidence]);

    private static UiKnowledgeGraph Rehash(
        UiKnowledgeGraph graph,
        IReadOnlyList<GraphNode> nodes,
        IReadOnlyList<GraphEdge> edges)
    {
        var orderedNodes = nodes.OrderBy(node => node.Id, StringComparer.Ordinal).ToArray();
        var orderedEdges = edges.OrderBy(edge => edge.Id, StringComparer.Ordinal).ToArray();
        return graph with
        {
            Nodes = orderedNodes,
            Edges = orderedEdges,
            Metadata = graph.Metadata with { SemanticHash = GraphSemantics.ComputeHash(orderedNodes, orderedEdges) }
        };
    }

    private static bool IsButton(GraphNode node) =>
        (Property(node, "controlType") ?? Property(node, "semanticControlKind") ?? string.Empty)
        .Contains("Button", StringComparison.OrdinalIgnoreCase);

    private static bool LabelsMatch(string first, string second) =>
        StableIdentity.Normalize(first) == StableIdentity.Normalize(second);

    private static bool SameFrame(EvidenceRef first, EvidenceRef second) =>
        first.FrameSequence == second.FrameSequence && first.BundleId == second.BundleId;

    private static double IntersectionOverUnion(RectI first, RectI second)
    {
        var width = Math.Max(0, Math.Min(first.X + first.Width, second.X + second.Width) - Math.Max(first.X, second.X));
        var height = Math.Max(0, Math.Min(first.Y + first.Height, second.Y + second.Height) - Math.Max(first.Y, second.Y));
        var intersection = (long)width * height;
        var union = Math.Max(1L, (long)first.Width * first.Height + (long)second.Width * second.Height - intersection);
        return intersection / (double)union;
    }

    private static string? Property(GraphNode node, string name) =>
        node.Properties.FirstOrDefault(property => property.Name == name)?.Value;

    private static string? Property(GraphEdge edge, string name) =>
        edge.Properties.FirstOrDefault(property => property.Name == name)?.Value;

    private static string? Layer(GraphNode node) => Property(node, "layer");

    private static void Validate(MapCurationDocument document, string logicalMapId)
    {
        if (document.FormatVersion != "map-curation/1" ||
            string.IsNullOrWhiteSpace(document.LogicalMapId) ||
            !string.Equals(document.LogicalMapId, logicalMapId, StringComparison.Ordinal) ||
            document.ManualControls is null || document.ControlRules is null ||
            document.ManualControls.Count > MaximumManualControls || document.ControlRules.Count > MaximumRules)
            throw new InvalidDataException("Map curation data is invalid or belongs to another map.");
        foreach (var item in document.ManualControls)
        {
            if (item is null || string.IsNullOrWhiteSpace(item.Id) || item.Id.Length > 128 ||
                string.IsNullOrWhiteSpace(item.SurfaceStableKey) || item.SurfaceStableKey.Length > 256 ||
                string.IsNullOrWhiteSpace(item.Label) || item.Label.Length > 512 ||
                item.ControlKind != "Button" || !Valid(item.Bounds))
                throw new InvalidDataException("A manual control annotation is invalid.");
        }
        if (document.ControlRules.Any(rule => rule is null || string.IsNullOrWhiteSpace(rule.StableKey) ||
                                              rule.StableKey.Length > 256 || rule.Action is not ("Confirm" or "Suppress")))
            throw new InvalidDataException("A map curation rule is invalid.");
    }

    private static bool Valid(NormalizedControlBounds bounds) =>
        double.IsFinite(bounds.X) && double.IsFinite(bounds.Y) &&
        double.IsFinite(bounds.Width) && double.IsFinite(bounds.Height) &&
        bounds.X is >= 0 and <= 1 && bounds.Y is >= 0 and <= 1 &&
        bounds.Width is > 0 and <= 1 && bounds.Height is > 0 and <= 1 &&
        bounds.X + bounds.Width <= 1.000001 && bounds.Y + bounds.Height <= 1.000001;
}
