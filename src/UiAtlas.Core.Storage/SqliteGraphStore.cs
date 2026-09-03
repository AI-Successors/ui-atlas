using System.Text.Json;
using UiAtlas.Core.Build;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Recording;
using Microsoft.Data.Sqlite;

namespace UiAtlas.Core.Storage;

public static class SqliteGraphStore
{
    public sealed record GraphSummary(
        GraphMetadata Metadata,
        int NodeCount,
        int EdgeCount,
        bool HasControlNodes,
        int SemanticControlCount);

    public static void Save(UiKnowledgeGraph graph, string path)
    {
        var curationPath = MapCurationStore.PathForMap(path);
        if (File.Exists(curationPath))
            graph = MapCurationStore.Apply(
                graph,
                MapCurationStore.Load(path, graph.Metadata.EffectiveLogicalMapId));
        SaveCore(graph, path);
    }

    public static void SaveImported(
        UiKnowledgeGraph graph,
        string path,
        MapCurationDocument? curation)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var curated = curation is null ? graph : MapCurationStore.Reapply(graph, curation);
        SaveCore(curated, path);

        var curationPath = MapCurationStore.PathForMap(path);
        if (curation is not null)
            MapCurationStore.Save(path, curation);
        else if (File.Exists(curationPath))
            File.Delete(curationPath);
    }

    private static void SaveCore(UiKnowledgeGraph graph, string path)
    {
        var validation = GraphValidator.Validate(graph);
        if (!validation.IsValid) throw new InvalidDataException("Graph failed validation.");
        AtomicFile.Publish(path, temp => WriteDatabase(graph, temp));
    }

    public static UiKnowledgeGraph Load(string path)
    {
        try { return LoadCore(path); }
        catch (SqliteException ex) { throw new InvalidDataException("Graph database is malformed or unsupported.", ex); }
        catch (JsonException ex) { throw new InvalidDataException("Graph database contains malformed JSON.", ex); }
    }

    public static GraphSummary ReadSummary(string path)
    {
        try { return ReadSummaryCore(path); }
        catch (SqliteException ex) { throw new InvalidDataException("Graph database is malformed or unsupported.", ex); }
        catch (JsonException ex) { throw new InvalidDataException("Graph database contains malformed JSON.", ex); }
    }

    private static UiKnowledgeGraph LoadCore(string path)
    {
        var fullPath = Path.GetFullPath(path);
        using var lockedInput = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (lockedInput.Length > 512L * 1024 * 1024) throw new InvalidDataException("Graph database is missing or exceeds size limit.");
        if (File.Exists(fullPath + "-wal") || File.Exists(fullPath + "-shm")) throw new InvalidDataException("Graph database sidecars are not accepted.");
        var builder = new SqliteConnectionStringBuilder { DataSource = fullPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false };
        using var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        HardenAndValidate(connection);
        var metadata = ReadMetadata(connection);
        var nodes = ReadRows<GraphNode>(connection, "SELECT json FROM nodes ORDER BY id");
        var edges = ReadRows<GraphEdge>(connection, "SELECT json FROM edges ORDER BY id");
        var graph = GraphMigration.UpgradeToCurrent(new UiKnowledgeGraph(metadata, nodes, edges));
        var validation = GraphValidator.Validate(graph);
        if (!validation.IsValid) throw new InvalidDataException("Stored graph failed validation: " +
            string.Join(", ", validation.Issues.Where(issue => issue.Severity == "error").Take(8)
                .Select(issue => $"{issue.Code}@{issue.Path}: {issue.Message}")));
        return graph;
    }

    private static GraphSummary ReadSummaryCore(string path)
    {
        var fullPath = Path.GetFullPath(path);
        using var lockedInput = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (lockedInput.Length > 512L * 1024 * 1024) throw new InvalidDataException("Graph database is missing or exceeds size limit.");
        if (File.Exists(fullPath + "-wal") || File.Exists(fullPath + "-shm")) throw new InvalidDataException("Graph database sidecars are not accepted.");
        var builder = new SqliteConnectionStringBuilder { DataSource = fullPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false };
        using var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        HardenAndValidateCatalogSummary(connection);
        var metadata = ReadMetadata(connection);
        return new GraphSummary(
            metadata,
            CountRows(connection, "nodes"),
            CountRows(connection, "edges"),
            ContainsNodeKind(connection, GraphNodeKind.Control),
            CountControlsForLayer(connection, "semantic-world"));
    }

    private static void WriteDatabase(UiKnowledgeGraph graph, string path)
    {
        var builder = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false };
        using var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                PRAGMA journal_mode=DELETE;
                PRAGMA synchronous=FULL;
                PRAGMA foreign_keys=ON;
                CREATE TABLE metadata (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                CREATE TABLE nodes (id TEXT PRIMARY KEY, kind TEXT NOT NULL, parent_id TEXT NOT NULL, label TEXT NOT NULL, json TEXT NOT NULL);
                CREATE TABLE edges (id TEXT PRIMARY KEY, kind TEXT NOT NULL, from_id TEXT NOT NULL, to_id TEXT NOT NULL, json TEXT NOT NULL);
                CREATE INDEX ix_nodes_parent ON nodes(parent_id);
                CREATE INDEX ix_edges_from ON edges(from_id);
                CREATE INDEX ix_edges_to ON edges(to_id);
                PRAGMA user_version=1;
                """;
            command.ExecuteNonQuery();
        }
        using var transaction = connection.BeginTransaction();
        InsertMetadata(connection, graph.Metadata);
        foreach (var node in graph.Nodes)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO nodes(id,kind,parent_id,label,json) VALUES($id,$kind,$parent,$label,$json)";
            command.Parameters.AddWithValue("$id", node.Id);
            command.Parameters.AddWithValue("$kind", node.Kind.ToString());
            command.Parameters.AddWithValue("$parent", node.ParentId);
            command.Parameters.AddWithValue("$label", node.Label);
            command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(node, JsonDefaults.Options));
            command.ExecuteNonQuery();
        }
        foreach (var edge in graph.Edges)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO edges(id,kind,from_id,to_id,json) VALUES($id,$kind,$from,$to,$json)";
            command.Parameters.AddWithValue("$id", edge.Id);
            command.Parameters.AddWithValue("$kind", edge.Kind);
            command.Parameters.AddWithValue("$from", edge.FromId);
            command.Parameters.AddWithValue("$to", edge.ToId);
            command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(edge, JsonDefaults.Options));
            command.ExecuteNonQuery();
        }
        transaction.Commit();
        HardenAndValidate(connection, queryOnly: false);
    }

    private static void InsertMetadata(SqliteConnection connection, GraphMetadata metadata)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO metadata(key,value) VALUES('graph',$json)";
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(metadata, JsonDefaults.Options));
        command.ExecuteNonQuery();
    }

    private static GraphMetadata ReadMetadata(SqliteConnection connection)
    {
        using (var bounds = connection.CreateCommand())
        {
            bounds.CommandText = "SELECT typeof(value), length(CAST(value AS BLOB)) FROM metadata WHERE key='graph'";
            using var reader = bounds.ExecuteReader();
            if (!reader.Read()) throw new InvalidDataException("Missing graph metadata.");
            if (!string.Equals(reader.GetString(0), "text", StringComparison.Ordinal) || reader.IsDBNull(1) || reader.GetInt64(1) > 64 * 1024)
                throw new InvalidDataException("Graph metadata is not text or exceeds the size limit.");
        }
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM metadata WHERE key='graph'";
        var json = command.ExecuteScalar() as string ?? throw new InvalidDataException("Missing graph metadata.");
        StrictJsonValidator.Validate(System.Text.Encoding.UTF8.GetBytes(json));
        return JsonSerializer.Deserialize<GraphMetadata>(json, JsonDefaults.Options) ?? throw new InvalidDataException("Invalid metadata.");
    }

    private static T[] ReadRows<T>(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var values = new List<T>();
        while (reader.Read())
        {
            if (values.Count >= 100_000) throw new InvalidDataException("Graph row count exceeds limit.");
            var json = reader.GetString(0);
            if (json.Length > 4 * 1024 * 1024) throw new InvalidDataException("Graph row exceeds size limit.");
            StrictJsonValidator.Validate(System.Text.Encoding.UTF8.GetBytes(json));
            values.Add(JsonSerializer.Deserialize<T>(json, JsonDefaults.Options) ?? throw new InvalidDataException("Invalid row JSON."));
        }
        return values.ToArray();
    }

    private static int CountRows(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table}";
        var value = command.ExecuteScalar();
        return Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static bool ContainsNodeKind(SqliteConnection connection, GraphNodeKind kind)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM nodes WHERE kind=$kind LIMIT 1)";
        command.Parameters.AddWithValue("$kind", kind.ToString());
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) != 0;
    }

    private static int CountControlsForLayer(SqliteConnection connection, string layer)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM nodes AS node
            WHERE node.kind = $kind
              AND EXISTS (
                  SELECT 1
                  FROM json_each(node.json, '$.properties') AS property
                  WHERE json_extract(property.value, '$.name') = 'layer'
                    AND json_extract(property.value, '$.value') = $layer
              )
            """;
        command.Parameters.AddWithValue("$kind", GraphNodeKind.Control.ToString());
        command.Parameters.AddWithValue("$layer", layer);
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void HardenAndValidate(SqliteConnection connection, bool queryOnly = true)
    {
        using (var pragmas = connection.CreateCommand())
        {
            pragmas.CommandText = $"PRAGMA trusted_schema=OFF; PRAGMA query_only={(queryOnly ? "ON" : "OFF")};";
            pragmas.ExecuteNonQuery();
        }
        using (var schema = connection.CreateCommand())
        {
            schema.CommandText = "SELECT type,name FROM sqlite_master WHERE name NOT LIKE 'sqlite_%'";
            using var reader = schema.ExecuteReader();
            var allowed = new HashSet<string>(StringComparer.Ordinal) { "metadata", "nodes", "edges", "ix_nodes_parent", "ix_edges_from", "ix_edges_to" };
            while (reader.Read())
            {
                var type = reader.GetString(0);
                var name = reader.GetString(1);
                if (type is not ("table" or "index") || !allowed.Contains(name)) throw new InvalidDataException("Unexpected database schema object.");
            }
        }
        using (var version = connection.CreateCommand())
        {
            version.CommandText = "PRAGMA user_version;";
            if (Convert.ToInt32(version.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) != 1)
                throw new InvalidDataException("Unexpected database schema version.");
        }
        ValidateColumns(connection, "metadata", [("key", "TEXT", 1), ("value", "TEXT", 0)]);
        ValidateColumns(connection, "nodes", [("id", "TEXT", 1), ("kind", "TEXT", 0), ("parent_id", "TEXT", 0), ("label", "TEXT", 0), ("json", "TEXT", 0)]);
        ValidateColumns(connection, "edges", [("id", "TEXT", 1), ("kind", "TEXT", 0), ("from_id", "TEXT", 0), ("to_id", "TEXT", 0), ("json", "TEXT", 0)]);
        ValidateIndex(connection, "ix_nodes_parent", ["parent_id"]);
        ValidateIndex(connection, "ix_edges_from", ["from_id"]);
        ValidateIndex(connection, "ix_edges_to", ["to_id"]);
        using (var check = connection.CreateCommand())
        {
            check.CommandText = "PRAGMA quick_check;";
            if (!string.Equals(check.ExecuteScalar() as string, "ok", StringComparison.Ordinal)) throw new InvalidDataException("Database integrity check failed.");
        }
        using (var foreignKeys = connection.CreateCommand())
        {
            foreignKeys.CommandText = "PRAGMA foreign_key_check;";
            using var reader = foreignKeys.ExecuteReader();
            if (reader.Read()) throw new InvalidDataException("Database foreign-key check failed.");
        }
    }

    private static void HardenAndValidateCatalogSummary(SqliteConnection connection)
    {
        using (var pragmas = connection.CreateCommand())
        {
            pragmas.CommandText = "PRAGMA trusted_schema=OFF; PRAGMA query_only=ON;";
            pragmas.ExecuteNonQuery();
        }
        using (var schema = connection.CreateCommand())
        {
            schema.CommandText = "SELECT type,name FROM sqlite_master WHERE name NOT LIKE 'sqlite_%'";
            using var reader = schema.ExecuteReader();
            var allowed = new HashSet<string>(StringComparer.Ordinal) { "metadata", "nodes", "edges", "ix_nodes_parent", "ix_edges_from", "ix_edges_to" };
            while (reader.Read())
            {
                var type = reader.GetString(0);
                var name = reader.GetString(1);
                if (type is not ("table" or "index") || !allowed.Contains(name)) throw new InvalidDataException("Unexpected database schema object.");
            }
        }
        using (var version = connection.CreateCommand())
        {
            version.CommandText = "PRAGMA user_version;";
            if (Convert.ToInt32(version.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) != 1)
                throw new InvalidDataException("Unexpected database schema version.");
        }
        ValidateColumns(connection, "metadata", [("key", "TEXT", 1), ("value", "TEXT", 0)]);
        ValidateColumns(connection, "nodes", [("id", "TEXT", 1), ("kind", "TEXT", 0), ("parent_id", "TEXT", 0), ("label", "TEXT", 0), ("json", "TEXT", 0)]);
        ValidateColumns(connection, "edges", [("id", "TEXT", 1), ("kind", "TEXT", 0), ("from_id", "TEXT", 0), ("to_id", "TEXT", 0), ("json", "TEXT", 0)]);
        ValidateIndex(connection, "ix_nodes_parent", ["parent_id"]);
        ValidateIndex(connection, "ix_edges_from", ["from_id"]);
        ValidateIndex(connection, "ix_edges_to", ["to_id"]);
    }

    private static void ValidateColumns(SqliteConnection connection, string table, IReadOnlyList<(string Name, string Type, int PrimaryKey)> expected)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_xinfo('{table}');";
        using var reader = command.ExecuteReader();
        var actual = new List<(string Name, string Type, int PrimaryKey, int Hidden)>();
        while (reader.Read()) actual.Add((reader.GetString(1), reader.GetString(2), reader.GetInt32(5), reader.GetInt32(6)));
        if (actual.Count != expected.Count || actual.Where((column, index) =>
                column.Name != expected[index].Name || !column.Type.Equals(expected[index].Type, StringComparison.OrdinalIgnoreCase) ||
                column.PrimaryKey != expected[index].PrimaryKey || column.Hidden != 0).Any())
            throw new InvalidDataException("Database table definition does not match v1.");
    }

    private static void ValidateIndex(SqliteConnection connection, string index, IReadOnlyList<string> expectedColumns)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA index_info('{index}');";
        using var reader = command.ExecuteReader();
        var actual = new List<string>();
        while (reader.Read()) actual.Add(reader.GetString(2));
        if (!actual.SequenceEqual(expectedColumns, StringComparer.Ordinal)) throw new InvalidDataException("Database index does not match v1.");
    }
}
