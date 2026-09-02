using UiAtlas.Core.Build;
using UiAtlas.Core.Cli;
using UiAtlas.Core.Contracts;

namespace UiAtlas.Core.Tests;

public sealed class CustomerDataCaptureCoordinatorTests
{
    [Fact]
    public void AttachesOnlyPackageMetadataToApplicationNode()
    {
        var graph = Graph();
        var result = new CustomerDataCaptureResult(
            "captured",
            Path.Combine("C:\\private", "map.customer-data", "session-01"),
            "abacre-ahms",
            42,
            new string('a', 64));

        var updated = CustomerDataCaptureCoordinator.AttachMetadata(graph, result);

        var properties = updated.Nodes.Single().Properties.ToDictionary(property => property.Name, property => property.Value);
        Assert.Equal("captured", properties["customerDataCaptureStatus"]);
        Assert.Equal("42", properties["customerDataRecordCount"]);
        Assert.Equal(new string('a', 64), properties["customerDataSha256"]);
        Assert.Equal("session-01", properties["customerDataPackageId"]);
        Assert.DoesNotContain(properties.Values, value => value.Contains("C:\\private", StringComparison.Ordinal));
        Assert.Equal(GraphSemantics.ComputeHash(updated.Nodes, updated.Edges), updated.Metadata.SemanticHash);
        Assert.True(GraphValidator.Validate(updated).IsValid);
    }

    [Fact]
    public void PreservesExistingCustomerPackageMetadataAcrossLaterUiSessions()
    {
        var existing = CustomerDataCaptureCoordinator.AttachMetadata(
            Graph(),
            new CustomerDataCaptureResult("captured", "session-01", "abacre-ahms", 2, new string('b', 64)));

        var updated = CustomerDataCaptureCoordinator.PreserveMetadata(Graph(), existing);

        Assert.Contains(updated.Nodes.Single().Properties,
            property => property.Name == "customerDataCaptureStatus" && property.Value == "captured");
        Assert.Contains(updated.Nodes.Single().Properties,
            property => property.Name == "customerDataRecordCount" && property.Value == "2");
        Assert.Equal(GraphSemantics.ComputeHash(updated.Nodes, updated.Edges), updated.Metadata.SemanticHash);
        Assert.True(GraphValidator.Validate(updated).IsValid);
    }

    private static UiKnowledgeGraph Graph()
    {
        var node = new GraphNode(
            "app", GraphNodeKind.Application, string.Empty, "app", "App",
            [new GraphProperty("layer", "shared")], []);
        var nodes = new[] { node };
        return new UiKnowledgeGraph(
            new GraphMetadata(
                FormatVersions.Graph,
                FormatVersions.Tool,
                "graph",
                DateTimeOffset.UnixEpoch,
                "bundle",
                GraphSemantics.ComputeHash(nodes, []),
                FormatVersions.FullEvidenceProfile),
            nodes,
            []);
    }
}
