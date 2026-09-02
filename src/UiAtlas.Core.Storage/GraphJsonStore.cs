using System.Text.Json;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Recording;

namespace UiAtlas.Core.Storage;

public static class GraphJsonStore
{
    public static void Save(UiKnowledgeGraph graph, string path)
    {
        if (!UiAtlas.Core.Build.GraphValidator.Validate(graph).IsValid)
            throw new InvalidDataException("Graph failed integrity validation.");
        var document = Organize(graph);
        AtomicFile.Publish(path, temp => File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(document, JsonDefaults.Options)));
    }

    public static UiKnowledgeGraph Load(string path)
    {
        try
        {
            using var input = new FileStream(Path.GetFullPath(path), FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length > 256L * 1024 * 1024) throw new InvalidDataException("Graph JSON is missing or exceeds size limit.");
            var bytes = new byte[checked((int)input.Length)];
            input.ReadExactly(bytes);
            StrictJsonValidator.Validate(bytes);
            using var document = JsonDocument.Parse(bytes);
            var graph = document.RootElement.TryGetProperty("formatVersion", out var formatVersion) &&
                        formatVersion.ValueKind == JsonValueKind.String &&
                        formatVersion.GetString() == FormatVersions.GraphJsonExport
                ? LoadOrganized(bytes)
                : JsonSerializer.Deserialize<UiKnowledgeGraph>(bytes, JsonDefaults.Options)
                  ?? throw new InvalidDataException("Graph JSON is empty.");
            graph = UiAtlas.Core.Build.GraphMigration.UpgradeToCurrent(graph);
            var report = UiAtlas.Core.Build.GraphValidator.Validate(graph);
            if (!report.IsValid)
            {
                var issue = report.Issues.First();
                throw new InvalidDataException($"Graph JSON failed integrity validation: {issue.Code} at {issue.Path}.");
            }
            return graph;
        }
        catch (JsonException ex) { throw new InvalidDataException("Graph JSON is malformed.", ex); }
    }

    private static OrganizedGraphJson Organize(UiKnowledgeGraph graph)
    {
        var applicationNodes = graph.Nodes.Where(node => node.Kind == GraphNodeKind.Application || Layer(node) == "shared").ToArray();
        var rawDataStreamNodes = graph.Nodes.Where(node => Layer(node) == "raw-data-streams").ToArray();
        var rawWorldNodes = graph.Nodes.Where(node => Layer(node) == "raw-world").ToArray();
        var semanticWorldNodes = graph.Nodes.Where(node => Layer(node) == "semantic-world").ToArray();
        var assigned = applicationNodes.Concat(rawDataStreamNodes).Concat(rawWorldNodes).Concat(semanticWorldNodes)
            .Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
        if (assigned.Count != graph.Nodes.Count)
            throw new InvalidDataException("Graph contains nodes outside the five export areas.");

        var areaByNode = applicationNodes.Select(node => (node.Id, Area: "app"))
            .Concat(rawDataStreamNodes.Select(node => (node.Id, Area: "raw-data-streams")))
            .Concat(rawWorldNodes.Select(node => (node.Id, Area: "raw-world")))
            .Concat(semanticWorldNodes.Select(node => (node.Id, Area: "semantic-world")))
            .ToDictionary(item => item.Id, item => item.Area, StringComparer.Ordinal);
        GraphArea Area(string name, IReadOnlyList<GraphNode> nodes) => new(nodes,
            graph.Edges.Where(edge => areaByNode.GetValueOrDefault(edge.ToId) == name).ToArray());
        var application = applicationNodes.Single(node => node.Kind == GraphNodeKind.Application);
        var processName = application.Properties.FirstOrDefault(property => property.Name == "processName")?.Value ?? application.Label;
        return new(FormatVersions.GraphJsonExport, graph.Metadata,
            graph.Nodes.Select(node => node.Id).ToArray(), graph.Edges.Select(edge => edge.Id).ToArray(),
            Area("app", applicationNodes), new(application.Id, processName),
            Area("raw-data-streams", rawDataStreamNodes), Area("raw-world", rawWorldNodes), Area("semantic-world", semanticWorldNodes));
    }

    private static UiKnowledgeGraph LoadOrganized(byte[] bytes)
    {
        var document = JsonSerializer.Deserialize<OrganizedGraphJson>(bytes, JsonDefaults.Options)
            ?? throw new InvalidDataException("Graph JSON is empty.");
        if (document.FormatVersion != FormatVersions.GraphJsonExport || document.Metadata is null ||
            document.App is null || document.Process is null || document.RawDataStreams is null ||
            document.RawWorld is null || document.SemanticWorld is null)
            throw new InvalidDataException("Organized graph JSON is incomplete.");
        var areas = new[] { document.App, document.RawDataStreams, document.RawWorld, document.SemanticWorld };
        if (areas.Any(area => area.Nodes is null || area.Edges is null))
            throw new InvalidDataException("Organized graph JSON contains a null area collection.");
        var nodeById = areas.SelectMany(area => area.Nodes).ToDictionary(node => node.Id, StringComparer.Ordinal);
        var edgeById = areas.SelectMany(area => area.Edges).ToDictionary(edge => edge.Id, StringComparer.Ordinal);
        if (document.NodeOrder is null || document.EdgeOrder is null ||
            document.NodeOrder.Count != nodeById.Count || document.EdgeOrder.Count != edgeById.Count ||
            document.NodeOrder.Distinct(StringComparer.Ordinal).Count() != nodeById.Count ||
            document.EdgeOrder.Distinct(StringComparer.Ordinal).Count() != edgeById.Count ||
            document.NodeOrder.Any(id => !nodeById.ContainsKey(id)) || document.EdgeOrder.Any(id => !edgeById.ContainsKey(id)))
            throw new InvalidDataException("Organized graph JSON order is incomplete.");
        var nodes = document.NodeOrder.Select(id => nodeById[id]).ToArray();
        var edges = document.EdgeOrder.Select(id => edgeById[id]).ToArray();
        if (nodes.Count(node => node.Kind == GraphNodeKind.Application && node.Id == document.Process.ApplicationId) != 1)
            throw new InvalidDataException("Process area does not reference the exported application.");
        return new(document.Metadata, nodes, edges);
    }

    private static string? Layer(GraphNode node) =>
        node.Properties.FirstOrDefault(property => property.Name is "layer" or "area")?.Value;

    private sealed record OrganizedGraphJson(
        string FormatVersion,
        GraphMetadata Metadata,
        IReadOnlyList<string> NodeOrder,
        IReadOnlyList<string> EdgeOrder,
        GraphArea App,
        ProcessArea Process,
        GraphArea RawDataStreams,
        GraphArea RawWorld,
        GraphArea SemanticWorld);

    private sealed record GraphArea(IReadOnlyList<GraphNode> Nodes, IReadOnlyList<GraphEdge> Edges);
    private sealed record ProcessArea(string ApplicationId, string Name);
}
