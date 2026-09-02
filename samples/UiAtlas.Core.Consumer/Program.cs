using System.Text.Json;
using UiAtlas.Core.Reader;

if (args.Length == 0 || args.Contains("--help", StringComparer.OrdinalIgnoreCase))
{
    Console.WriteLine("Usage: ui-atlas-consumer <graph.db|graph.json> [--query <label>] [--json]");
    Console.WriteLine("Returns semantic controls, stable selectors, supported actions, and observed destinations.");
    return 0;
}

var path = args[0];
var queryIndex = Array.FindIndex(args, argument => argument.Equals("--query", StringComparison.OrdinalIgnoreCase));
if (queryIndex >= 0 && queryIndex + 1 >= args.Length)
{
    Console.Error.WriteLine("--query requires text.");
    return 2;
}
var query = queryIndex >= 0 ? args[queryIndex + 1] : string.Empty;
var asJson = args.Contains("--json", StringComparer.OrdinalIgnoreCase);

var graph = new UiGraphReader().Load(path);
var model = new UiMappingReadModel(graph);
var semantic = model.LayerFor(UiUnderstandingLevel.SemanticWorld);
var controls = semantic.Controls
    .Where(control => string.IsNullOrWhiteSpace(query) ||
                      control.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                      UiMappingReadModel.Properties(control.Source, "stableSelector")
                          .Any(selector => selector.Contains(query, StringComparison.OrdinalIgnoreCase)))
    .Select(control => new AgentControl(
        control.Id,
        control.DisplayName,
        control.CanonicalKind,
        control.OwnerSurfaceId,
        UiMappingReadModel.Properties(control.Source, "stableSelector"),
        model.Affordances.Where(item => item.ControlId == control.Id)
            .Select(item => item.Action.ToString()).Distinct(StringComparer.Ordinal).Order().ToArray(),
        model.Routes.Where(route => route.SourceControlId == control.Id)
            .Select(route => route.TargetStateId).Distinct(StringComparer.Ordinal).Order().ToArray(),
        control.Evidence.Count))
    .OrderBy(control => control.Label, StringComparer.OrdinalIgnoreCase)
    .ThenBy(control => control.Id, StringComparer.Ordinal)
    .ToArray();

if (asJson)
{
    Console.WriteLine(JsonSerializer.Serialize(new AgentMap(
        graph.Metadata.GraphId,
        semantic.Surfaces.Count,
        semantic.Controls.Count,
        query,
        controls), new JsonSerializerOptions { WriteIndented = true }));
    return controls.Length > 0 || string.IsNullOrWhiteSpace(query) ? 0 : 3;
}

Console.WriteLine($"{graph.Metadata.GraphId}: {semantic.Surfaces.Count} semantic surfaces, {semantic.Controls.Count} semantic controls");
Console.WriteLine(string.IsNullOrWhiteSpace(query)
    ? $"Showing {Math.Min(controls.Length, 20)} controls. Use --query to find a target or --json for an agent-friendly response."
    : $"Query '{query}': {controls.Length} matching controls.");
foreach (var control in controls.Take(20))
{
    var selector = control.Selectors.FirstOrDefault() ?? "no stable selector";
    var actions = control.Actions.Count == 0 ? "inspect" : string.Join(',', control.Actions);
    Console.WriteLine($"{control.Kind}\t{control.Label}\t{selector}\t{actions}\t{control.Id}");
}
return controls.Length > 0 || string.IsNullOrWhiteSpace(query) ? 0 : 3;

internal sealed record AgentMap(
    string MapId,
    int SurfaceCount,
    int ControlCount,
    string Query,
    IReadOnlyList<AgentControl> Controls);

internal sealed record AgentControl(
    string Id,
    string Label,
    string Kind,
    string SurfaceId,
    IReadOnlyList<string> Selectors,
    IReadOnlyList<string> Actions,
    IReadOnlyList<string> ObservedTargetStateIds,
    int EvidenceCount);
