using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using UiAtlas.Core.Build;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Recording;
using UiAtlas.Core.Storage;

namespace UiAtlas.Core.Tests;

public sealed class ObservationGraphVersionTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ExistingV4MapLoadsWithoutRewritingItsHashOrFile(bool hasObservations)
    {
        using var temp = new TempDirectory();
        var graph = CreateGraph(temp.Path, hasObservations);
        var path = Path.Combine(temp.Path, "existing.db");
        SqliteGraphStore.Save(graph, path);
        SetStoredVersion(path, graph, FormatVersions.InteractionGraph, corruptHash: false);
        var originalBytes = SHA256.HashData(File.ReadAllBytes(path));

        var loaded = SqliteGraphStore.Load(path);

        Assert.Equal("ui-atlas.uikg/5", loaded.Metadata.FormatVersion);
        Assert.Equal(graph.Metadata.SemanticHash, loaded.Metadata.SemanticHash);
        Assert.Equal(JsonSerializer.Serialize(graph.Nodes), JsonSerializer.Serialize(loaded.Nodes));
        Assert.Equal(originalBytes, SHA256.HashData(File.ReadAllBytes(path)));
        Assert.True(GraphValidator.Validate(loaded).IsValid);
    }

    [Theory]
    [InlineData(FormatVersions.InteractionGraph)]
    [InlineData(FormatVersions.Graph)]
    public void CorruptedObservationGraphHashStillFailsClosed(string version)
    {
        using var temp = new TempDirectory();
        var graph = CreateGraph(temp.Path, hasObservations: true);
        var path = Path.Combine(temp.Path, "corrupt.db");
        SqliteGraphStore.Save(graph, path);
        SetStoredVersion(path, graph, version, corruptHash: true);

        var error = Assert.Throws<InvalidDataException>(() => SqliteGraphStore.Load(path));

        Assert.Contains("graph.semantic-hash", error.Message);
    }

    private static UiKnowledgeGraph CreateGraph(string directory, bool hasObservations)
    {
        var graph = new RecordingGraphBuilder().Build(SyntheticBundleFactory.Create(directory));
        Assert.Contains(graph.Nodes, node => node.Observations is { Count: > 0 });
        if (hasObservations) return graph;
        var nodes = graph.Nodes.Select(node => node with { Observations = null }).ToArray();
        return graph with { Nodes = nodes, Metadata = graph.Metadata with { SemanticHash = GraphSemantics.ComputeHash(nodes, graph.Edges) } };
    }

    private static void SetStoredVersion(string path, UiKnowledgeGraph graph, string version, bool corruptHash)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE metadata SET value = $json WHERE key = 'graph'";
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(graph.Metadata with
        {
            FormatVersion = version,
            SemanticHash = corruptHash ? new string('0', 64) : graph.Metadata.SemanticHash
        }, JsonDefaults.Options));
        Assert.Equal(1, command.ExecuteNonQuery());
    }
}
