using UiAtlas.Core.Contracts;

namespace UiAtlas.Core.Mcp;

public static class GridExplorationConfirmation
{
    public static async Task<bool> ConfirmAsync(SavedDataGridReview grid, GridTargetIdentity target,
        RectI bounds, CancellationToken cancellation)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        await using var presentation = await GridExplorationPresentation.OpenAsync(
            grid.DisplayName, grid.Context.WindowLocator.WindowTitle, target, bounds, stop).ConfigureAwait(false);
        return await presentation.ConfirmAsync(cancellation).ConfigureAwait(false);
    }
}
