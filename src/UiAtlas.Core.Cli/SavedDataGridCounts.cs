using UiAtlas.Core.Contracts;

namespace UiAtlas.Core.Cli;

internal sealed record SavedDataGridCounts(int UiaNative, int NonUiaNative)
{
    public int Total => UiaNative + NonUiaNative;

    public static SavedDataGridCounts From(UiKnowledgeGraph graph, IEnumerable<SavedDataGridReview> reviews)
    {
        // Semantic controls are already merged across recording frames and sessions.
        // A DataGrid/Table role alone is only a candidate: native grids require a provider pattern.
        var native = graph.Nodes.Where(IsNativeGrid).DistinctBy(node => node.Id).ToArray();
        var surfaces = graph.Nodes.Where(node => node.Kind == GraphNodeKind.Surface)
            .ToDictionary(node => node.Id, StringComparer.Ordinal);
        var nativeCount = native.Length;
        var nonNativeCount = 0;
        foreach (var grid in reviews.DistinctBy(grid => grid.GridId))
        {
            // A reviewed native grid can also be present in the semantic graph. Count it once.
            if (native.Any(node => MatchesReview(node, surfaces, grid.Context))) continue;
            if (grid.Context.IsUiaNative == true) nativeCount++;
            else nonNativeCount++;
        }
        return new(nativeCount, nonNativeCount);
    }

    private static bool IsNativeGrid(GraphNode node) =>
        node.Kind == GraphNodeKind.Control && Has(node, "layer", "semantic-world") &&
        !node.Properties.Any(p => p.Name == "frameworkId" && p.Value.StartsWith("UiAtlas.", StringComparison.OrdinalIgnoreCase)) &&
        // Win32 status bars expose their panels through GridPattern; they are not data grids.
        !Has(node, "controlType", "StatusBar") &&
        !Has(node, "tableRow") && !Has(node, "tableColumn") &&
        !Has(node, "controlType", "DataItem") && !Has(node, "controlType", "HeaderItem") &&
        node.Properties.Any(p => p.Name == "supportedPattern" && p.Value is
            "Grid" or "Table" or "GridPattern" or "TablePattern" or
            "GridPatternIdentifiers.Pattern" or "TablePatternIdentifiers.Pattern");

    private static bool MatchesReview(GraphNode node, IReadOnlyDictionary<string, GraphNode> surfaces,
        DataGridReviewContext context)
    {
        if (!Has(node, "className", context.Control.ClassName)) return false;
        if (!string.IsNullOrWhiteSpace(context.Control.AutomationId))
        {
            if (!Has(node, "automationId", context.Control.AutomationId)) return false;
        }
        else if (!string.IsNullOrWhiteSpace(context.Control.Name) && !Has(node, "name", context.Control.Name)) return false;
        var surfaceId = node.Properties.FirstOrDefault(p => p.Name == "semanticSurfaceId")?.Value;
        if (surfaceId is null || !surfaces.TryGetValue(surfaceId, out var surface) ||
            !Has(surface, "className", context.WindowLocator.WindowClassName)) return false;
        return node.Evidence.Any(control => control.Bounds is { } bounds &&
            surface.Evidence.Any(window => window.BundleId == control.BundleId &&
                window.FrameSequence == control.FrameSequence && window.Bounds is { } root &&
                MapperHighlightModel.Equivalent(context.RelativeBounds,
                    bounds with { X = bounds.X - root.X, Y = bounds.Y - root.Y })));
    }

    private static bool Has(GraphNode node, string name, string? value = null) =>
        node.Properties.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase) &&
            (value is null || p.Value.Equals(value, StringComparison.OrdinalIgnoreCase)));
}
