using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using UiAtlas.Core.Build;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Recording;

namespace UiAtlas.Core.Storage;

public static class HumanReadableMapExporter
{
    public const string LegacyFormatVersion = "ui-atlas.map.json/1";
    public const string FormatVersion = "ui-atlas.map.json/2";
    public const string PrivacyProfile = "sensitive-identities/1";

    public static string Publish(UiKnowledgeGraph graph, string path, bool acknowledgeSensitiveIdentities)
    {
        if (!acknowledgeSensitiveIdentities)
            throw new InvalidOperationException("Human-readable JSON export requires explicit acknowledgement of identity-bearing UI data.");
        if (!GraphValidator.Validate(graph).IsValid)
            throw new InvalidDataException("Source graph failed integrity validation.");
        if (graph.Metadata.PrivacyProfile != FormatVersions.FullEvidenceProfile)
            throw new InvalidOperationException("Human-readable JSON export requires the local full-evidence graph.");
        var bytes = Materialize(graph);
        if (!Validate(bytes).IsValid) throw new InvalidDataException("Human-readable JSON export failed validation.");
        var fullPath = Path.GetFullPath(path);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        AtomicFile.Publish(fullPath + ".sha256", temporary =>
            File.WriteAllText(temporary, $"{hash}  {Path.GetFileName(fullPath)}{Environment.NewLine}", new UTF8Encoding(false)));
        AtomicFile.Publish(fullPath, temporary => File.WriteAllBytes(temporary, bytes));
        if (!ValidateFile(fullPath).IsValid) throw new InvalidDataException("Published human-readable JSON export failed re-open validation.");
        return hash;
    }

    internal static byte[] Materialize(UiKnowledgeGraph graph)
    {
        var app = graph.Nodes.Single(node => node.Kind == GraphNodeKind.Application);
        var root = new JsonObject
        {
            ["formatVersion"] = FormatVersion,
            ["generatedUtc"] = graph.Metadata.BuiltUtc.UtcDateTime,
            ["source"] = new JsonObject
            {
                ["graphFormatVersion"] = graph.Metadata.FormatVersion,
                ["graphId"] = graph.Metadata.GraphId,
                ["semanticHash"] = graph.Metadata.SemanticHash,
                ["privacyProfile"] = PrivacyProfile
            },
            ["app"] = new JsonObject
            {
                ["id"] = app.Id,
                ["name"] = app.Label,
                ["properties"] = Properties(app)
            },
            ["process"] = new JsonObject
            {
                ["name"] = Property(app, "processName") ?? app.Label,
                ["applicationId"] = app.Id
            },
            ["rawDataStreams"] = new JsonObject { ["windows"] = RawDataStreamWindows(graph) },
            ["rawWorld"] = new JsonObject { ["windows"] = RawWorldWindows(graph) },
            ["semanticWorld"] = new JsonObject { ["windows"] = SemanticWorldWindows(graph) },
            ["interactionTrace"] = InteractionTrace(graph),
            ["routeGraph"] = RouteGraph(graph),
            ["affordances"] = Affordances(graph),
            ["negativeExamples"] = NegativeExamples(graph)
        };
        return Encoding.UTF8.GetBytes(root.ToJsonString(JsonDefaults.Options));
    }

    public static ValidationReport ValidateFile(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            using var input = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length is <= 0 or > 512L * 1024 * 1024) return Invalid("export.size", "$", "JSON export is empty or exceeds the size limit.");
            var bytes = new byte[checked((int)input.Length)];
            input.ReadExactly(bytes);
            var report = Validate(bytes);
            if (!report.IsValid) return report;
            var sidecar = File.ReadAllText(full + ".sha256").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            var actual = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            return string.Equals(sidecar, actual, StringComparison.OrdinalIgnoreCase)
                ? ValidationReport.Valid
                : Invalid("export.hash", "$", "JSON export checksum does not match.");
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
        {
            return Invalid("export.file", "$", BundleSecurity.SafeDiagnostic(ex.Message, 500));
        }
    }

    public static ValidationReport Validate(byte[] bytes)
    {
        try
        {
            StrictJsonValidator.Validate(bytes);
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            var formatVersion = Text(root, "formatVersion");
            if (root.ValueKind != JsonValueKind.Object || formatVersion is not (LegacyFormatVersion or FormatVersion))
                return Invalid("export.version", "formatVersion", "Unsupported human-readable map JSON version.");
            foreach (var name in new[] { "app", "process", "rawDataStreams", "rawWorld", "semanticWorld" })
                if (!root.TryGetProperty(name, out var area) || area.ValueKind != JsonValueKind.Object)
                    return Invalid("export.area", name, "Required export area is missing.");
            foreach (var name in new[] { "rawDataStreams", "rawWorld", "semanticWorld" })
            {
                var area = root.GetProperty(name);
                if (!area.TryGetProperty("windows", out var windows) || windows.ValueKind != JsonValueKind.Array)
                    return Invalid("export.windows", name, "World does not contain a windows array.");
                foreach (var window in windows.EnumerateArray())
                {
                    if (!window.TryGetProperty("id", out _) || !window.TryGetProperty("variants", out var variants) || variants.ValueKind != JsonValueKind.Array)
                        return Invalid("export.window", name, "Window is missing its ID or variants.");
                    foreach (var variant in variants.EnumerateArray())
                        if (!variant.TryGetProperty("id", out _) || !variant.TryGetProperty("controls", out var controls) || controls.ValueKind != JsonValueKind.Array)
                            return Invalid("export.variant", name, "Variant is missing its ID or controls.");
                }
            }
            if (formatVersion == FormatVersion)
                foreach (var name in new[] { "interactionTrace", "routeGraph", "affordances", "negativeExamples" })
                    if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
                        return Invalid("export.interactions", name, "Interaction export area is missing.");
            return ValidationReport.Valid;
        }
        catch (JsonException ex) { return Invalid("export.json", "$", BundleSecurity.SafeDiagnostic(ex.Message, 500)); }
    }

    private static JsonArray RawDataStreamWindows(UiKnowledgeGraph graph)
    {
        var surfaces = graph.Nodes.Where(node => node.Kind == GraphNodeKind.Window && Layer(node) == "raw-data-streams").ToArray();
        var controls = graph.Nodes.Where(node => node.Kind == GraphNodeKind.Control && Layer(node) == "raw-data-streams").ToArray();
        var groups = surfaces.GroupBy(node => string.Join('|', Property(node, "nativeWindowType"), Property(node, "className"),
            string.IsNullOrEmpty(Property(node, "ownerHwnd")) ? "root" : "owned"), StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal);
        var result = new JsonArray();
        foreach (var group in groups)
        {
            var values = group.OrderBy(node => node.Evidence.FirstOrDefault()?.FrameSequence ?? long.MaxValue).ToArray();
            var representative = values.OrderByDescending(node => controls.Count(control => Property(control, "rawDataStreamSurfaceId") == node.Id)).First();
            var windowId = StableIdentity.Create("window", FormatVersion, group.Key);
            var variants = new JsonArray();
            foreach (var surface in values)
            {
                foreach (var frame in FrameIdentities(surface.Evidence))
                {
                    var frameControls = controls.Where(control => Property(control, "rawDataStreamSurfaceId") == surface.Id &&
                                                                   HasEvidence(control, frame))
                        .OrderBy(control => control.Id, StringComparer.Ordinal).ToArray();
                    variants.Add(Variant($"{surface.Id}:frame:{frame.BundleId}:{frame.FrameSequence}", $"Observed frame {frame.FrameSequence}", frame.BundleId, frame.FrameSequence, true, "observed",
                    new JsonObject { ["sourceWindowIds"] = Strings([surface.Id]), ["sourceVariantIds"] = Strings([]) },
                        new JsonArray(frameControls.Select(control => (JsonNode?)Control(control, [], frame)).ToArray())));
                }
            }
            result.Add(Window(windowId, representative.Label, Property(representative, "nativeWindowType") ?? "Window", null,
                new JsonObject { ["sourceWindowIds"] = Strings(values.Select(node => node.Id)) }, Properties(representative), variants));
        }
        return result;
    }

    private static JsonArray RawWorldWindows(UiKnowledgeGraph graph)
    {
        var surfaces = graph.Nodes.Where(node => node.Kind == GraphNodeKind.Surface && Layer(node) == "raw-world").OrderBy(node => node.Id, StringComparer.Ordinal).ToArray();
        var states = graph.Nodes.Where(node => node.Kind == GraphNodeKind.State && Layer(node) == "raw-world").ToArray();
        var controls = graph.Nodes.Where(node => node.Kind == GraphNodeKind.Control && Layer(node) == "raw-world").ToArray();
        var rdsControls = graph.Nodes.Where(node => node.Kind == GraphNodeKind.Control && Layer(node) == "raw-data-streams").ToArray();
        var result = new JsonArray();
        foreach (var surface in surfaces)
        {
            var variants = new JsonArray();
            var frames = FrameIdentities(surface.Evidence);
            foreach (var frame in frames)
            {
                var state = states.FirstOrDefault(node => node.ParentId == surface.Id && HasEvidence(node, frame));
                var frameControls = controls.Where(control => Property(control, "rawSurfaceId") == surface.Id && HasEvidence(control, frame))
                    .OrderBy(control => control.Id, StringComparer.Ordinal).ToArray();
                var sourceWindows = SourceIdsAtFrame(graph, Properties(surface, "sourceRawDataStreamSurfaceId"), frame);
                variants.Add(Variant($"{state?.Id ?? surface.Id}:frame:{frame.BundleId}:{frame.FrameSequence}", state?.Label ?? $"Observed frame {frame.FrameSequence}", frame.BundleId, frame.FrameSequence, true, "observed",
                    new JsonObject { ["sourceWindowIds"] = Strings(sourceWindows), ["sourceVariantIds"] = Strings(sourceWindows) },
                    new JsonArray(frameControls.Select(control => (JsonNode?)Control(control, SourceRdsControlIds(control, rdsControls, frame), frame)).ToArray())));
            }
            result.Add(Window(surface.Id, surface.Label, Property(surface, "surfaceClass") ?? "RawWindow",
                Property(surface, "ownerRawSurfaceId"), new JsonObject { ["sourceWindowIds"] = Strings(Properties(surface, "sourceRawDataStreamSurfaceId")) },
                Properties(surface), variants));
        }
        return result;
    }

    private static JsonArray SemanticWorldWindows(UiKnowledgeGraph graph)
    {
        var surfaces = graph.Nodes.Where(node => node.Kind == GraphNodeKind.Surface && Layer(node) == "semantic-world").ToArray();
        var controls = graph.Nodes.Where(node => node.Kind == GraphNodeKind.Control && Layer(node) == "semantic-world").ToArray();
        var rawStates = graph.Nodes.Where(node => node.Kind == GraphNodeKind.State && Layer(node) == "raw-world").ToArray();
        var families = surfaces.Where(surface => Property(surface, "semanticSurfaceKind") == "PopupFamily").ToArray();
        var familyVariantIds = surfaces.Where(surface => Property(surface, "semanticSurfaceKind") == "PopupVariant").Select(surface => surface.Id).ToHashSet(StringComparer.Ordinal);
        var roots = surfaces.Where(surface => !familyVariantIds.Contains(surface.Id) && Property(surface, "semanticSurfaceKind") != "PopupFamily").Concat(families)
            .OrderBy(surface => surface.Id, StringComparer.Ordinal);
        var result = new JsonArray();
        foreach (var surface in roots)
        {
            var variants = new JsonArray();
            if (Property(surface, "semanticSurfaceKind") == "PopupFamily")
            {
                foreach (var popup in surfaces.Where(candidate => candidate.ParentId == surface.Id).OrderBy(candidate => candidate.Id, StringComparer.Ordinal))
                {
                    foreach (var frame in FrameIdentities(popup.Evidence))
                    {
                        var popupControls = controls.Where(control => Property(control, "semanticSurfaceId") == popup.Id && HasEvidence(control, frame))
                            .OrderBy(control => control.Id, StringComparer.Ordinal).ToArray();
                        var sourceWindows = Properties(popup, "sourceRawSurfaceId");
                        variants.Add(Variant($"{popup.Id}:frame:{frame.BundleId}:{frame.FrameSequence}", popup.Label, frame.BundleId, frame.FrameSequence, true, "semantic_popup_variant",
                            new JsonObject { ["sourceWindowIds"] = Strings(sourceWindows), ["sourceVariantIds"] = Strings(SourceStateIds(rawStates, sourceWindows, frame)) },
                            new JsonArray(popupControls.Select(control => (JsonNode?)Control(control, Properties(control, "sourceRawControlId"), frame)).ToArray())));
                    }
                }
            }
            else
            {
                foreach (var frame in FrameIdentities(surface.Evidence))
                {
                    var frameControls = controls.Where(control => Property(control, "semanticSurfaceId") == surface.Id && HasEvidence(control, frame))
                        .OrderBy(control => control.Id, StringComparer.Ordinal).ToArray();
                    var sourceWindows = Properties(surface, "sourceRawSurfaceId");
                    variants.Add(Variant($"{surface.Id}:frame:{frame.BundleId}:{frame.FrameSequence}", $"Observed frame {frame.FrameSequence}", frame.BundleId, frame.FrameSequence, true, "observed",
                        new JsonObject { ["sourceWindowIds"] = Strings(sourceWindows), ["sourceVariantIds"] = Strings(SourceStateIds(rawStates, sourceWindows, frame)) },
                        new JsonArray(frameControls.Select(control => (JsonNode?)Control(control, Properties(control, "sourceRawControlId"), frame)).ToArray())));
                }
            }
            result.Add(Window(surface.Id, surface.Label, Property(surface, "semanticClass") ?? Property(surface, "semanticSurfaceKind") ?? "SemanticWindow",
                null, new JsonObject { ["sourceWindowIds"] = Strings(Properties(surface, "sourceRawSurfaceId")) }, Properties(surface), variants));
        }
        return result;
    }

    private static JsonArray InteractionTrace(UiKnowledgeGraph graph)
    {
        var controls = graph.Nodes.Where(node => node.Kind == GraphNodeKind.Control).ToDictionary(node => node.Id, StringComparer.Ordinal);
        var result = new JsonArray();
        foreach (var session in graph.Edges.Where(edge => edge.Kind == "interaction")
                     .GroupBy(edge => Property(edge, "sessionId") ?? edge.Evidence.FirstOrDefault()?.BundleId ?? "unknown", StringComparer.Ordinal)
                     .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            var steps = new JsonArray();
            foreach (var group in session.GroupBy(edge => Property(edge, "interactionId") ?? edge.Id, StringComparer.Ordinal)
                         .OrderBy(group => LongProperty(group.First(), "sequence"))
                         .ThenBy(group => group.Key, StringComparer.Ordinal))
            {
                var edge = group.OrderBy(item => item.ToId, StringComparer.Ordinal).First();
                var sourceControlId = Property(edge, "sourceControlId") ?? string.Empty;
                steps.Add(new JsonObject
                {
                    ["id"] = Property(edge, "interactionId") ?? edge.Id,
                    ["operationId"] = Property(edge, "operationId") ?? string.Empty,
                    ["sequence"] = LongProperty(edge, "sequence"),
                    ["attempt"] = LongProperty(edge, "attempt"),
                    ["actor"] = Property(edge, "actor") ?? "DerivedCandidate",
                    ["gesture"] = Property(edge, "gesture") ?? "Click",
                    ["action"] = Property(edge, "action") ?? "Unknown",
                    ["outcome"] = Property(edge, "outcome") ?? "Unobserved",
                    ["sourceStateId"] = edge.FromId,
                    ["sourceControlId"] = sourceControlId,
                    ["sourceControlName"] = controls.GetValueOrDefault(sourceControlId)?.Label ?? string.Empty,
                    ["targetStateIds"] = Strings(group.Select(item => item.ToId)),
                    ["sourceFrameSequence"] = LongProperty(edge, "sourceFrameSequence"),
                    ["inputSequences"] = LongValues(group, "inputSequence"),
                    ["resultFrameSequences"] = new JsonArray(group.SelectMany(item => item.Properties)
                        .Where(property => property.Name == "resultFrameSequence")
                        .Select(property => long.TryParse(property.Value, out var value) ? value : 0)
                        .Where(value => value > 0).Distinct().Order()
                        .Select(value => (JsonNode?)value).ToArray()),
                    ["startedUtc"] = Property(edge, "startedUtc") ?? string.Empty,
                    ["completedUtc"] = Property(edge, "completedUtc") ?? string.Empty,
                    ["diagnosticCode"] = Property(edge, "diagnosticCode") ?? string.Empty
                });
            }
            result.Add(new JsonObject { ["sessionId"] = session.Key, ["steps"] = steps });
        }
        return result;
    }

    private static JsonArray RouteGraph(UiKnowledgeGraph graph)
    {
        var result = new JsonArray();
        var groups = graph.Edges.Where(edge => edge.Kind == "interaction" && Property(edge, "outcome") == "Succeeded")
            .GroupBy(edge => string.Join('\u001f', edge.FromId, Property(edge, "sourceControlId"),
                Property(edge, "action"), edge.ToId), StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal);
        foreach (var group in groups)
        {
            var first = group.First();
            var outcomes = group.Select(edge => Property(edge, "outcome") ?? "Unobserved").ToArray();
            result.Add(new JsonObject
            {
                ["sourceStateId"] = first.FromId,
                ["sourceControlId"] = Property(first, "sourceControlId") ?? string.Empty,
                ["action"] = Property(first, "action") ?? "Unknown",
                ["targetStateId"] = first.ToId,
                ["observedCount"] = group.Count(),
                ["successCount"] = outcomes.Count(value => value == "Succeeded"),
                ["failureCount"] = outcomes.Count(value => value is "Failed" or "TimedOut" or "NoChange" or "Cancelled"),
                ["interactionIds"] = Strings(group.Select(edge => Property(edge, "interactionId") ?? edge.Id))
            });
        }
        return result;
    }

    private static JsonArray Affordances(UiKnowledgeGraph graph)
    {
        var observed = graph.Edges.Where(edge => edge.Kind == "interaction")
            .Select(edge => (ControlId: Property(edge, "sourceControlId") ?? string.Empty,
                Action: Property(edge, "action") ?? "Unknown"))
            .ToHashSet();
        return new JsonArray(graph.Nodes
            .Where(node => node.Kind == GraphNodeKind.Control)
            .SelectMany(control => Properties(control, "affordance").Select(action => (Control: control, Action: action)))
            .OrderBy(item => item.Control.Id, StringComparer.Ordinal)
            .ThenBy(item => item.Action, StringComparer.Ordinal)
            .Select(item => (JsonNode?)new JsonObject
            {
                ["controlId"] = item.Control.Id,
                ["controlName"] = item.Control.Label,
                ["action"] = item.Action,
                ["status"] = observed.Contains((item.Control.Id, item.Action)) ? "Observed" : "Unobserved",
                ["destinationKnown"] = observed.Contains((item.Control.Id, item.Action)),
                ["safeForAutoExplore"] = IsSafeForAutoExplore(item.Control, item.Action)
            }).ToArray());
    }

    private static JsonArray NegativeExamples(UiKnowledgeGraph graph) => new(graph.Edges
        .Where(edge => edge.Kind == "interaction")
        .Where(edge => Property(edge, "outcome") is "Failed" or "TimedOut" or "NoChange" or "Cancelled" or "Unobserved")
        .GroupBy(edge => Property(edge, "interactionId") ?? edge.Id, StringComparer.Ordinal)
        .OrderBy(group => group.Key, StringComparer.Ordinal)
        .Select(group =>
        {
            var edge = group.First();
            return (JsonNode?)new JsonObject
            {
                ["interactionId"] = group.Key,
                ["operationId"] = Property(edge, "operationId") ?? string.Empty,
                ["sourceStateId"] = edge.FromId,
                ["sourceControlId"] = Property(edge, "sourceControlId") ?? string.Empty,
                ["action"] = Property(edge, "action") ?? "Unknown",
                ["outcome"] = Property(edge, "outcome") ?? "Failed",
                ["diagnosticCode"] = Property(edge, "diagnosticCode") ?? string.Empty
            };
        }).ToArray());

    private static bool IsSafeForAutoExplore(GraphNode control, string action)
    {
        if (bool.TryParse(Property(control, "safeForAutoExplore"), out var explicitlySafe) && !explicitlySafe)
            return false;
        if (Property(control, "interactionMethod") == "VisualCoordinate" &&
            Property(control, "actionVerificationStatus") != "Observed")
            return false;
        if (action is "SetValue" or "MoveResize") return false;
        var text = $"{control.Label} {Property(control, "automationId")}";
        return !new[] { "delete", "remove", "send", "submit", "sign out", "logout", "purchase", "buy", "pay", "publish" }
            .Any(token => text.Contains(token, StringComparison.OrdinalIgnoreCase));
    }

    private static long LongProperty(GraphEdge edge, string name) =>
        long.TryParse(Property(edge, name), out var value) ? value : 0;

    private static JsonArray LongValues(IEnumerable<GraphEdge> edges, string name) => new(edges
        .SelectMany(edge => edge.Properties)
        .Where(property => property.Name == name)
        .Select(property => long.TryParse(property.Value, out var value) ? value : 0)
        .Where(value => value > 0).Distinct().Order()
        .Select(value => (JsonNode?)value).ToArray());

    private static JsonObject Window(string id, string name, string kind, string? ownerWindowId, JsonObject lineage, JsonObject properties, JsonArray variants)
    {
        var value = new JsonObject { ["id"] = id, ["name"] = name, ["kind"] = kind, ["lineage"] = lineage, ["properties"] = properties, ["variants"] = variants };
        if (!string.IsNullOrEmpty(ownerWindowId)) value["ownerWindowId"] = ownerWindowId;
        return value;
    }

    private static JsonObject Variant(string id, string name, string bundleId, long frame, bool visible, string reason, JsonObject lineage, JsonArray controls) => new()
    {
        ["id"] = id, ["name"] = name, ["bundleId"] = bundleId, ["frameSequence"] = frame, ["visibleByDefault"] = visible, ["reason"] = reason,
        ["lineage"] = lineage,
        ["controls"] = controls
    };

    private static JsonObject Control(GraphNode control, IEnumerable<string> sourceControlIds, FrameIdentity? frame = null)
    {
        var bounds = frame is null
            ? control.Evidence.FirstOrDefault()?.Bounds
            : control.Evidence.FirstOrDefault(evidence => MatchesFrame(evidence, frame.Value))?.Bounds;
        return new JsonObject
        {
            ["id"] = control.Id, ["name"] = control.Label,
            ["kind"] = Property(control, "controlType") ?? "Control",
            ["parentControlId"] = control.ParentId.Contains("control", StringComparison.Ordinal) ? control.ParentId : null,
            ["bounds"] = bounds is null ? null : new JsonObject { ["x"] = bounds.X, ["y"] = bounds.Y, ["width"] = bounds.Width, ["height"] = bounds.Height },
            ["lineage"] = new JsonObject { ["sourceControlIds"] = Strings(sourceControlIds) },
            ["properties"] = Properties(control)
        };
    }

    private static JsonObject Properties(GraphNode node)
    {
        var result = new JsonObject();
        foreach (var group in node.Properties.Where(property => property.Name != "layer").GroupBy(property => property.Name, StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            var values = group.Select(property => property.Value).Distinct(StringComparer.Ordinal).ToArray();
            result[group.Key] = values.Length == 1 ? JsonValue.Create(values[0]) : Strings(values);
        }
        return result;
    }

    private static JsonArray Strings(IEnumerable<string> values) => new(values.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal)
        .OrderBy(value => value, StringComparer.Ordinal).Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
    private static IReadOnlyList<FrameIdentity> FrameIdentities(IEnumerable<EvidenceRef> evidence) => evidence
        .Where(item => item.FrameSequence > 0)
        .Select(item => new FrameIdentity(item.BundleId, item.FrameSequence))
        .Distinct()
        .OrderBy(item => item.BundleId, StringComparer.Ordinal)
        .ThenBy(item => item.FrameSequence)
        .ToArray();
    private static bool HasEvidence(GraphNode node, FrameIdentity frame) => node.Evidence.Any(evidence => MatchesFrame(evidence, frame));
    private static bool MatchesFrame(EvidenceRef evidence, FrameIdentity frame) =>
        string.Equals(evidence.BundleId, frame.BundleId, StringComparison.Ordinal) && evidence.FrameSequence == frame.FrameSequence;
    private static string[] SourceIdsAtFrame(UiKnowledgeGraph graph, IEnumerable<string> ids, FrameIdentity frame) => ids
        .Where(id => graph.Nodes.Any(node => node.Id == id && node.Evidence.Any(evidence => MatchesFrame(evidence, frame)))).ToArray();
    private static string[] SourceStateIds(IEnumerable<GraphNode> states, IEnumerable<string> windowIds, FrameIdentity frame)
    {
        var windows = windowIds.ToHashSet(StringComparer.Ordinal);
        return states.Where(state => windows.Contains(state.ParentId) && state.Evidence.Any(evidence => MatchesFrame(evidence, frame)))
            .Select(state => state.Id).ToArray();
    }
    private static string[] SourceRdsControlIds(GraphNode rawControl, IEnumerable<GraphNode> rdsControls, FrameIdentity frame)
    {
        var rawEvidence = rawControl.Evidence.FirstOrDefault(evidence => MatchesFrame(evidence, frame));
        if (rawEvidence is null) return [];
        return rdsControls.Where(candidate => candidate.Evidence.Any(evidence => MatchesFrame(evidence, frame) && Equals(evidence.Bounds, rawEvidence.Bounds)) &&
                                             Property(candidate, "controlType") == Property(rawControl, "controlType") &&
                                             Property(candidate, "className") == Property(rawControl, "className") &&
                                             Property(candidate, "automationId") == Property(rawControl, "automationId"))
            .Select(candidate => candidate.Id).Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToArray();
    }
    private static string? Layer(GraphNode node) => Property(node, "layer");
    private static string? Property(GraphNode node, string name) => node.Properties.FirstOrDefault(property => property.Name == name)?.Value;
    private static string? Property(GraphEdge edge, string name) => edge.Properties.FirstOrDefault(property => property.Name == name)?.Value;
    private static string[] Properties(GraphNode node, string name) => node.Properties.Where(property => property.Name == name).Select(property => property.Value).ToArray();
    private static string? Text(JsonElement value, string name) => value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    private static ValidationReport Invalid(string code, string path, string message) => new(false, [new(code, "error", path, message)]);
    private readonly record struct FrameIdentity(string BundleId, long FrameSequence);
}

public static class SqliteMapExporter
{
    public static string Publish(UiKnowledgeGraph graph, string path)
    {
        var fullPath = Path.GetFullPath(path);
        SqliteGraphStore.Save(graph, fullPath);
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fullPath))).ToLowerInvariant();
        AtomicFile.Publish(fullPath + ".sha256", temporary =>
            File.WriteAllText(temporary, $"{hash}  {Path.GetFileName(fullPath)}{Environment.NewLine}", new UTF8Encoding(false)));
        return hash;
    }

    public static ValidationReport ValidateFile(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var graph = SqliteGraphStore.Load(fullPath);
            var report = GraphValidator.Validate(graph);
            if (!report.IsValid) return report;
            var expected = File.ReadAllText(fullPath + ".sha256").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            var actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fullPath))).ToLowerInvariant();
            return string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase)
                ? ValidationReport.Valid
                : new(false, [new("export.hash", "error", "$", "SQLite export checksum does not match.")]);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return new(false, [new("export.file", "error", "$", BundleSecurity.SafeDiagnostic(ex.Message, 500))]);
        }
    }
}
