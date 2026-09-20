using System.IO;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Storage;

namespace UiAtlas.Core.Mcp;

public sealed record SavedGridEntry(string MapId, string GridId, string DisplayName, string ProcessName,
    string WindowTitle, IReadOnlyList<GridColumn> Columns, bool SchemaComplete, DateTimeOffset SavedUtc,
    string Notice = "Saved reviewed structure. Live availability, complete coverage and cell reading are not established.");
public sealed record GridCatalogIssue(string MapId, string Reason);
public sealed record SavedMapEntry(string MapId, string ProcessName);
public sealed record GridCatalogResponse(IReadOnlyList<SavedGridEntry> Grids, IReadOnlyList<GridCatalogIssue> Issues,
    bool ListingComplete, IReadOnlyList<SavedMapEntry>? Maps = null);

/// <summary>Reads the current recorder's named grids, without binding windows or opening saved image paths.</summary>
public sealed class SavedGridCatalog(LocalArtifactCatalog catalog)
{
    public GridCatalogResponse List(string? mapId = null)
    {
        var entries = new List<SavedGridEntry>();
        var issues = new List<GridCatalogIssue>();
        var savedMaps = new List<SavedMapEntry>();
        if (mapId is not null && !LocalArtifactCatalog.IsValidId(mapId))
            return new([], [new(mapId, "Invalid map identifier.")], false);
        var maps = mapId is null ? catalog.ListMaps().Select(x => x.Id).ToArray() : [mapId];
        foreach (var id in maps)
        {
            try
            {
                var session = LoadSession(id);
                var grids = ValidatedGrids(session);
                var process = session?.ProcessName ?? SqliteGraphStore.Load(catalog.MapPath(id)).Nodes
                    .FirstOrDefault(n => n.Kind == GraphNodeKind.Application)?.Properties
                    .FirstOrDefault(p => p.Name == "processName")?.Value;
                if (!string.IsNullOrWhiteSpace(process)) savedMaps.Add(new(id, process));
                else issues.Add(new(id, "Saved map has no application identity."));
                entries.AddRange(grids.Select(g => new SavedGridEntry(id, g.GridId, g.DisplayName,
                    g.Context.WindowLocator.ProcessName, g.Context.WindowLocator.WindowTitle,
                    g.Review.Schema.Columns, g.Review.Schema.IsComplete, g.SavedUtc)));
            }
            catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException or
                UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException or System.Text.Json.JsonException)
            { issues.Add(new(id, "Saved grid catalog could not be read: " + error.GetType().Name)); }
        }
        return new(entries, issues, issues.Count == 0, savedMaps);
    }

    public SavedDataGridReview Get(string mapId, string gridId) => Load(mapId).SingleOrDefault(g => g.GridId == gridId)
        ?? throw new GridOperationException("unknown_grid", "This map has no saved grid with that identifier.");

    public IReadOnlyList<SavedDataGridReview> Load(string mapId) => ValidatedGrids(LoadSession(mapId));

    private LogicalMapSessionManifest? LoadSession(string mapId)
    {
        catalog.EnsureSafe();
        var map = SqliteGraphStore.ReadSummary(catalog.MapPath(mapId));
        if (map.Metadata.EffectiveLogicalMapId != mapId) throw new InvalidDataException("Map identity mismatch.");
        var path = catalog.MapSessionPath(mapId);
        if (!File.Exists(path)) return null;
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Linked session file.");
        if (new FileInfo(path).Length > 16 * 1024 * 1024) throw new InvalidDataException("Oversized session file.");
        var session = LogicalMapSessionStore.Load(path);
        if (session.LogicalMapId != mapId) throw new InvalidDataException("Session identity mismatch.");
        return session;
    }

    private static IReadOnlyList<SavedDataGridReview> ValidatedGrids(LogicalMapSessionManifest? session)
    {
        if (session is null) return [];
        var grids = session.DataGrids ?? [];
        if (grids.Count > 1000 || grids.Any(g => g is null) || grids.Select(g => g.GridId).Distinct().Count() != grids.Count)
            throw new InvalidDataException("Invalid saved grid collection.");
        foreach (var grid in grids)
        {
            if (!LocalArtifactCatalog.IsValidId(grid.GridId) || string.IsNullOrWhiteSpace(grid.DisplayName) ||
                grid.DisplayName.Length > 512 || grid.Context?.WindowLocator is not { } locator ||
                string.IsNullOrWhiteSpace(locator.ProcessName) || !locator.ProcessName.Equals(session.ProcessName, StringComparison.OrdinalIgnoreCase) ||
                locator.Ancestors is null || locator.Ancestors.Count > 32 || locator.Host is null ||
                string.IsNullOrWhiteSpace(locator.Host.ClassName) || grid.Context.RelativeBounds is not { IsValid: true } bounds ||
                bounds.Width <= 0 || bounds.Height <= 1 || bounds.X < 0 || bounds.Y < 0 ||
                grid.Review?.Schema?.Columns is not { } columns || columns.Count > 128 || columns.Any(c => c is null) ||
                grid.Review.GridId != grid.GridId || grid.Review.ReviewedUtc is null)
                throw new InvalidDataException("Invalid saved grid review.");
        }
        return grids;
    }
}
