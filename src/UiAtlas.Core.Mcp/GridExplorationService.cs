using System.IO;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Recording.Windows;

namespace UiAtlas.Core.Mcp;

public sealed record GridExplorationResult(GridCaptureStatus Status, GridAcquisitionStage Stage,
    IReadOnlyList<string> Reasons, GridTargetIdentity? Source, GridCoverage? Coverage,
    GridRestorationResult Restoration, DateTimeOffset StartedUtc, DateTimeOffset EndedUtc,
    int TileCount = 0, int MovementCount = 0, RectI? ImageBounds = null, string? ImageSha256 = null,
    string? CaptureManifestPath = null, string? ImagePath = null,
    string Notice = "Exploration covers columns across the visible row band. Complete refers to this scope; consult rowCoverageComplete for row coverage. No structured cell transcription or complete business-data answer is established.",
    GridCaptureScope Scope = GridCaptureScope.VisibleRowBand, string? MapId = null, string? GridId = null,
    string? GridName = null)
{
    [JsonIgnore] public byte[]? Png { get; init; }
    [JsonIgnore] public GridReadResult? Data { get; init; }
    public int? DataRowCount => Data?.Rows.Count;
    public GridDataStatus? DataStatus => Data?.Status;
    public static GridExplorationResult Failed(GridAcquisitionStage stage, string reason) => new(
        GridCaptureStatus.Failed, stage, [reason], null, null,
        new(GridRestorationStatus.SafelySkipped, "No exploration input was issued.", false, false),
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
}

internal sealed record GridExplorationChoice(string MapId, SavedDataGridReview Grid,
    GridTargetIdentity? Target, RectI? Bounds, string? UnavailableReason = null)
{
    public string Label => Grid.DisplayName + " · saved " + Grid.SavedUtc.ToLocalTime().ToString("g");
}

public sealed class GridExplorationService(SavedGridCatalog catalog, GridEvidenceLifetime evidence, AppGridCatalog? apps = null)
{
    public async Task<GridExplorationResult> RunAsync(GridExplorationArguments args, string id,
        IProgress<AcquisitionProgress> progress, CancellationToken cancellation)
    {
        if (args.ReadGridRef is not null)
            return await new GridReadService(apps ?? throw new InvalidOperationException("app-catalog-unavailable"), evidence)
                .RunAsync(args, id, progress, cancellation).ConfigureAwait(false);
        GridExplorationChoice[]? choices = null;
        AppGridSelection? app = null;
        SavedDataGridReview grid;
        GridTargetIdentity target;
        RectI bounds;
        if (args.AppId is { } appId)
        {
            app = (apps ?? throw new InvalidOperationException("app-catalog-unavailable")).Get(appId);
            choices = app.Catalog.Grids.Select(entry =>
            {
                cancellation.ThrowIfCancellationRequested();
                return BindChoice(entry, app.Window);
            }).ToArray();
            if (choices.Length == 0) return GridExplorationResult.Failed(GridAcquisitionStage.Bind, "no-saved-grids");
            grid = choices[0].Grid;
            var w = app.Window;
            target = new(w.Hwnd, w.RootOwnerHwnd, w.ProcessId, w.ProcessStartedUtc, w.Hwnd, w.Bounds, w.Bounds);
            bounds = w.Bounds;
        }
        else
        {
            grid = catalog.Get(args.MapId, args.GridId);
            target = DataGridTargetBinding.ResolveTarget(grid.Context.WindowLocator);
            bounds = BoundsFor(grid, target);
        }
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        await using var presentation = await GridExplorationPresentation.OpenAsync(grid.DisplayName,
            app?.Window.Title ?? grid.Context.WindowLocator.WindowTitle, target, bounds, stop, choices).ConfigureAwait(false);
        progress.Report(new(id, GridAcquisitionLifecycle.AwaitingApproval, GridAcquisitionStage.Bind,
            0, 0, 0, 0, "Confirm the selected live table in the local approval window."));
        // The operator owns approval timing. Cancellation or host disconnect closes the dialog.
        if (!await presentation.ConfirmAsync(stop.Token).ConfigureAwait(false))
            return GridExplorationResult.Failed(GridAcquisitionStage.Bind,
                cancellation.IsCancellationRequested ? "cancelled-before-approval" : "approval-declined-or-cancelled");
        var mapId = args.MapId;
        if (choices is not null)
        {
            var selected = presentation.SelectedChoice ?? throw new InvalidOperationException("grid-selection-required");
            grid = selected.Grid;
            target = selected.Target ?? throw new InvalidOperationException("grid-unavailable");
            bounds = selected.Bounds ?? throw new InvalidOperationException("grid-unavailable");
            mapId = selected.MapId;
        }
        var result = await CaptureAsync(grid, target, bounds, app?.Window, id, progress, stop.Token, presentation).ConfigureAwait(false);
        return result with { MapId = mapId, GridId = grid.GridId, GridName = grid.DisplayName };
    }

    private GridExplorationChoice BindChoice(SavedGridEntry entry, WindowTarget window)
    {
        var grid = catalog.Get(entry.MapId, entry.GridId);
        try
        {
            var target = DataGridTargetBinding.ResolveTarget(grid.Context.WindowLocator, window.Hwnd);
            VerifyApp(window, target);
            return new(entry.MapId, grid, target, BoundsFor(grid, target));
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception)
        {
            var reason = error.Message.StartsWith("saved-grid-geometry-changed", StringComparison.Ordinal)
                ? "The table layout changed. Review this table in UI Mapper again."
                : "This saved table is not available in the selected window. Open its screen and try again.";
            return new(entry.MapId, grid, null, null, reason);
        }
    }

    private async Task<GridExplorationResult> CaptureAsync(SavedDataGridReview grid, GridTargetIdentity target,
        RectI bounds, WindowTarget? app, string id, IProgress<AcquisitionProgress> progress,
        CancellationToken cancellation, GridExplorationPresentation presentation)
    {
        cancellation.ThrowIfCancellationRequested();
        await Task.Delay(200, cancellation).ConfigureAwait(false);
        var fresh = DataGridTargetBinding.ResolveTarget(grid.Context.WindowLocator, target.SelectedHwnd);
        if (app is not null) VerifyApp(app, fresh);
        if (fresh != target || BoundsFor(grid, fresh) != bounds) throw new InvalidOperationException("approved-target-changed");
        using var scope = await ApprovedGridWindowScope.EnterAsync(target, cancellation).ConfigureAwait(false);
        await presentation.StartAsync().ConfigureAwait(false);
        progress.Report(new(id, GridAcquisitionLifecycle.Running, GridAcquisitionStage.Probe,
            0, 0, 0, 0, "Preparing the approved table for visual exploration."));
        var directory = evidence.Create(id);
        GridImageExplorationResult image;
        try
        {
            image = await new GridImageExplorer().ExploreAsync(target.SelectedHwnd, target.HostHwnd,
                bounds, grid.DisplayName, directory, new CapsuleProgress(progress, presentation), cancellation).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            // A receipt-less exception cannot prove that restoration succeeded or no input occurred.
            return GridExplorationResult.Failed(GridAcquisitionStage.Capture, error.Message) with
            {
                Source = target,
                Restoration = new(GridRestorationStatus.Failed, "Exploration did not return a restoration receipt.", false, false)
            };
        }
        var capture = image.Capture;
        var result = new GridExplorationResult(capture.Status, capture.Stage, image.Reasons,
            capture.Source, capture.Coverage, capture.Restoration, capture.StartedUtc, capture.EndedUtc,
            capture.Tiles.Count, capture.Movements.Count, image.TableBounds,
            CaptureManifestPath: capture.ManifestPath, ImagePath: image.ImagePath, Scope: capture.Scope);
        if (image.ImagePath is null) return result;
        // Only the engine's new output is read. Saved review image paths are never followed.
        try
        {
            var path = Path.GetFullPath(image.ImagePath);
            if (!path.Equals(Path.Combine(directory, "table.png"), StringComparison.OrdinalIgnoreCase) ||
                (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                return result with { Reasons = result.Reasons.Append("invalid-result-image-path").ToArray() };
            if (new FileInfo(path).Length > 8 * 1024 * 1024)
                return result with { Reasons = result.Reasons.Append("mcp-image-byte-limit; image retained in local evidence").ToArray() };
            var png = await File.ReadAllBytesAsync(path, CancellationToken.None).ConfigureAwait(false);
            return result with { Png = png, ImageSha256 = Convert.ToHexString(SHA256.HashData(png)).ToLowerInvariant() };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Delivery failure must retain the real capture/restoration receipt after input.
            return result with { Reasons = result.Reasons.Append("image-output-unavailable: " + error.GetType().Name).ToArray() };
        }
    }

    internal static void VerifyApp(WindowTarget app, GridTargetIdentity target)
    {
        if (app.Hwnd != target.SelectedHwnd || app.RootOwnerHwnd != target.RootOwnerHwnd ||
            app.ProcessId != target.ProcessId || app.ProcessStartedUtc != target.ProcessStartedUtc)
            throw new InvalidOperationException("selected-app-changed");
    }

    private sealed class CapsuleProgress(IProgress<AcquisitionProgress> caller, IProgress<AcquisitionProgress> capsule)
        : IProgress<AcquisitionProgress>
    {
        public void Report(AcquisitionProgress value)
        {
            caller.Report(value);
            capsule.Report(value);
        }
    }

    public static RectI BoundsFor(SavedDataGridReview grid, GridTargetIdentity target)
    {
        var saved = grid.Context.RelativeBounds;
        var bounds = saved with { X = checked(target.WindowBounds.X + saved.X), Y = checked(target.WindowBounds.Y + saved.Y) };
        // Current native-host support requires the saved selection to still match the exact host.
        // Moving the app is fine; changed layout/size requires reviewing the grid again.
        if (bounds != target.HostBounds) throw new InvalidOperationException("saved-grid-geometry-changed; review this table again");
        return bounds;
    }
}
