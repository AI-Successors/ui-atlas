using System.Globalization;
using UiAtlas.Core.Build;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Storage;

namespace UiAtlas.Core.Cli;

internal static class SpeculativeGraphProjector
{
    public static UiKnowledgeGraph Apply(UiKnowledgeGraph graph, SpeculativePlanningState? planning)
    {
        ArgumentNullException.ThrowIfNull(graph);
        if (planning is null || planning.Predictions.Count == 0)
            return graph;

        var predictionNodeIds = graph.Nodes.Where(node => Property(node, "layer") == "prediction")
            .Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
        var nodes = graph.Nodes.Where(node => !predictionNodeIds.Contains(node.Id)).ToList();
        var edges = graph.Edges.Where(edge => !predictionNodeIds.Contains(edge.FromId) &&
                                              !predictionNodeIds.Contains(edge.ToId)).ToList();
        var rawStates = nodes.Where(node => node.Kind == GraphNodeKind.State && Property(node, "layer") == "raw-world").ToArray();
        var created = new Dictionary<string, GraphNode>(StringComparer.Ordinal);

        foreach (var prediction in planning.Predictions.OrderBy(item => item.Depth)
                     .ThenBy(item => item.Revision).ThenBy(item => item.PredictionId, StringComparer.Ordinal))
        {
            var sourceState = FindState(rawStates, prediction.SourceSessionId, prediction.SourceFrameSequence);
            if (sourceState is null)
                continue;
            GraphNode? parentPrediction = null;
            if (prediction.ParentPredictionId is not null &&
                !created.TryGetValue(prediction.ParentPredictionId, out parentPrediction))
                continue;

            var id = StableIdentity.Create("state", "prediction", prediction.PredictionId);
            var parentId = parentPrediction?.Id ?? sourceState.Id;
            var evidence = sourceState.Evidence.Where(item =>
                    item.BundleId == prediction.SourceSessionId && item.FrameSequence == prediction.SourceFrameSequence)
                .Take(1).ToArray();
            if (evidence.Length == 0)
                continue;
            var node = new GraphNode(
                id,
                GraphNodeKind.State,
                parentId,
                prediction.PredictionId,
                prediction.DisplayName,
                [
                    new("layer", "prediction"),
                    new("predictionStatus", prediction.Status.ToString()),
                    new("confidence", prediction.Confidence.ToString("0.000", CultureInfo.InvariantCulture)),
                    new("sourceSurfaceFingerprint", prediction.SourceSurfaceFingerprint),
                    new("actionFingerprint", prediction.ActionFingerprint),
                    new("expectedOutcomeKind", prediction.ExpectedOutcomeKind),
                    new("depth", prediction.Depth.ToString(CultureInfo.InvariantCulture)),
                    new("revision", prediction.Revision.ToString(CultureInfo.InvariantCulture)),
                    new("knowledgeSource", prediction.KnowledgeSource),
                    new("controlEvidence", SpeculativeEvidenceKind.ControlObserved.ToString()),
                    new("surfaceEvidence", SpeculativeEvidenceKind.SurfaceObserved.ToString()),
                    new("transitionEvidence", prediction.Status == SpeculativePredictionStatus.Matched
                        ? SpeculativeEvidenceKind.TransitionConfirmed.ToString()
                        : "Unconfirmed"),
                    new("predictedPath", PredictionPath(prediction, planning.Predictions))
                ],
                evidence);
            nodes.Add(node);
            created[prediction.PredictionId] = node;
            edges.Add(new(
                StableIdentity.Create("edge", "prediction-contains", parentId, id),
                "contains", parentId, id, [], evidence));
            edges.Add(new(
                StableIdentity.Create("edge", "predicts-transition", parentId, id),
                "predicts-transition", parentId, id,
                [new("confidence", prediction.Confidence.ToString("0.000", CultureInfo.InvariantCulture))], evidence));

            if (prediction.Status is not (SpeculativePredictionStatus.Matched or SpeculativePredictionStatus.Rejected) ||
                prediction.ResultSessionId is null || prediction.ResultFrameSequence is null)
                continue;
            var resultState = FindState(rawStates, prediction.ResultSessionId, prediction.ResultFrameSequence.Value);
            if (resultState is null)
                continue;
            var edgeKind = prediction.Status == SpeculativePredictionStatus.Matched ? "confirmed-as" : "contradicted-by";
            edges.Add(new(
                StableIdentity.Create("edge", edgeKind, id, resultState.Id),
                edgeKind, id, resultState.Id, [], resultState.Evidence.Where(item =>
                    item.BundleId == prediction.ResultSessionId && item.FrameSequence == prediction.ResultFrameSequence).Take(1).ToArray()));
        }

        var orderedNodes = nodes.OrderBy(node => node.Id, StringComparer.Ordinal).ToArray();
        var orderedEdges = edges.GroupBy(edge => edge.Id, StringComparer.Ordinal).Select(group => group.First())
            .OrderBy(edge => edge.Id, StringComparer.Ordinal).ToArray();
        return graph with
        {
            Metadata = graph.Metadata with { SemanticHash = GraphSemantics.ComputeHash(orderedNodes, orderedEdges) },
            Nodes = orderedNodes,
            Edges = orderedEdges
        };
    }

    private static GraphNode? FindState(IEnumerable<GraphNode> states, string sessionId, long frameSequence) => states
        .FirstOrDefault(node => node.Evidence.Any(evidence =>
            evidence.BundleId == sessionId && evidence.FrameSequence == frameSequence));

    private static string PredictionPath(SpeculativePredictionState prediction, IReadOnlyList<SpeculativePredictionState> all)
    {
        var byId = all.ToDictionary(item => item.PredictionId, StringComparer.Ordinal);
        var labels = new Stack<string>();
        var current = prediction;
        for (var depth = 0; depth < 2; depth++)
        {
            labels.Push(current.DisplayName);
            if (current.ParentPredictionId is null || !byId.TryGetValue(current.ParentPredictionId, out current))
                break;
        }
        return string.Join(" → ", labels);
    }

    private static string? Property(GraphNode node, string name) =>
        node.Properties.FirstOrDefault(property => property.Name == name)?.Value;
}
