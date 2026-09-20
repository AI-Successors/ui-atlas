using System.ComponentModel;
using UiAtlas.Core.Recording.Windows;
using UiAtlas.Core.Contracts;

namespace UiAtlas.Core.Mcp;

public sealed record GridAppEntry(string AppId, string DisplayName, string ProcessName, string WindowTitle,
    int ProcessId, bool HasMap, int MapCount, int SavedGridCount);
public sealed record GridAppsResponse(IReadOnlyList<GridAppEntry> Apps, IReadOnlyList<GridCatalogIssue> Issues,
    bool ListingComplete);
public sealed record AppGridSelection(GridAppEntry App, WindowTarget Window, GridCatalogResponse Catalog);
public sealed record LiveAppGrid(string GridRef, string Name, IReadOnlyList<GridColumn> Columns,
    bool SavedSchemaComplete, bool Available, string? Reason);
public sealed record AppGridListing(GridAppEntry App, IReadOnlyList<LiveAppGrid> Grids,
    IReadOnlyList<GridCatalogIssue> Issues, bool ListingComplete);
internal sealed record SelectedAppGrid(string AppId, string MapId, SavedDataGridReview Grid,
    WindowTarget Window, GridTargetIdentity Target, RectI Bounds);

/// <summary>Uses the mapper's window catalog. App IDs select a window/process instance, never a process-name wildcard.</summary>
public sealed class AppGridCatalog(SavedGridCatalog saved,
    Func<IReadOnlyList<WindowTarget>>? listWindows = null, Func<long, WindowTarget>? resolveWindow = null)
{
    private readonly object _sync = new();
    private readonly Dictionary<string, WindowTarget> _apps = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string AppId, string MapId, string GridId, DateTimeOffset SavedUtc)> _grids = new(StringComparer.Ordinal);
    private readonly Func<IReadOnlyList<WindowTarget>> _list = listWindows ?? WindowCatalog.ListTopLevelWindows;
    private readonly Func<long, WindowTarget> _resolve = resolveWindow ?? WindowCatalog.Resolve;

    public GridAppsResponse List()
    {
        var catalog = saved.List();
        IReadOnlyList<WindowTarget> windows;
        try { windows = _list().Where(w => w.ProcessId != Environment.ProcessId).ToArray(); }
        catch (Exception error) when (error is InvalidOperationException or Win32Exception or ArgumentException)
        { return new([], [.. catalog.Issues, new("", "Application listing failed: " + error.GetType().Name)], false); }
        lock (_sync)
        {
            var entries = new List<GridAppEntry>();
            var active = new HashSet<string>(StringComparer.Ordinal);
            foreach (var window in windows)
            {
                var id = _apps.FirstOrDefault(a => SameInstance(a.Value, window)).Key ?? Guid.NewGuid().ToString("N");
                _apps[id] = window;
                active.Add(id);
                entries.Add(Describe(id, window, catalog));
            }
            foreach (var id in _apps.Keys.Where(id => !active.Contains(id)).ToArray()) _apps.Remove(id);
            foreach (var key in _grids.Where(p => !active.Contains(p.Value.AppId)).Select(p => p.Key).ToArray()) _grids.Remove(key);
            return new(entries, catalog.Issues, catalog.ListingComplete);
        }
    }

    public AppGridSelection Get(string appId)
    {
        WindowTarget previous;
        lock (_sync)
            previous = _apps.GetValueOrDefault(appId) ?? throw new GridOperationException("unknown_app",
                "Unknown app ID. Call list_apps and select a returned app; no capture was started.");
        WindowTarget current;
        try { current = _resolve(previous.Hwnd); }
        catch (Exception error) when (error is InvalidOperationException or Win32Exception or ArgumentException)
        { throw new GridOperationException("app_unavailable", "The selected app window is no longer available. List apps again."); }
        if (!SameInstance(previous, current))
            throw new GridOperationException("app_changed", "The selected app instance changed. List apps again.");
        var catalog = saved.List();
        var maps = (catalog.Maps ?? []).Where(m => Matches(m.ProcessName, current.ProcessName)).ToArray();
        var grids = catalog.Grids.Where(g => Matches(g.ProcessName, current.ProcessName)).ToArray();
        return new(Describe(appId, current, catalog), current, new(grids, catalog.Issues, catalog.ListingComplete, maps));
    }

    public AppGridListing ListGrids(string appId)
    {
        var app = Get(appId);
        var entries = new List<LiveAppGrid>();
        lock (_sync)
        {
            foreach (var key in _grids.Where(p => p.Value.AppId == appId).Select(p => p.Key).ToArray()) _grids.Remove(key);
            foreach (var entry in app.Catalog.Grids)
            {
                var reference = Guid.NewGuid().ToString("N");
                _grids[reference] = (appId, entry.MapId, entry.GridId, entry.SavedUtc);
                string? reason = null;
                try { _ = ResolveGrid(appId, reference); }
                catch (Exception error) when (error is InvalidOperationException or ArgumentException or GridOperationException or Win32Exception)
                { reason = error is GridOperationException operation ? operation.Message : error.Message; }
                entries.Add(new(reference, entry.DisplayName, entry.Columns, entry.SchemaComplete, reason is null, reason));
            }
        }
        return new(app.App, entries, app.Catalog.Issues, app.Catalog.ListingComplete);
    }

    internal SelectedAppGrid ResolveGrid(string appId, string gridRef)
    {
        (string AppId, string MapId, string GridId, DateTimeOffset SavedUtc) reference;
        lock (_sync)
        {
            if (!_grids.TryGetValue(gridRef, out reference) || reference.AppId != appId)
                throw new GridOperationException("unknown_grid_ref", "List data grids for this app again; the grid reference is unknown or expired.");
        }
        var app = Get(appId);
        var grid = saved.Get(reference.MapId, reference.GridId);
        if (grid.SavedUtc != reference.SavedUtc) throw new GridOperationException("grid_changed", "The saved table changed. List its grids again.");
        var target = DataGridTargetBinding.ResolveTarget(grid.Context.WindowLocator, app.Window.Hwnd);
        GridExplorationService.VerifyApp(app.Window, target);
        return new(appId, reference.MapId, grid, app.Window, target, GridExplorationService.BoundsFor(grid, target));
    }

    internal static bool SameInstance(WindowTarget a, WindowTarget b) => a.Hwnd == b.Hwnd &&
        a.RootOwnerHwnd == b.RootOwnerHwnd && a.ProcessId == b.ProcessId && a.ProcessStartedUtc == b.ProcessStartedUtc &&
        Matches(a.ProcessName, b.ProcessName) && a.ClassName == b.ClassName;

    private static GridAppEntry Describe(string id, WindowTarget w, GridCatalogResponse catalog)
    {
        var maps = (catalog.Maps ?? []).Count(m => Matches(m.ProcessName, w.ProcessName));
        return new(id, string.IsNullOrWhiteSpace(w.ProductName) ? w.ProcessName : w.ProductName,
            w.ProcessName, w.Title, w.ProcessId, maps > 0, maps,
            catalog.Grids.Count(g => Matches(g.ProcessName, w.ProcessName)));
    }

    private static bool Matches(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);
}
