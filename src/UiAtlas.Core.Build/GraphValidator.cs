using UiAtlas.Core.Contracts;

namespace UiAtlas.Core.Build;

public static class GraphValidator
{
    public static ValidationReport Validate(UiKnowledgeGraph graph)
    {
        var issues = new List<ValidationIssue>();
        if (graph.Metadata is null || graph.Nodes is null || graph.Edges is null)
            return new(false, [new("graph.required", "error", "$", "Required graph member is null.")]);
        if (graph.Metadata.FormatVersion != FormatVersions.Graph)
            issues.Add(new("graph.version", "error", "metadata.formatVersion", "Unsupported graph version."));
        if (!ValidString(graph.Metadata.ToolVersion, 128) || !ValidString(graph.Metadata.GraphId, 128) ||
            !ValidString(graph.Metadata.SourceBundleId, 256) ||
            graph.Metadata.EffectiveSourceBundleIds.Count == 0 ||
            graph.Metadata.EffectiveSourceBundleIds.Any(bundleId => !ValidString(bundleId, 256)) ||
            !ValidString(graph.Metadata.EffectiveLogicalMapId, 256) ||
            !ValidString(graph.Metadata.PrivacyProfile, 128))
            issues.Add(new("graph.metadata", "error", "metadata", "Graph metadata string is null or exceeds limits."));
        else if (graph.Metadata.PrivacyProfile is not (FormatVersions.SafeExportProfile or FormatVersions.FullEvidenceProfile))
            issues.Add(new("graph.metadata", "error", "metadata.privacyProfile", "Graph privacy profile is unsupported."));
        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (graph.Nodes.Count > 100_000 || graph.Edges.Count > 100_000)
            issues.Add(new("graph.count-limit", "error", "$", "Graph exceeds node or edge limit."));
        foreach (var node in graph.Nodes)
        {
            if (node is null) { issues.Add(new("graph.required", "error", "nodes", "Node is null.")); continue; }
            if (string.IsNullOrEmpty(node.Id) || node.Id.Length > 128 || node.Label is null || node.Label.Length > 4_096 ||
                node.StableKey is null || node.StableKey.Length > 256 || node.ParentId is null)
            { issues.Add(new("graph.required", "error", "nodes", "Node string field is null or invalid.")); continue; }
            if (node.Id.Length is < 1 or > 128 || node.Label.Length > 4_096 || node.StableKey.Length > 256)
                issues.Add(new("graph.length", "error", node.Id, "Node field exceeds length limits."));
            if (node.Properties is null || node.Evidence is null)
            { issues.Add(new("graph.required", "error", node.Id, "Node collections are null.")); continue; }
            if (node.Properties.Count > 1_000 || node.Evidence.Count > 10_000)
                issues.Add(new("graph.count-limit", "error", node.Id, "Node collection exceeds limit."));
            ValidateProperties(node.Properties, node.Id, issues);
            ValidateEvidence(node.Evidence, node.Id, graph.Metadata, issues);
            if (!ids.Add(node.Id)) issues.Add(new("graph.duplicate-id", "error", node.Id, "Duplicate node identifier."));
            if (node.Kind != GraphNodeKind.Application && string.IsNullOrWhiteSpace(node.ParentId))
                issues.Add(new("graph.parent", "error", node.Id, "Non-application node has no parent."));
            if (node.Kind is GraphNodeKind.State or GraphNodeKind.Control or GraphNodeKind.Window && node.Evidence.Count == 0)
                issues.Add(new("graph.evidence", "error", node.Id, "Observed node has no evidence."));
        }
        var validNodes = graph.Nodes.Where(x => x is not null && !string.IsNullOrEmpty(x.Id) && x.Id.Length <= 128).ToArray();
        var nodeIds = validNodes.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var node in validNodes.Where(x => !string.IsNullOrEmpty(x.ParentId)))
            if (!nodeIds.Contains(node.ParentId)) issues.Add(new("graph.parent-missing", "error", node.Id, "Parent does not exist."));
        ValidateHierarchy(validNodes, issues);
        if (validNodes.All(node => node.Properties is not null && node.Properties.All(property => property is not null) &&
                                   node.Evidence is not null && node.Evidence.All(evidence => evidence is not null) && node.ParentId is not null) &&
            graph.Edges.All(edge => edge is not null && !string.IsNullOrEmpty(edge.Id) && !string.IsNullOrEmpty(edge.Kind) &&
                                    !string.IsNullOrEmpty(edge.FromId) && !string.IsNullOrEmpty(edge.ToId)))
            ValidateLayers(validNodes, graph.Edges, graph.Metadata, issues);
        foreach (var edge in graph.Edges)
        {
            if (edge is null) { issues.Add(new("graph.required", "error", "edges", "Edge is null.")); continue; }
            if (string.IsNullOrEmpty(edge.Id) || edge.Id.Length > 128 || string.IsNullOrEmpty(edge.Kind) || edge.Kind.Length > 128 ||
                string.IsNullOrEmpty(edge.FromId) || string.IsNullOrEmpty(edge.ToId) || edge.Properties is null || edge.Evidence is null)
            { issues.Add(new("graph.required", "error", edge.Id, "Edge fields are invalid.")); continue; }
            if (edge.Kind is not ("contains" or "observed-transition" or "opens-popup" or "interaction" or
                "predicts-transition" or "confirmed-as" or "contradicted-by"))
                issues.Add(new("graph.edge-kind", "error", edge.Id, "Edge kind is unsupported."));
            if (!ids.Add(edge.Id)) issues.Add(new("graph.duplicate-id", "error", edge.Id, "Duplicate edge identifier."));
            if (!nodeIds.Contains(edge.FromId) || !nodeIds.Contains(edge.ToId))
                issues.Add(new("graph.edge-endpoint", "error", edge.Id, "Edge endpoint does not exist."));
            if (edge.Properties.Count > 1_000 || edge.Evidence.Count > 10_000)
                issues.Add(new("graph.count-limit", "error", edge.Id, "Edge collection exceeds limit."));
            ValidateProperties(edge.Properties, edge.Id, issues);
            ValidateEvidence(edge.Evidence, edge.Id, graph.Metadata, issues);
        }
        if (graph.Metadata.PrivacyProfile == FormatVersions.SafeExportProfile)
            ValidateSafeProfile(graph, issues);
        if (string.IsNullOrEmpty(graph.Metadata.SemanticHash) || graph.Metadata.SemanticHash.Length != 64 || !graph.Metadata.SemanticHash.All(Uri.IsHexDigit))
            issues.Add(new("graph.semantic-hash", "error", "metadata.semanticHash", "Semantic hash is invalid."));
        else if (!issues.Any(x => x.Code == "graph.required") &&
                 !string.Equals(graph.Metadata.SemanticHash, GraphSemantics.ComputeHash(graph.Nodes, graph.Edges), StringComparison.Ordinal))
            issues.Add(new("graph.semantic-hash", "error", "metadata.semanticHash", "Semantic hash does not match graph content."));
        return new(!issues.Any(x => x.Severity == "error"), issues);
    }

    private static void ValidateProperties(IReadOnlyList<GraphProperty> properties, string owner, List<ValidationIssue> issues)
    {
        foreach (var property in properties)
            if (property is null || !ValidString(property.Name, 128) || property.Value is null || property.Value.Length > 4_096)
                issues.Add(new("graph.property", "error", owner, "Graph property is null or exceeds limits."));
    }

    private static void ValidateEvidence(IReadOnlyList<EvidenceRef> evidence, string owner, GraphMetadata metadata, List<ValidationIssue> issues)
    {
        foreach (var item in evidence)
        {
            if (item is null) { issues.Add(new("graph.evidence", "error", owner, "Evidence reference is null or invalid.")); continue; }
            var valid = metadata.PrivacyProfile switch
            {
                FormatVersions.SafeExportProfile => item.BundleId == "[redacted]" && item.FrameSequence == 0 &&
                    item.ObservationEntry == "[redacted]" && item.ScreenshotEntry is null,
                FormatVersions.FullEvidenceProfile => item.FrameSequence > 0 &&
                    metadata.EffectiveSourceBundleIds.Contains(item.BundleId, StringComparer.Ordinal) &&
                    string.Equals(item.ObservationEntry, $"raw/observations/frame-{item.FrameSequence:D6}.json", StringComparison.Ordinal) &&
                    (item.ScreenshotEntry is null || string.Equals(item.ScreenshotEntry, $"raw/frames/frame-{item.FrameSequence:D6}.png", StringComparison.Ordinal)),
                _ => false
            };
            if (!valid)
                issues.Add(new("graph.evidence", "error", owner, "Evidence reference is null or invalid."));
        }
    }

    private static bool ValidString(string? value, int max) => !string.IsNullOrEmpty(value) && value.Length <= max;
    private static void ValidateLayers(IReadOnlyList<GraphNode> nodes, IReadOnlyList<GraphEdge> edges, GraphMetadata metadata, List<ValidationIssue> issues)
    {
        if (metadata.PrivacyProfile == FormatVersions.SafeExportProfile) return;
        var layersByNode = nodes.GroupBy(node => node.Id, StringComparer.Ordinal).ToDictionary(group => group.Key,
            group => group.SelectMany(node => (node.Properties ?? []).Where(property => property is not null && property.Name == "layer"))
                .Select(property => property.Value).Distinct(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
        if (layersByNode.Count > 0 && layersByNode.Values.All(layers => layers.Length == 1 && layers[0] == "legacy-observed")) return;

        foreach (var node in nodes)
        {
            var layers = layersByNode[node.Id];
            if (layers.Length != 1 || layers[0] is not ("shared" or "raw-data-streams" or "raw-world" or "semantic-world" or "prediction"))
            {
                issues.Add(new("graph.layer", "error", node.Id, "Node must declare exactly one supported world layer."));
                continue;
            }
            if (node.Kind == GraphNodeKind.Application && layers[0] != "shared")
                issues.Add(new("graph.layer", "error", node.Id, "Application must be in the shared layer."));
            if (node.Kind != GraphNodeKind.Application && layers[0] == "shared")
                issues.Add(new("graph.layer", "error", node.Id, "Only application nodes may be shared."));
        }

        var rawStreamNodes = nodes.Where(node => layersByNode[node.Id].Contains("raw-data-streams", StringComparer.Ordinal))
            .GroupBy(node => node.Id, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        var rawNodes = nodes.Where(node => layersByNode[node.Id].Contains("raw-world", StringComparer.Ordinal))
            .GroupBy(node => node.Id, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var byId = nodes.GroupBy(node => node.Id, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var contains = edges.Where(edge => edge is not null && edge.Kind == "contains")
            .Select(edge => (edge.FromId, edge.ToId)).ToHashSet();
        foreach (var node in nodes.Where(node => node.Kind != GraphNodeKind.Application))
            if (!contains.Contains((node.ParentId, node.Id)))
                issues.Add(new("graph.containment", "error", node.Id, "Hierarchy parent is not represented by containment evidence."));
        foreach (var edge in edges.Where(edge => edge is not null && edge.Kind == "contains"))
            if (!byId.TryGetValue(edge.ToId, out var child) ||
                child.ParentId != edge.FromId &&
                !(rawNodes.TryGetValue(edge.FromId, out var rawState) && rawState.Kind == GraphNodeKind.State &&
                  rawNodes.TryGetValue(edge.ToId, out var rawControl) && rawControl.Kind == GraphNodeKind.Control &&
                  rawControl.Properties.FirstOrDefault(property => property.Name == "rawSurfaceId")?.Value == rawState.ParentId) &&
                !(rawStreamNodes.TryGetValue(edge.ToId, out var streamChild) && streamChild.ParentId == edge.FromId))
                issues.Add(new("graph.containment", "error", edge.Id, "Containment edge does not match the declared hierarchy."));
        foreach (var edge in edges.Where(edge => edge is not null && edge.Kind == "observed-transition"))
            if (!rawNodes.TryGetValue(edge.FromId, out var from) || !rawNodes.TryGetValue(edge.ToId, out var to) ||
                from.Kind != GraphNodeKind.State || to.Kind != GraphNodeKind.State || from.ParentId != to.ParentId)
                issues.Add(new("graph.transition", "error", edge.Id, "Observed transition must connect Raw World states on one surface."));
        foreach (var edge in edges.Where(edge => edge is not null && edge.Kind == "opens-popup"))
        {
            if (!byId.TryGetValue(edge.FromId, out var from) || !byId.TryGetValue(edge.ToId, out var to) ||
                from.Kind != GraphNodeKind.Control || to.Kind != GraphNodeKind.Surface ||
                LayerOf(layersByNode, from.Id) != LayerOf(layersByNode, to.Id) ||
                !(to.Properties.Any(property => property.Name == "surfaceClass" && property.Value == "RawPopupWindow") ||
                  to.Properties.Any(property => property.Name == "semanticSurfaceKind" && property.Value == "PopupVariant")))
                issues.Add(new("graph.popup-relationship", "error", edge.Id,
                    "Popup relationship must connect a control to a popup surface in the same world layer."));
        }
        foreach (var edge in edges.Where(edge => edge is not null && edge.Kind == "interaction"))
        {
            var sourceControlIds = edge.Properties.Where(property => property.Name == "sourceControlId")
                .Select(property => property.Value).ToArray();
            var sessionIds = edge.Properties.Where(property => property.Name == "sessionId")
                .Select(property => property.Value).ToArray();
            var outcomeText = edge.Properties.FirstOrDefault(property => property.Name == "outcome")?.Value;
            var sourceFrameText = edge.Properties.FirstOrDefault(property => property.Name == "sourceFrameSequence")?.Value;
            var sourceControlEvidenceFrameText = edge.Properties
                .FirstOrDefault(property => property.Name == "sourceControlEvidenceFrameSequence")?.Value;
            var startedText = edge.Properties.FirstOrDefault(property => property.Name == "startedUtc")?.Value;
            var completedText = edge.Properties.FirstOrDefault(property => property.Name == "completedUtc")?.Value;
            var resultFrames = edge.Properties.Where(property => property.Name == "resultFrameSequence")
                .Select(property => long.TryParse(property.Value, out var parsed) ? parsed : 0).ToArray();
            var sourceControlEvidenceFrame = long.TryParse(sourceControlEvidenceFrameText, out var parsedSourceControlFrame)
                ? parsedSourceControlFrame
                : long.TryParse(sourceFrameText, out var parsedSourceFrame) ? parsedSourceFrame : 0;
            var pointerObservedSource = sourceControlIds.Length == 1 &&
                rawNodes.TryGetValue(sourceControlIds[0], out var candidateSourceControl) &&
                candidateSourceControl.Properties.Any(property => property.Name == "controlType" && property.Value == "CanvasItem") &&
                candidateSourceControl.Properties.Any(property => property.Name == "frameworkId" && property.Value == "UiAtlas.Pointer");
            if (!rawNodes.TryGetValue(edge.FromId, out var from) || !rawNodes.TryGetValue(edge.ToId, out var to) ||
                from.Kind != GraphNodeKind.State || to.Kind != GraphNodeKind.State ||
                sourceControlIds.Length != 1 || !rawNodes.TryGetValue(sourceControlIds.FirstOrDefault() ?? string.Empty, out var sourceControl) ||
                sourceControl.Kind != GraphNodeKind.Control || LayerOf(layersByNode, sourceControl.Id) != "raw-world" ||
                sessionIds.Length != 1 || !metadata.EffectiveSourceBundleIds.Contains(sessionIds[0], StringComparer.Ordinal) ||
                !long.TryParse(sourceFrameText, out var sourceFrame) || sourceFrame < 1 ||
                sourceControlEvidenceFrame < sourceFrame ||
                sourceControlEvidenceFrame != sourceFrame &&
                    (!pointerObservedSource || resultFrames.Length == 0 || sourceControlEvidenceFrame > resultFrames.Max()) ||
                !DateTimeOffset.TryParse(startedText, out var started) ||
                !DateTimeOffset.TryParse(completedText, out var completed) || completed < started ||
                !Enum.TryParse<InteractionOutcome>(outcomeText, out var outcome) ||
                !Enum.TryParse<InteractionActor>(edge.Properties.FirstOrDefault(property => property.Name == "actor")?.Value, out _) ||
                !Enum.TryParse<InteractionGestureKind>(edge.Properties.FirstOrDefault(property => property.Name == "gesture")?.Value, out _) ||
                !Enum.TryParse<InteractionActionKind>(edge.Properties.FirstOrDefault(property => property.Name == "action")?.Value, out _) ||
                outcome == InteractionOutcome.Succeeded && (resultFrames.Length == 0 || resultFrames.Any(frame => frame < 1)) ||
                outcome != InteractionOutcome.Succeeded && edge.FromId != edge.ToId ||
                edge.Evidence.Any(evidence => evidence.BundleId != sessionIds[0]) ||
                !from.Evidence.Any(evidence => evidence.BundleId == sessionIds[0] && evidence.FrameSequence == sourceFrame) ||
                !sourceControl.Evidence.Any(evidence => evidence.BundleId == sessionIds[0] &&
                    evidence.FrameSequence == sourceControlEvidenceFrame) ||
                outcome == InteractionOutcome.Succeeded && !to.Evidence.Any(evidence =>
                    evidence.BundleId == sessionIds[0] && resultFrames.Contains(evidence.FrameSequence)))
                issues.Add(new("graph.interaction", "error", edge.Id,
                    "Interaction must connect Raw World states and carry complete same-session causal evidence."));
        }

        var predictionNodes = nodes.Where(node => layersByNode[node.Id].Contains("prediction", StringComparer.Ordinal))
            .ToDictionary(node => node.Id, StringComparer.Ordinal);
        foreach (var prediction in predictionNodes.Values)
        {
            if (prediction.Kind != GraphNodeKind.State || !byId.TryGetValue(prediction.ParentId, out var parent) ||
                !(parent.Kind == GraphNodeKind.State && LayerOf(layersByNode, parent.Id) is "raw-world" or "prediction"))
                issues.Add(new("graph.prediction", "error", prediction.Id,
                    "Prediction must be a state below an observed or predicted state."));
            if (!prediction.Properties.Any(property => property.Name == "predictionStatus" &&
                    property.Value is "Predicted" or "Matched" or "Rejected" or "Stale") ||
                !prediction.Properties.Any(property => property.Name == "actionFingerprint"))
                issues.Add(new("graph.prediction", "error", prediction.Id,
                    "Prediction state is missing its status or action fingerprint."));
        }
        foreach (var edge in edges.Where(edge => edge is not null && edge.Kind == "predicts-transition"))
            if (!predictionNodes.ContainsKey(edge.ToId) ||
                !byId.TryGetValue(edge.FromId, out var predictionSource) ||
                predictionSource.Kind != GraphNodeKind.State ||
                LayerOf(layersByNode, predictionSource.Id) is not ("raw-world" or "prediction"))
                issues.Add(new("graph.prediction-transition", "error", edge.Id,
                    "Predicted transition must lead from an observed or predicted state to a prediction."));
        foreach (var edge in edges.Where(edge => edge is not null && edge.Kind is "confirmed-as" or "contradicted-by"))
            if (!predictionNodes.ContainsKey(edge.FromId) || !rawNodes.TryGetValue(edge.ToId, out var observedResult) ||
                observedResult.Kind != GraphNodeKind.State)
                issues.Add(new("graph.prediction-outcome", "error", edge.Id,
                    "Prediction outcome must connect a prediction to an observed state."));

        var ownerByRawSurface = rawNodes.Values.Where(node => node.Kind == GraphNodeKind.Surface)
            .ToDictionary(node => node.Id, node => node.Properties.FirstOrDefault(property => property.Name == "ownerRawSurfaceId")?.Value, StringComparer.Ordinal);
        foreach (var raw in rawNodes.Values)
        {
            if (raw.Kind is not (GraphNodeKind.Surface or GraphNodeKind.State or GraphNodeKind.Control))
                issues.Add(new("graph.layer-kind", "error", raw.Id, "Raw World contains an unsupported node kind."));
            if (!byId.TryGetValue(raw.ParentId, out var parent)) { continue; }
            var parentLayer = LayerOf(layersByNode, parent.Id);
            if (raw.Kind == GraphNodeKind.Surface && (parent.Kind != GraphNodeKind.Application || parentLayer != "shared"))
                issues.Add(new("graph.raw-parent", "error", raw.Id, "Raw surface parent is invalid."));
            if (raw.Kind == GraphNodeKind.State && (parent.Kind != GraphNodeKind.Surface || parentLayer != "raw-world"))
                issues.Add(new("graph.raw-parent", "error", raw.Id, "Raw state parent is invalid."));
            if (raw.Kind == GraphNodeKind.Control)
            {
                var rawSurfaceId = raw.Properties.FirstOrDefault(property => property.Name == "rawSurfaceId")?.Value;
                if (rawSurfaceId is null || !rawNodes.TryGetValue(rawSurfaceId, out var rawSurface) || rawSurface.Kind != GraphNodeKind.Surface)
                    issues.Add(new("graph.raw-surface", "error", raw.Id, "Raw control surface is missing."));
                if (parentLayer != "raw-world" || parent.Kind is not (GraphNodeKind.Surface or GraphNodeKind.Control) ||
                    parent.Kind == GraphNodeKind.Control && parent.Properties.FirstOrDefault(property => property.Name == "rawSurfaceId")?.Value != rawSurfaceId ||
                    parent.Kind == GraphNodeKind.Surface && parent.Id != rawSurfaceId)
                    issues.Add(new("graph.raw-parent", "error", raw.Id, "Raw control parent crosses a surface boundary."));
            }
            var owner = raw.Properties.FirstOrDefault(property => property.Name == "ownerRawSurfaceId")?.Value;
            if (owner is not null && (owner == raw.Id || !rawNodes.TryGetValue(owner, out var ownerNode) || ownerNode.Kind != GraphNodeKind.Surface))
                issues.Add(new("graph.owner-lineage", "error", raw.Id, "Raw surface owner is missing."));
        }
        ValidateOwnerTopology(ownerByRawSurface, issues);
        foreach (var stream in rawStreamNodes.Values)
        {
            if (stream.Kind is not (GraphNodeKind.Window or GraphNodeKind.Control))
            {
                issues.Add(new("graph.layer-kind", "error", stream.Id, "Raw Data Streams contains an unsupported node kind."));
                continue;
            }
            if (!byId.TryGetValue(stream.ParentId, out var parent)) continue;
            var parentLayer = LayerOf(layersByNode, parent.Id);
            if (stream.Kind == GraphNodeKind.Window && (parent.Kind != GraphNodeKind.Application || parentLayer != "shared"))
                issues.Add(new("graph.rds-parent", "error", stream.Id, "Raw Data Streams surface parent is invalid."));
            if (stream.Kind == GraphNodeKind.Control &&
                (parentLayer != "raw-data-streams" || parent.Kind is not (GraphNodeKind.Window or GraphNodeKind.Control)))
                issues.Add(new("graph.rds-parent", "error", stream.Id, "Raw Data Streams control parent is invalid."));
        }
        foreach (var semantic in nodes.Where(node => layersByNode[node.Id].Contains("semantic-world", StringComparer.Ordinal)))
        {
            var propertyName = semantic.Kind switch
            {
                GraphNodeKind.Surface => "sourceRawSurfaceId",
                GraphNodeKind.Control => "sourceRawControlId",
                _ => string.Empty
            };
            if (propertyName.Length == 0)
            {
                issues.Add(new("graph.layer-kind", "error", semantic.Id, "Semantic World contains an unsupported node kind."));
                continue;
            }
            var sourceIds = semantic.Properties.Where(property => property.Name == propertyName)
                .Select(property => property.Value).Distinct(StringComparer.Ordinal).ToArray();
            if (sourceIds.Length == 0)
            {
                issues.Add(new("graph.lineage", "error", semantic.Id, "Semantic entity has no Raw World lineage."));
                continue;
            }
            var expectedKind = semantic.Kind;
            foreach (var sourceId in sourceIds)
                if (!rawNodes.TryGetValue(sourceId, out var source) || source.Kind != expectedKind)
                    issues.Add(new("graph.lineage", "error", semantic.Id, "Semantic entity references a missing or incompatible Raw World entity."));

            if (!byId.TryGetValue(semantic.ParentId, out var parent)) continue;
            var parentLayer = LayerOf(layersByNode, parent.Id);
            if (semantic.Kind == GraphNodeKind.Surface)
            {
                var kind = semantic.Properties.FirstOrDefault(property => property.Name == "semanticSurfaceKind")?.Value;
                if (kind is not ("Window" or "PopupFamily" or "PopupVariant"))
                    issues.Add(new("graph.semantic-kind", "error", semantic.Id, "Semantic surface kind is unsupported."));
                if (kind != "PopupFamily")
                {
                    var expectedOwners = sourceIds.Where(rawNodes.ContainsKey)
                        .Select(sourceId => rawNodes[sourceId].Properties.FirstOrDefault(property => property.Name == "ownerRawSurfaceId")?.Value)
                        .Distinct(StringComparer.Ordinal).ToArray();
                    var declaredOwner = semantic.Properties.FirstOrDefault(property => property.Name == "sourceOwnerRawSurfaceId")?.Value;
                    if (expectedOwners.Length != 1 || expectedOwners[0] != declaredOwner)
                        issues.Add(new("graph.owner-lineage", "error", semantic.Id, "Semantic owner lineage does not match its Raw World source."));
                }
                if (kind == "PopupVariant")
                {
                    if (parent.Kind != GraphNodeKind.Surface || parentLayer != "semantic-world" ||
                        parent.Properties.FirstOrDefault(property => property.Name == "semanticSurfaceKind")?.Value != "PopupFamily" ||
                        semantic.Properties.FirstOrDefault(property => property.Name == "semanticPopupFamilyId")?.Value != parent.Id)
                        issues.Add(new("graph.popup-lineage", "error", semantic.Id, "Popup variant family is inconsistent."));
                }
                else if (parent.Kind != GraphNodeKind.Application || parentLayer != "shared")
                    issues.Add(new("graph.semantic-parent", "error", semantic.Id, "Semantic surface parent is invalid."));
                var owner = semantic.Properties.FirstOrDefault(property => property.Name == "sourceOwnerRawSurfaceId")?.Value;
                if (owner is not null && (!rawNodes.TryGetValue(owner, out var ownerNode) || ownerNode.Kind != GraphNodeKind.Surface))
                    issues.Add(new("graph.owner-lineage", "error", semantic.Id, "Semantic owner lineage is invalid."));
            }
            else if (semantic.Kind == GraphNodeKind.Control)
            {
                var semanticSurfaceId = semantic.Properties.FirstOrDefault(property => property.Name == "semanticSurfaceId")?.Value;
                GraphNode? semanticSurface = null;
                if (semanticSurfaceId is null || !byId.TryGetValue(semanticSurfaceId, out semanticSurface) || semanticSurface.Kind != GraphNodeKind.Surface ||
                    LayerOf(layersByNode, semanticSurface.Id) != "semantic-world")
                    issues.Add(new("graph.semantic-surface", "error", semantic.Id, "Semantic control surface is missing."));
                if (parentLayer != "semantic-world" || parent.Kind is not (GraphNodeKind.Surface or GraphNodeKind.Control) ||
                    parent.Kind == GraphNodeKind.Control && parent.Properties.FirstOrDefault(property => property.Name == "semanticSurfaceId")?.Value != semanticSurfaceId ||
                    parent.Kind == GraphNodeKind.Surface && parent.Id != semanticSurfaceId)
                    issues.Add(new("graph.semantic-parent", "error", semantic.Id, "Semantic control parent crosses a surface boundary."));
                foreach (var sourceId in sourceIds)
                    if (rawNodes.TryGetValue(sourceId, out var rawControl) &&
                        rawControl.Properties.FirstOrDefault(property => property.Name == "rawSurfaceId")?.Value is { } sourceSurface &&
                        (semanticSurface is null || !semanticSurface.Properties.Where(property => property.Name == "sourceRawSurfaceId").Any(property => property.Value == sourceSurface)))
                        issues.Add(new("graph.lineage-surface", "error", semantic.Id, "Semantic control lineage crosses a surface boundary."));
            }
        }
    }

    private static string? LayerOf(IReadOnlyDictionary<string, string[]> layersByNode, string id) =>
        layersByNode.TryGetValue(id, out var layers) && layers.Length == 1 ? layers[0] : null;

    private static void ValidateOwnerTopology(IReadOnlyDictionary<string, string?> owners, List<ValidationIssue> issues)
    {
        foreach (var id in owners.Keys)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal) { id };
            var current = id;
            for (var depth = 0; depth <= 32 && owners.TryGetValue(current, out var owner) && owner is not null; depth++)
            {
                if (!seen.Add(owner)) { issues.Add(new("graph.owner-cycle", "error", id, "Raw surface ownership contains a cycle.")); break; }
                current = owner;
                if (depth == 32) issues.Add(new("graph.owner-depth", "error", id, "Raw surface ownership exceeds the depth limit."));
            }
        }
    }

    private static void ValidateSafeProfile(UiKnowledgeGraph graph, List<ValidationIssue> issues)
    {
        if (graph.Metadata.BuiltUtc != DateTimeOffset.UnixEpoch ||
            graph.Metadata.SourceBundleId != "[redacted]" ||
            graph.Metadata.EffectiveLogicalMapId != "[redacted]" ||
            graph.Metadata.EffectiveSourceBundleIds.Count != 1 ||
            graph.Metadata.EffectiveSourceBundleIds[0] != "[redacted]" ||
            graph.Metadata.ToolVersion != FormatVersions.Tool ||
            !OpaqueId(graph.Metadata.GraphId, "graph"))
            issues.Add(new("graph.safe-profile", "error", "metadata", "Safe profile metadata is not fully redacted."));
        foreach (var node in graph.Nodes.Where(node => node is not null))
        {
            var expectedLabelPrefix = node.Kind + " ";
            var ordinal = node.Label is not null && node.Label.StartsWith(expectedLabelPrefix, StringComparison.Ordinal) ? node.Label[expectedLabelPrefix.Length..] : string.Empty;
            var area = node.Properties is { Count: 1 } ? node.Properties[0] : null;
            if (!OpaqueId(node.Id, "node") || node.StableKey != node.Id || area is null || area.Name != "area" || area.Sensitive ||
                area.Value is not ("app" or "raw-data-streams" or "raw-world" or "semantic-world" or "prediction") ||
                !int.TryParse(ordinal, out var parsed) || parsed < 1 || node.Evidence is null || node.Evidence.Any(evidence => evidence?.Bounds is not null))
                issues.Add(new("graph.safe-profile", "error", node.Id, "Safe profile node retains non-redacted content."));
        }
        foreach (var edge in graph.Edges.Where(edge => edge is not null))
            if (!OpaqueId(edge.Id, "edge") || edge.Kind is not ("contains" or "observed-transition" or "opens-popup" or "interaction" or
                    "predicts-transition" or "confirmed-as" or "contradicted-by") || edge.Properties is null || edge.Properties.Count != 0 || edge.Evidence is null || edge.Evidence.Any(evidence => evidence?.Bounds is not null))
                issues.Add(new("graph.safe-profile", "error", edge.Id, "Safe profile edge retains non-redacted content."));
    }

    private static bool OpaqueId(string? value, string prefix)
    {
        if (value is null || value.Length != prefix.Length + 25 || !value.StartsWith(prefix + "_", StringComparison.Ordinal)) return false;
        return value[(prefix.Length + 1)..].All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    }

    private static void ValidateHierarchy(IReadOnlyList<GraphNode> nodes, List<ValidationIssue> issues)
    {
        var byId = nodes.GroupBy(x => x.Id, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.First(), StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal) { node.Id };
            var current = node;
            var depth = 0;
            while (!string.IsNullOrEmpty(current.ParentId) && byId.TryGetValue(current.ParentId, out var parent))
            {
                current = parent;
                if (!seen.Add(current.Id)) { issues.Add(new("graph.cycle", "error", node.Id, "Hierarchy contains a cycle.")); break; }
                if (++depth > 64) { issues.Add(new("graph.depth", "error", node.Id, "Hierarchy exceeds depth limit.")); break; }
            }
        }
    }
}
