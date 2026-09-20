using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Recording.Windows;

namespace UiAtlas.Core.Mcp;

internal sealed class GridReadService(AppGridCatalog apps, GridEvidenceLifetime evidence)
{
    public async Task<GridExplorationResult> RunAsync(GridExplorationArguments args, string id,
        IProgress<AcquisitionProgress> progress, CancellationToken cancellation)
    {
        var selected = apps.ResolveGrid(args.AppId!, args.ReadGridRef!);
        // Resolve credentials before offering approval; construction does not submit any data.
        var reader = new AzureHeaderSettingsStore().CreateTableReader();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        await using var hud = await GridExplorationPresentation.OpenAsync(selected.Grid.DisplayName,
            selected.Window.Title, selected.Target, selected.Bounds, stop, readTable: true).ConfigureAwait(false);
        progress.Report(new(id, GridAcquisitionLifecycle.AwaitingApproval, GridAcquisitionStage.Bind, 0, 0, 0, 0,
            "Approve full-table capture and Azure cell reading in the local HUD."));
        if (!await hud.ConfirmAsync(stop.Token).ConfigureAwait(false))
            return GridExplorationResult.Failed(GridAcquisitionStage.Bind, "approval-declined-or-cancelled") with
            { Scope = GridCaptureScope.FullTable, Source = selected.Target, MapId = selected.MapId,
                GridId = selected.Grid.GridId, GridName = selected.Grid.DisplayName,
                Notice = "Full-table read cancelled before capture; no table data was submitted to Azure." };
        await Task.Delay(200, stop.Token).ConfigureAwait(false);
        var fresh = apps.ResolveGrid(args.AppId!, args.ReadGridRef!);
        if (fresh.Target != selected.Target || fresh.Bounds != selected.Bounds)
            throw new InvalidOperationException("approved-target-changed");
        var directory = evidence.Create(id);
        GridImageExplorationResult image;
        var combined = new ReadProgress(progress, hud);
        using (await ApprovedGridWindowScope.EnterAsync(selected.Target, stop.Token).ConfigureAwait(false))
        {
            await hud.StartAsync().ConfigureAwait(false);
            await hud.CountdownAsync(stop.Token).ConfigureAwait(false);
            try
            {
                image = await new GridImageExplorer().ExploreAsync(selected.Target.SelectedHwnd, selected.Target.HostHwnd,
                    selected.Bounds, selected.Grid.DisplayName, directory, combined, stop.Token, GridCaptureScope.FullTable, readTable: true).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                return GridExplorationResult.Failed(GridAcquisitionStage.Capture, error.Message) with
                { Source = selected.Target, Scope = GridCaptureScope.FullTable,
                    Restoration = new(GridRestorationStatus.Failed, "Capture returned no restoration receipt.", false, false) };
            }
        }
        var data = await reader.ExtractAsync(image, id, selected.Grid.GridId, combined, stop.Token, selected.Grid.Review.Schema).ConfigureAwait(false);
        var capture = image.Capture;
        byte[]? png = null;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "data.json"), JsonSerializer.Serialize(data, GridMcpTools.Json),
                CancellationToken.None).ConfigureAwait(false);
            png = image.ImagePath is { } path && new FileInfo(path).Length <= 8 * 1024 * 1024
                ? await File.ReadAllBytesAsync(path, CancellationToken.None).ConfigureAwait(false) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Keep the restoration receipt and retained data even if optional local evidence cannot be written.
            data = data with { Reasons = data.Reasons.Concat(["evidence-io-unavailable"]).ToArray(),
                Status = data.Status == GridDataStatus.Failed ? GridDataStatus.Failed : GridDataStatus.Partial };
        }
        return new(capture.Status, GridAcquisitionStage.Extract, data.Reasons, capture.Source, capture.Coverage,
            capture.Restoration, capture.StartedUtc, data.ExtractionEndedUtc, capture.Tiles.Count, capture.Movements.Count,
            image.TableBounds, png is null ? null : Convert.ToHexString(SHA256.HashData(png)).ToLowerInvariant(),
            capture.ManifestPath, image.ImagePath, "Full reachable table under current filters. Inspect dataStatus, coverage and restoration separately.",
            GridCaptureScope.FullTable, selected.MapId, selected.Grid.GridId, selected.Grid.DisplayName) { Data = data, Png = png };
    }

    private sealed class ReadProgress(IProgress<AcquisitionProgress> caller, IProgress<AcquisitionProgress> hud) : IProgress<AcquisitionProgress>
    {
        public void Report(AcquisitionProgress value)
        {
            // Capture completion is followed by Azure extraction, so Stop must remain available.
            var progress = value with { Lifecycle = GridAcquisitionLifecycle.Running };
            caller.Report(progress); hud.Report(progress);
        }
    }
}
