using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Globalization;
using UiAtlas.Core.Build;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Recording;

namespace UiAtlas.Core.Storage;

public static class UiAtlasVNextCompatibilityExporter
{
    public const string AdapterVersion = "ui-atlas-vnext-compat/1";
    public const string RequiredFileName = "ui_knowledge_graph_vnext.json";
    public const string PrivacyProfile = "sensitive-identities/1";

    public static string Publish(UiKnowledgeGraph graph, string path, string projectId, bool acknowledgeSensitiveIdentities)
    {
        if (!acknowledgeSensitiveIdentities)
            throw new InvalidOperationException("Compatibility export requires explicit acknowledgement of identity-bearing UI data.");
        if (!GraphValidator.Validate(graph).IsValid) throw new InvalidDataException("Source graph failed integrity validation.");
        if (graph.Metadata.PrivacyProfile != FormatVersions.FullEvidenceProfile)
            throw new InvalidOperationException("Compatibility export requires the local full-evidence graph, not a privacy-safe export.");
        if (!ValidProjectId(projectId)) throw new ArgumentException("Project identifier is invalid.", nameof(projectId));

        var bytes = Materialize(graph, projectId);
        var validation = UiAtlasVNextCompatibilityValidator.Validate(bytes);
        if (!validation.IsValid)
            throw new InvalidDataException("Compatibility artifact failed validation: " +
                string.Join(", ", validation.Issues.Take(8)
                    .Select(issue => $"{issue.Code}@{issue.Path}: {issue.Message}")));
        var fullPath = Path.GetFullPath(path);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        AtomicFile.Publish(fullPath + ".sha256", temporary =>
            File.WriteAllText(temporary, $"{hash}  {Path.GetFileName(fullPath)}{Environment.NewLine}", new UTF8Encoding(false)));
        AtomicFile.Publish(fullPath, temporary => File.WriteAllBytes(temporary, bytes));
        if (!UiAtlasVNextCompatibilityValidator.ValidateFile(fullPath).IsValid)
            throw new InvalidDataException("Published compatibility artifact failed re-open validation.");
        return hash;
    }

    internal static byte[] Materialize(UiKnowledgeGraph graph, string projectId)
    {
        var app = graph.Nodes.SingleOrDefault(node => node.Kind == GraphNodeKind.Application)
            ?? throw new InvalidDataException("Graph must contain exactly one application.");
        var surfaces = graph.Nodes.Where(node => node.Kind == GraphNodeKind.Surface && Property(node, "layer") == "semantic-world" &&
                                                  Property(node, "semanticSurfaceKind") != "PopupFamily")
            .OrderBy(node => node.Id, StringComparer.Ordinal).ToArray();
        if (surfaces.Length == 0) throw new InvalidDataException("Graph has no Semantic World surfaces to publish.");
        var rawById = graph.Nodes.Where(node => node.Kind == GraphNodeKind.Surface && Property(node, "layer") == "raw-world")
            .ToDictionary(node => node.Id, StringComparer.Ordinal);
        var surfaceByRaw = surfaces.Select(surface => (Raw: Property(surface, "sourceRawSurfaceId"), Surface: surface))
            .Where(pair => pair.Raw is not null).ToDictionary(pair => pair.Raw!, pair => pair.Surface, StringComparer.Ordinal);
        var appId = CompatibleId("app", projectId, app.Id);
        var windowIds = surfaces.ToDictionary(surface => surface.Id, surface => CompatibleId("window", projectId, surface.Id), StringComparer.Ordinal);
        var windowsById = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var surface in surfaces)
        {
            var rawId = Property(surface, "sourceRawSurfaceId");
            rawById.TryGetValue(rawId ?? string.Empty, out var raw);
            var bounds = surface.Evidence.OrderBy(evidence => evidence.FrameSequence).FirstOrDefault()?.Bounds ?? new RectI(0, 0, 0, 0);
            var kind = Property(surface, "semanticSurfaceKind") ?? "Window";
            var surfaceClass = Property(surface, "surfaceClass") ?? "RawWindow";
            var ownerRaw = Property(surface, "sourceOwnerRawSurfaceId");
            var ownerId = ownerRaw is not null && surfaceByRaw.TryGetValue(ownerRaw, out var owner) ? windowIds[owner.Id] : null;
            var value = new JsonObject
            {
                ["windowId"] = windowIds[surface.Id], ["sceneId"] = 0, ["applicationId"] = appId, ["kind"] = kind,
                ["title"] = raw is null ? surface.Label : Property(raw, "title") ?? surface.Label,
                ["mode"] = surfaceClass.Contains("Dialog", StringComparison.Ordinal) ? "dialog" : surfaceClass.Contains("Popup", StringComparison.Ordinal) ? "borderless" : "normal",
                ["chromeMode"] = surfaceClass.Contains("Popup", StringComparison.Ordinal) ? "borderless" : "normal",
                ["x"] = bounds.X, ["y"] = bounds.Y, ["width"] = bounds.Width, ["height"] = bounds.Height,
                ["isModal"] = surfaceClass.Contains("Dialog", StringComparison.Ordinal), ["showInTaskbar"] = surfaceClass == "RawWindow",
                ["alwaysOnTop"] = raw is not null && bool.TryParse(Property(raw, "isTopMost"), out var topMost) && topMost,
                ["defaultZIndex"] = 0, ["processName"] = raw is null ? Property(app, "processName") : Property(raw, "processName"),
                ["className"] = raw is null ? Property(surface, "className") : Property(raw, "className"),
                ["originalFilename"] = Property(app, "originalFilename"), ["productVersion"] = Property(app, "productVersion"),
                ["companyName"] = Property(app, "companyName"), ["productName"] = Property(app, "productName"),
                ["semanticSurfaceId"] = windowIds[surface.Id], ["semanticSurfaceKind"] = kind,
                ["semanticSurfaceDisplay"] = surface.Label, ["semanticParentSurfaceId"] = ownerId,
                ["semanticPopupFamilyId"] = kind == "PopupVariant" ? CompatibleId("popup-family", projectId, surface.ParentId) : null,
                ["semanticPopupVariantId"] = kind == "PopupVariant" ? windowIds[surface.Id] : null
            };
            RemoveNulls(value);
            windowsById.Add(surface.Id, value);
        }

        var semanticControls = graph.Nodes.Where(node => node.Kind == GraphNodeKind.Control && Property(node, "layer") == "semantic-world" &&
                                                         windowIds.ContainsKey(Property(node, "semanticSurfaceId") ?? string.Empty))
            .OrderBy(node => node.Id, StringComparer.Ordinal).ToArray();
        var controlIds = semanticControls.ToDictionary(control => control.Id, control => CompatibleId("control", projectId, control.Id), StringComparer.Ordinal);
        var controls = new JsonArray();
        foreach (var control in semanticControls)
        {
            var surfaceId = Property(control, "semanticSurfaceId")!;
            var bounds = control.Evidence.OrderBy(evidence => evidence.FrameSequence).FirstOrDefault()?.Bounds ?? new RectI(0, 0, 0, 0);
            var identity = new JsonObject
            {
                ["automationId"] = Property(control, "automationId"), ["name"] = Property(control, "name"),
                ["className"] = Property(control, "className"), ["frameworkId"] = Property(control, "frameworkId"),
                ["semanticSurfaceId"] = windowIds[surfaceId],
                ["semanticSurfaceKind"] = windowsById[surfaceId]["semanticSurfaceKind"]!.GetValue<string>()
            };
            RemoveNulls(identity);
            var structure = new JsonObject
            {
                ["parentControlId"] = controlIds.TryGetValue(control.ParentId, out var parent) ? parent : null,
                ["partOfWindow"] = windowIds[surfaceId], ["containerPath"] = Property(control, "controlPath")
            };
            RemoveNulls(structure);
            var value = new JsonObject
            {
                ["controlId"] = controlIds[control.Id], ["controlKey"] = controlIds[control.Id], ["sceneId"] = 0,
                ["windowId"] = windowIds[surfaceId], ["controlType"] = Property(control, "controlType") ?? "Unknown",
                ["role"] = Property(control, "role") ?? Property(control, "controlType") ?? "Unknown", ["label"] = Property(control, "name") ?? control.Label,
                ["canonicalKind"] = CanonicalKind(Property(control, "controlType")), ["supportedPatterns"] = Array(control, "supportedPattern"),
                ["stableSelectors"] = Array(control, "stableSelector"), ["identity"] = identity, ["structure"] = structure,
                ["x"] = bounds.X, ["y"] = bounds.Y, ["width"] = bounds.Width, ["height"] = bounds.Height,
                ["verificationStatus"] = Property(control, "verificationStatus"),
                ["confirmationSource"] = Property(control, "confirmationSource"),
                ["geometrySource"] = Property(control, "geometrySource"),
                ["interactionMethod"] = Property(control, "interactionMethod"),
                ["actionVerificationStatus"] = Property(control, "actionVerificationStatus")
            };
            if (bool.TryParse(Property(control, "safeForAutoExplore"), out var safe))
                value["safeForAutoExplore"] = safe;
            RemoveNulls(value);
            controls.Add(value);
        }

        var applications = new JsonArray(new JsonObject { ["applicationId"] = appId, ["name"] = Property(app, "processName") ?? app.Label, ["inTwinDefault"] = true });
        var windows = new JsonArray();
        foreach (var surface in surfaces) windows.Add(windowsById[surface.Id]);
        var observationPackages = BuildObservationPackages(graph, projectId);
        var root = new JsonObject
        {
            ["schemaVersion"] = 5, ["kind"] = "uikg/vnext", ["projectId"] = projectId, ["generatedUtc"] = graph.Metadata.BuiltUtc.UtcDateTime,
            ["UiAtlasCore"] = new JsonObject
            {
                ["adapterVersion"] = AdapterVersion, ["privacyProfile"] = PrivacyProfile,
                ["sourceFormatVersion"] = graph.Metadata.FormatVersion, ["sourceSemanticHash"] = graph.Metadata.SemanticHash
            },
            ["buildRevision"] = new JsonObject
            {
                ["buildId"] = CompatibleId("build", projectId, graph.Metadata.SemanticHash), ["projectId"] = projectId,
                ["generatedUtc"] = graph.Metadata.BuiltUtc.UtcDateTime, ["sourcePackageCount"] = observationPackages.Count
            },
            ["authoring"] = new JsonObject { ["applications"] = applications, ["windows"] = windows, ["controls"] = controls },
            ["observationPackages"] = observationPackages
        };
        return Encoding.UTF8.GetBytes(root.ToJsonString(JsonDefaults.Options));
    }

    private static JsonArray BuildObservationPackages(UiKnowledgeGraph graph, string projectId)
    {
        var streamSurfaces = graph.Nodes
            .Where(node => node.Kind == GraphNodeKind.Window && Property(node, "layer") == "raw-data-streams")
            .ToArray();
        var streamControls = graph.Nodes
            .Where(node => node.Kind == GraphNodeKind.Control && Property(node, "layer") == "raw-data-streams")
            .ToArray();
        var packages = new JsonArray();
        foreach (var frameGroup in streamSurfaces
                     .SelectMany(surface => surface.Evidence.Select(evidence => (Surface: surface, Frame: new FrameIdentity(evidence.BundleId, evidence.FrameSequence))))
                     .GroupBy(item => item.Frame)
                     .OrderBy(group => group.Key.BundleId, StringComparer.Ordinal)
                     .ThenBy(group => group.Key.FrameSequence))
        {
            var frame = frameGroup.Key;
            var bundleId = frame.BundleId;
            var frameSequence = frame.FrameSequence;
            var frameToken = bundleId + ":" + frameSequence.ToString(CultureInfo.InvariantCulture);
            var surfaces = frameGroup.Select(item => item.Surface).DistinctBy(node => node.Id, StringComparer.Ordinal).ToArray();
            var bounds = surfaces.Select(surface => surface.Evidence.First(evidence => MatchesFrame(evidence, frame)).Bounds)
                .Where(value => value is not null).Cast<RectI>().ToArray();
            var left = bounds.Length == 0 ? 0 : bounds.Min(value => value.X);
            var top = bounds.Length == 0 ? 0 : bounds.Min(value => value.Y);
            var right = bounds.Length == 0 ? 1 : bounds.Max(value => value.X + value.Width);
            var bottom = bounds.Length == 0 ? 1 : bounds.Max(value => value.Y + value.Height);
            var packageId = CompatibleId("package", projectId, frameToken);
            var artifactId = CompatibleId("artifact", projectId, frameToken + ":observation");
            var win32StreamId = CompatibleId("stream", projectId, frameToken + ":win32");
            var uiaStreamId = CompatibleId("stream", projectId, frameToken + ":uia");
            var windowsPayload = new JsonArray();
            foreach (var surface in surfaces)
            {
                var evidence = surface.Evidence.First(item => MatchesFrame(item, frame));
                var value = evidence.Bounds ?? new RectI(0, 0, 0, 0);
                windowsPayload.Add(new JsonObject
                {
                    ["zOrder"] = ParseInt(Property(surface, "zOrder")),
                    ["hwndHex"] = string.Empty,
                    ["processId"] = 0,
                    ["processName"] = Property(surface, "processName") ?? string.Empty,
                    ["title"] = Property(surface, "title") ?? surface.Label,
                    ["className"] = Property(surface, "className") ?? string.Empty,
                    ["boundsScreen"] = Rect(value),
                    ["boundsImagePx"] = Rect(value with { X = value.X - left, Y = value.Y - top }),
                    ["isVisible"] = ParseBool(Property(surface, "isVisible")),
                    ["isCloaked"] = ParseBool(Property(surface, "isCloaked")),
                    ["isMinimized"] = ParseBool(Property(surface, "isMinimized")),
                    ["isToolWindow"] = ParseBool(Property(surface, "isToolWindow")),
                    ["isTopMost"] = ParseBool(Property(surface, "isTopMost")),
                    ["style"] = ParseHex(Property(surface, "style")),
                    ["exStyle"] = ParseHex(Property(surface, "exStyle")),
                    ["resolvedWindowAnchor"] = surface.Id,
                    ["windowAnchorAliases"] = new JsonArray(JsonValue.Create(surface.Id))
                });
            }

            var surfaceIds = surfaces.Select(surface => surface.Id).ToHashSet(StringComparer.Ordinal);
            var controlsPayload = new JsonArray();
            foreach (var control in streamControls.Where(control =>
                         surfaceIds.Contains(Property(control, "rawDataStreamSurfaceId") ?? string.Empty) &&
                         control.Evidence.Any(evidence => MatchesFrame(evidence, frame)))
                     .OrderBy(control => control.Id, StringComparer.Ordinal))
            {
                var evidence = control.Evidence.First(item => MatchesFrame(item, frame));
                var value = evidence.Bounds ?? new RectI(0, 0, 0, 0);
                var ownerId = Property(control, "rawDataStreamSurfaceId") ?? string.Empty;
                var stableControlKey = Property(control, "stableControlKey") ??
                    StableIdentity.Create("control", Property(control, "controlType") ?? string.Empty,
                        Property(control, "automationId") ?? string.Empty, Property(control, "className") ?? string.Empty);
                var relationships = new JsonObject
                {
                    ["containerPath"] = new JsonArray(JsonValue.Create(stableControlKey)),
                    ["controlViewPath"] = new JsonArray(JsonValue.Create(stableControlKey)),
                    ["contentViewPath"] = new JsonArray(),
                    ["rawViewPath"] = new JsonArray(JsonValue.Create(stableControlKey)),
                    ["rawViewIdentityPath"] = new JsonArray(JsonValue.Create(stableControlKey)),
                    ["nativeWindowLineage"] = ownerId
                };
                if (streamControls.Any(parent => parent.Id == control.ParentId))
                {
                    var parentNode = streamControls.First(parent => parent.Id == control.ParentId);
                    relationships["parent"] = new JsonObject
                    {
                        ["runtimeId"] = Property(parentNode, "stableControlKey") ?? parentNode.Id,
                        ["summary"] = parentNode.Label
                    };
                }
                controlsPayload.Add(new JsonObject
                {
                    ["windowHwndHex"] = string.Empty,
                    ["windowProcessId"] = 0,
                    ["windowProcessName"] = surfaces.FirstOrDefault(surface => surface.Id == ownerId) is { } owner ? Property(owner, "processName") ?? string.Empty : string.Empty,
                    ["effectiveWindowKey"] = ownerId,
                    ["ownerResolutionKind"] = "recorded_window",
                    ["automationId"] = Property(control, "automationId") ?? string.Empty,
                    ["runtimeId"] = stableControlKey,
                    ["name"] = Property(control, "name") ?? control.Label,
                    ["className"] = Property(control, "className") ?? string.Empty,
                    ["frameworkId"] = Property(control, "frameworkId") ?? string.Empty,
                    ["controlType"] = Property(control, "controlType") ?? string.Empty,
                    ["localizedControlType"] = Property(control, "controlType") ?? string.Empty,
                    ["boundsScreen"] = Rect(value),
                    ["boundsImagePx"] = Rect(value with { X = value.X - left, Y = value.Y - top }),
                    ["geometrySource"] = "recorded",
                    ["geometryConfidence"] = value.Width > 0 && value.Height > 0 ? 1.0 : 0.0,
                    ["isEnabled"] = ParseBool(Property(control, "enabled")),
                    ["isOffscreen"] = ParseBool(Property(control, "offscreen")),
                    ["supportedPatterns"] = new JsonArray(Properties(control, "supportedPattern").Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()),
                    ["identity"] = new JsonObject { ["processId"] = 0, ["isControlElement"] = true, ["isContentElement"] = true },
                    ["relationships"] = relationships
                });
            }

            var evidenceEntry = frameGroup
                .SelectMany(item => item.Surface.Evidence)
                .First(evidence => MatchesFrame(evidence, frame));
            var observation = new JsonObject
            {
                ["schemaVersion"] = 9,
                ["sessionId"] = StableGuid(bundleId),
                ["frameIndex"] = frameSequence,
                ["timestampUtc"] = graph.Metadata.BuiltUtc.UtcDateTime,
                ["observationCapturedUtc"] = graph.Metadata.BuiltUtc.UtcDateTime,
                ["captureMode"] = "manual_map_step",
                ["pngFileName"] = evidenceEntry.ScreenshotEntry ?? string.Empty,
                ["captureReason"] = "ManualMapStep",
                ["imageWidth"] = Math.Max(1, right - left),
                ["imageHeight"] = Math.Max(1, bottom - top),
                ["captureRectScreen"] = Rect(new RectI(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top))),
                ["screenToImageScaleX"] = 1.0,
                ["screenToImageScaleY"] = 1.0,
                ["windows"] = windowsPayload,
                ["automationControls"] = controlsPayload,
                ["visionControls"] = new JsonArray(),
                ["textRegions"] = new JsonArray(),
                ["triggerKind"] = "manual",
                ["inputEventIds"] = new JsonArray()
            };
            var artifacts = new JsonArray(new JsonObject
            {
                ["artifactId"] = artifactId,
                ["kind"] = "artifact.observation_entry",
                ["path"] = evidenceEntry.ObservationEntry,
                ["mediaType"] = "application/json",
                ["payloadJson"] = observation.ToJsonString(JsonDefaults.Options)
            });
            var streams = new JsonArray(
                new JsonObject { ["streamId"] = win32StreamId, ["kind"] = "stream.win32", ["status"] = "captured", ["source"] = "manual", ["confidence"] = 1.0,
                    ["observedUtc"] = graph.Metadata.BuiltUtc.UtcDateTime, ["artifactIds"] = new JsonArray(JsonValue.Create(artifactId)) },
                new JsonObject { ["streamId"] = uiaStreamId, ["kind"] = "stream.uia", ["status"] = "captured", ["source"] = "manual", ["confidence"] = 1.0,
                    ["observedUtc"] = graph.Metadata.BuiltUtc.UtcDateTime, ["artifactIds"] = new JsonArray(JsonValue.Create(artifactId)) });
            packages.Add(new JsonObject
            {
                ["packageId"] = packageId,
                ["kind"] = "observation_package.variant",
                ["displayName"] = $"Observed frame {frameSequence.ToString(CultureInfo.InvariantCulture)}",
                ["variantId"] = CompatibleId("variant", projectId, frameToken),
                ["sceneId"] = checked((int)Math.Min(frameSequence, int.MaxValue)),
                ["sourceSessionId"] = StableGuid(bundleId),
                ["recordingSessionKey"] = CompatibleId("recording", projectId, bundleId),
                ["source"] = "manual-recording",
                ["confidence"] = 1.0,
                ["observedUtc"] = graph.Metadata.BuiltUtc.UtcDateTime,
                ["captureReason"] = "ManualMapStep",
                ["materializationStatus"] = "reproducible",
                ["buildRevisionId"] = CompatibleId("build", projectId, graph.Metadata.SemanticHash),
                ["artifacts"] = artifacts,
                ["streams"] = streams
            });
        }
        return packages;
    }

    private static JsonObject Rect(RectI value) => new()
    {
        ["x"] = value.X,
        ["y"] = value.Y,
        ["width"] = value.Width,
        ["height"] = value.Height
    };

    private static string StableGuid(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return new Guid(bytes.AsSpan(0, 16)).ToString("D", CultureInfo.InvariantCulture);
    }

    private static bool MatchesFrame(EvidenceRef evidence, FrameIdentity frame) =>
        string.Equals(evidence.BundleId, frame.BundleId, StringComparison.Ordinal) && evidence.FrameSequence == frame.FrameSequence;
    private static int ParseInt(string? value) => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
    private static bool ParseBool(string? value) => bool.TryParse(value, out var parsed) && parsed;
    private static long ParseHex(string? value) => long.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;

    private static void RemoveNulls(JsonObject value)
    {
        foreach (var key in value.Where(pair => pair.Value is null).Select(pair => pair.Key).ToArray()) value.Remove(key);
    }
    private static JsonArray Array(GraphNode node, string name) => new(node.Properties.Where(property => property.Name == name)
        .Select(property => property.Value).Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal)
        .Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
    private static string? Property(GraphNode node, string name) => node.Properties.FirstOrDefault(property => property.Name == name)?.Value;
    private static string[] Properties(GraphNode node, string name) => node.Properties.Where(property => property.Name == name)
        .Select(property => property.Value).Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray();
    private static string CompatibleId(string prefix, string projectId, string sourceId) => StableIdentity.Create(prefix, AdapterVersion, projectId, sourceId);
    private static string CanonicalKind(string? value) => StableIdentity.Normalize(value).Replace(' ', '-');
    internal static bool ValidProjectId(string value) => value.Length is >= 1 and <= 128 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');
    private readonly record struct FrameIdentity(string BundleId, long FrameSequence);
}

public static class UiAtlasVNextCompatibilityValidator
{
    private const int MaxCoordinate = 1_000_000;
    private static readonly HashSet<string> RootNames = new(["schemaVersion", "kind", "projectId", "generatedUtc", "UiAtlasCore", "buildRevision", "authoring", "observationPackages"], StringComparer.Ordinal);
    private static readonly HashSet<string> RootRequiredNames = new(["schemaVersion", "kind", "projectId", "generatedUtc", "UiAtlasCore", "buildRevision", "authoring"], StringComparer.Ordinal);
    private static readonly HashSet<string> PublicationNames = new(["adapterVersion", "privacyProfile", "sourceFormatVersion", "sourceSemanticHash"], StringComparer.Ordinal);
    private static readonly HashSet<string> RevisionNames = new(["buildId", "projectId", "generatedUtc", "sourcePackageCount"], StringComparer.Ordinal);
    private static readonly HashSet<string> AuthoringNames = new(["applications", "windows", "controls"], StringComparer.Ordinal);
    private static readonly HashSet<string> ApplicationNames = new(["applicationId", "name", "inTwinDefault"], StringComparer.Ordinal);
    private static readonly HashSet<string> WindowNames = new(["windowId", "sceneId", "applicationId", "kind", "title", "mode", "chromeMode", "x", "y", "width", "height", "isModal", "showInTaskbar", "alwaysOnTop", "defaultZIndex", "processName", "className", "originalFilename", "productVersion", "companyName", "productName", "semanticSurfaceId", "semanticSurfaceKind", "semanticSurfaceDisplay", "semanticParentSurfaceId", "semanticPopupFamilyId", "semanticPopupVariantId"], StringComparer.Ordinal);
    private static readonly HashSet<string> ControlNames = new(["controlId", "controlKey", "sceneId", "windowId", "controlType", "role", "label", "canonicalKind", "supportedPatterns", "stableSelectors", "identity", "structure", "x", "y", "width", "height", "verificationStatus", "confirmationSource", "geometrySource", "interactionMethod", "actionVerificationStatus", "safeForAutoExplore"], StringComparer.Ordinal);
    private static readonly HashSet<string> IdentityNames = new(["automationId", "name", "className", "frameworkId", "semanticSurfaceId", "semanticSurfaceKind"], StringComparer.Ordinal);
    private static readonly HashSet<string> StructureNames = new(["parentControlId", "partOfWindow", "containerPath"], StringComparer.Ordinal);
    private static readonly HashSet<string> PackageNames = new(["packageId", "kind", "displayName", "variantId", "sceneId", "sourceSessionId", "recordingSessionKey", "source", "confidence", "observedUtc", "captureReason", "materializationStatus", "buildRevisionId", "artifacts", "streams"], StringComparer.Ordinal);
    private static readonly HashSet<string> ArtifactNames = new(["artifactId", "kind", "path", "mediaType", "payloadJson"], StringComparer.Ordinal);
    private static readonly HashSet<string> StreamNames = new(["streamId", "kind", "status", "source", "confidence", "observedUtc", "artifactIds"], StringComparer.Ordinal);
    private static readonly HashSet<string> ObservationNames = new(["schemaVersion", "sessionId", "frameIndex", "timestampUtc", "observationCapturedUtc", "captureMode", "pngFileName", "captureReason", "imageWidth", "imageHeight", "captureRectScreen", "screenToImageScaleX", "screenToImageScaleY", "windows", "automationControls", "visionControls", "textRegions", "triggerKind", "inputEventIds"], StringComparer.Ordinal);
    private static readonly HashSet<string> ObservationWindowNames = new(["zOrder", "hwndHex", "processId", "processName", "title", "className", "boundsScreen", "boundsImagePx", "isVisible", "isCloaked", "isMinimized", "isToolWindow", "isTopMost", "style", "exStyle", "resolvedWindowAnchor", "windowAnchorAliases"], StringComparer.Ordinal);
    private static readonly HashSet<string> ObservationControlNames = new(["windowHwndHex", "windowProcessId", "windowProcessName", "effectiveWindowKey", "ownerResolutionKind", "automationId", "runtimeId", "name", "className", "frameworkId", "controlType", "localizedControlType", "boundsScreen", "boundsImagePx", "geometrySource", "geometryConfidence", "isEnabled", "isOffscreen", "supportedPatterns", "identity", "relationships"], StringComparer.Ordinal);
    private static readonly HashSet<string> ObservationIdentityNames = new(["processId", "isControlElement", "isContentElement"], StringComparer.Ordinal);
    private static readonly HashSet<string> ObservationRelationshipNames = new(["containerPath", "controlViewPath", "contentViewPath", "rawViewPath", "rawViewIdentityPath", "nativeWindowLineage", "parent"], StringComparer.Ordinal);
    private static readonly HashSet<string> ObservationParentNames = new(["runtimeId", "summary"], StringComparer.Ordinal);
    private static readonly HashSet<string> RectNames = new(["x", "y", "width", "height"], StringComparer.Ordinal);

    public static ValidationReport ValidateFile(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            RejectReparsePath(full);
            using var input = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length is <= 0 or > 256L * 1024 * 1024) return Invalid("compat.size", "$", "Compatibility JSON is empty or exceeds the size limit.");
            var bytes = new byte[checked((int)input.Length)];
            input.ReadExactly(bytes);
            var report = Validate(bytes);
            if (!report.IsValid) return report;
            var sidecar = full + ".sha256";
            RejectReparsePath(sidecar);
            using var checksum = new FileStream(sidecar, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (checksum.Length is <= 0 or > 1_024) return Invalid("compat.hash", "$", "Compatibility checksum is invalid or exceeds the size limit.");
            var checksumBytes = new byte[checked((int)checksum.Length)];
            checksum.ReadExactly(checksumBytes);
            var line = System.Text.Encoding.UTF8.GetString(checksumBytes).Trim();
            var expected = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            var actual = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            return expected?.Length == 64 && expected.All(Uri.IsHexDigit) && string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase)
                ? report : Invalid("compat.hash", "$", "Compatibility checksum does not match.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            return Invalid("compat.read", "$", "Compatibility publication could not be read safely.");
        }
    }

    private static void RejectReparsePath(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Compatibility publication file is missing.");
        for (FileSystemInfo? item = new FileInfo(Path.GetFullPath(path)); item is not null; item = item switch
        {
            FileInfo file => file.Directory,
            DirectoryInfo directory => directory.Parent,
            _ => null
        })
            if ((item.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Compatibility publication paths cannot contain links or reparse points.");
    }

    public static ValidationReport Validate(byte[] bytes)
    {
        try
        {
            if (bytes.Length is <= 0 or > 256 * 1024 * 1024) return Invalid("compat.size", "$", "Compatibility JSON is empty or exceeds the size limit.");
            StrictJsonValidator.Validate(bytes);
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            var issues = new List<ValidationIssue>();
            RequireObject(root, RootNames, "$", issues);
            RequireMembers(root, RootRequiredNames, "$", issues);
            if (issues.Count > 0) return new(false, issues);
            if (Integer(root, "schemaVersion") != 5 || Text(root, "kind") != "uikg/vnext") issues.Add(Issue("compat.version", "$", "Compatibility version or kind is unsupported."));
            var projectId = Text(root, "projectId");
            if (!UiAtlasVNextCompatibilityExporter.ValidProjectId(projectId)) issues.Add(Issue("compat.project", "projectId", "Project identifier is invalid."));
            if (!Date(root, "generatedUtc")) issues.Add(Issue("compat.time", "generatedUtc", "Generation time is invalid."));
            var publication = root.GetProperty("UiAtlasCore");
            RequireObject(publication, PublicationNames, "UiAtlasCore", issues);
            RequireMembers(publication, PublicationNames, "UiAtlasCore", issues);
            if (Text(publication, "adapterVersion") != UiAtlasVNextCompatibilityExporter.AdapterVersion ||
                Text(publication, "privacyProfile") != UiAtlasVNextCompatibilityExporter.PrivacyProfile ||
                Text(publication, "sourceFormatVersion") is not (FormatVersions.Graph or FormatVersions.PreviousGraph) ||
                !Hash(Text(publication, "sourceSemanticHash")))
                issues.Add(Issue("compat.profile", "UiAtlasCore", "Compatibility profile declaration is invalid."));
            var revision = root.GetProperty("buildRevision");
            RequireObject(revision, RevisionNames, "buildRevision", issues);
            RequireMembers(revision, RevisionNames, "buildRevision", issues);
            var packageCount = root.TryGetProperty("observationPackages", out var packageArray) && packageArray.ValueKind == JsonValueKind.Array
                ? packageArray.GetArrayLength()
                : 0;
            if (!Id(Text(revision, "buildId")) || Text(revision, "projectId") != projectId || Integer(revision, "sourcePackageCount") != packageCount || !Date(revision, "generatedUtc"))
                issues.Add(Issue("compat.build", "buildRevision", "Build revision is invalid."));
            var authoring = root.GetProperty("authoring");
            RequireObject(authoring, AuthoringNames, "authoring", issues);
            RequireMembers(authoring, AuthoringNames, "authoring", issues);
            if (issues.Count > 0) return new(false, issues);
            var applications = Array(authoring, "applications", 1, 10_000, issues);
            var windows = Array(authoring, "windows", 1, 100_000, issues);
            var controls = Array(authoring, "controls", 0, 100_000, issues);
            if (root.TryGetProperty("observationPackages", out packageArray))
                ValidateObservationPackages(packageArray, Text(revision, "buildId"), issues);
            if (issues.Count > 0) return new(false, issues);
            var allIds = new HashSet<string>(StringComparer.Ordinal);
            var appIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var value in applications.EnumerateArray())
            {
                RequireObject(value, ApplicationNames, "authoring.applications", issues);
                RequireMembers(value, ApplicationNames, "authoring.applications", issues);
                var id = Text(value, "applicationId");
                if (!AddId(id, allIds) || !appIds.Add(id)) issues.Add(Issue("compat.duplicate-id", id, "Application identifier is invalid or collides."));
                BoundedText(value, "name", 4_096, true, issues);
                if (!Boolean(value, "inTwinDefault")) issues.Add(Issue("compat.type", id, "Application boolean is invalid."));
            }
            var windowIds = new HashSet<string>(StringComparer.Ordinal);
            var windowKinds = new Dictionary<string, string>(StringComparer.Ordinal);
            var parentByWindow = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var value in windows.EnumerateArray())
            {
                RequireObject(value, WindowNames, "authoring.windows", issues);
                RequireMembers(value, new(["windowId", "sceneId", "applicationId", "kind", "title", "mode", "chromeMode", "x", "y", "width", "height", "isModal", "showInTaskbar", "alwaysOnTop", "defaultZIndex", "semanticSurfaceId", "semanticSurfaceKind", "semanticSurfaceDisplay"]), "authoring.windows", issues);
                var id = Text(value, "windowId");
                if (!AddId(id, allIds) || !windowIds.Add(id)) issues.Add(Issue("compat.duplicate-id", id, "Window identifier is invalid or collides."));
                if (!appIds.Contains(Text(value, "applicationId"))) issues.Add(Issue("compat.reference", id, "Window application reference is missing."));
                foreach (var name in new[] { "kind", "title", "mode", "chromeMode", "semanticSurfaceId", "semanticSurfaceKind", "semanticSurfaceDisplay" })
                    BoundedText(value, name, 4_096, true, issues);
                foreach (var name in new[] { "processName", "className", "originalFilename", "productVersion", "companyName", "productName", "semanticParentSurfaceId", "semanticPopupFamilyId", "semanticPopupVariantId" })
                    if (value.TryGetProperty(name, out _)) BoundedText(value, name, 4_096, false, issues);
                foreach (var name in new[] { "semanticParentSurfaceId", "semanticPopupFamilyId", "semanticPopupVariantId" })
                    if (value.TryGetProperty(name, out _) && !Id(Text(value, name))) issues.Add(Issue("compat.type", id, "Window optional identifier is invalid."));
                if (Integer(value, "sceneId") != 0 || Integer(value, "defaultZIndex") == int.MinValue)
                    issues.Add(Issue("compat.type", id, "Window integer is invalid."));
                foreach (var name in new[] { "isModal", "showInTaskbar", "alwaysOnTop" })
                    if (!Boolean(value, name)) issues.Add(Issue("compat.type", id, "Window boolean is invalid."));
                if (Text(value, "semanticSurfaceId") != id) issues.Add(Issue("compat.reference", id, "Window semantic identifier is inconsistent."));
                windowKinds[id] = Text(value, "semanticSurfaceKind");
                parentByWindow[id] = value.TryGetProperty("semanticParentSurfaceId", out var parent) && parent.ValueKind == JsonValueKind.String ? parent.GetString() : null;
                Bounds(value, id, issues);
            }
            foreach (var pair in parentByWindow)
                if (pair.Value is not null && !windowIds.Contains(pair.Value)) issues.Add(Issue("compat.reference", pair.Key, "Window parent reference is missing."));
            ValidateAcyclic(parentByWindow, issues);
            var controlIds = new HashSet<string>(StringComparer.Ordinal);
            var parentByControl = new Dictionary<string, string?>(StringComparer.Ordinal);
            var windowByControl = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var value in controls.EnumerateArray())
            {
                RequireObject(value, ControlNames, "authoring.controls", issues);
                RequireMembers(value, new(["controlId", "controlKey", "sceneId", "windowId", "controlType", "role", "label", "canonicalKind", "supportedPatterns", "stableSelectors", "identity", "structure", "x", "y", "width", "height"]), "authoring.controls", issues);
                var id = Text(value, "controlId");
                if (!AddId(id, allIds) || !controlIds.Add(id)) issues.Add(Issue("compat.duplicate-id", id, "Control identifier is invalid or collides."));
                var windowId = Text(value, "windowId");
                if (!windowIds.Contains(windowId)) issues.Add(Issue("compat.reference", id, "Control window reference is missing."));
                foreach (var name in new[] { "controlKey", "controlType", "role", "label", "canonicalKind" })
                    BoundedText(value, name, 4_096, true, issues);
                foreach (var name in new[] { "verificationStatus", "confirmationSource", "geometrySource", "interactionMethod", "actionVerificationStatus" })
                    if (value.TryGetProperty(name, out _)) BoundedText(value, name, 4_096, false, issues);
                if (value.TryGetProperty("safeForAutoExplore", out _) && !Boolean(value, "safeForAutoExplore"))
                    issues.Add(Issue("compat.type", id, "Control safety flag is invalid."));
                if (Text(value, "controlKey") != id || Integer(value, "sceneId") != 0)
                    issues.Add(Issue("compat.type", id, "Control identity or scene is invalid."));
                Bounds(value, id, issues);
                StringArray(value, "supportedPatterns", 128, 512, issues);
                StringArray(value, "stableSelectors", 128, 4_096, issues);
                if (!value.TryGetProperty("identity", out var identity)) issues.Add(Issue("compat.required", id, "Control identity is missing."));
                else
                {
                    RequireObject(identity, IdentityNames, id + ".identity", issues);
                    RequireMembers(identity, new(["semanticSurfaceId", "semanticSurfaceKind"]), id + ".identity", issues);
                    foreach (var name in IdentityNames) if (identity.TryGetProperty(name, out _)) BoundedText(identity, name, 4_096, false, issues);
                    if (Text(identity, "semanticSurfaceId") != windowId || !windowKinds.TryGetValue(windowId, out var windowKind) || Text(identity, "semanticSurfaceKind") != windowKind)
                        issues.Add(Issue("compat.reference", id, "Control semantic surface is inconsistent."));
                }
                string? parent = null;
                if (!value.TryGetProperty("structure", out var structure)) issues.Add(Issue("compat.required", id, "Control structure is missing."));
                else
                {
                    RequireObject(structure, StructureNames, id + ".structure", issues);
                    RequireMembers(structure, new(["partOfWindow", "containerPath"]), id + ".structure", issues);
                    if (structure.TryGetProperty("parentControlId", out var parentValue)) parent = parentValue.GetString();
                    if (Text(structure, "partOfWindow") != windowId) issues.Add(Issue("compat.reference", id, "Control structure window is inconsistent."));
                    if (structure.TryGetProperty("containerPath", out _)) BoundedText(structure, "containerPath", 4_096, false, issues);
                }
                parentByControl[id] = parent;
                windowByControl[id] = windowId;
            }
            foreach (var pair in parentByControl)
                if (pair.Value is not null && (!controlIds.Contains(pair.Value) || !windowByControl.TryGetValue(pair.Value, out var parentWindow) || parentWindow != windowByControl[pair.Key]))
                    issues.Add(Issue("compat.reference", pair.Key, "Parent control reference is missing or crosses a window boundary."));
            ValidateAcyclic(parentByControl, issues);
            return new(!issues.Any(issue => issue.Severity == "error"), issues);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or InvalidOperationException or KeyNotFoundException)
        {
            return Invalid("compat.json", "$", "Compatibility JSON is malformed or violates strict rules.");
        }
    }

    private static void RequireObject(JsonElement value, HashSet<string> allowed, string path, List<ValidationIssue> issues)
    {
        if (value.ValueKind != JsonValueKind.Object) { issues.Add(Issue("compat.required", path, "Object is required.")); return; }
        foreach (var property in value.EnumerateObject()) if (!allowed.Contains(property.Name)) issues.Add(Issue("compat.member", path, "Unsupported member is present."));
    }

    private static void ValidateObservationPackages(JsonElement packages, string expectedBuildId, List<ValidationIssue> issues)
    {
        if (packages.ValueKind != JsonValueKind.Array || packages.GetArrayLength() > 10_000)
        {
            issues.Add(Issue("compat.count-limit", "observationPackages", "Observation packages are invalid or exceed count limits."));
            return;
        }
        var packageIds = new HashSet<string>(StringComparer.Ordinal);
        var artifactIds = new HashSet<string>(StringComparer.Ordinal);
        var streamIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var package in packages.EnumerateArray())
        {
            if (package.ValueKind != JsonValueKind.Object)
            {
                issues.Add(Issue("compat.type", "observationPackages", "Observation package must be an object."));
                continue;
            }
            RequireObject(package, PackageNames, "observationPackages", issues);
            RequireMembers(package, PackageNames, "observationPackages", issues);
            var packageId = Text(package, "packageId");
            if (!Id(packageId) || !packageIds.Add(packageId)) issues.Add(Issue("compat.duplicate-id", packageId, "Observation package identifier is invalid or collides."));
            foreach (var name in new[] { "kind", "displayName", "variantId", "sourceSessionId", "recordingSessionKey", "source", "captureReason", "materializationStatus", "buildRevisionId" })
                BoundedText(package, name, 4_096, true, issues);
            var sourceSessionId = Text(package, "sourceSessionId");
            var sceneId = Integer(package, "sceneId");
            if (Text(package, "kind") != "observation_package.variant" || !Id(Text(package, "variantId")) ||
                !Id(Text(package, "recordingSessionKey")) || !Id(Text(package, "buildRevisionId")) ||
                !Guid.TryParseExact(sourceSessionId, "D", out _) || sceneId < 1 ||
                !Date(package, "observedUtc") || !UnitInterval(package, "confidence"))
                issues.Add(Issue("compat.package", packageId, "Observation package identity, time, or confidence is invalid."));
            if (Text(package, "source") != "manual-recording" || Text(package, "captureReason") != "ManualMapStep" ||
                Text(package, "materializationStatus") != "reproducible" || Text(package, "buildRevisionId") != expectedBuildId)
                issues.Add(Issue("compat.lineage", packageId, "Observation package lineage or capture profile is inconsistent."));
            if (!package.TryGetProperty("artifacts", out var artifacts) || artifacts.ValueKind != JsonValueKind.Array)
            {
                issues.Add(Issue("compat.count-limit", packageId, "Observation package must contain exactly one observation artifact."));
                continue;
            }
            if (artifacts.GetArrayLength() != 1)
                issues.Add(Issue("compat.count-limit", packageId, "Observation package must contain exactly one observation artifact."));
            var localArtifactIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var artifact in artifacts.EnumerateArray())
            {
                if (artifact.ValueKind != JsonValueKind.Object) { issues.Add(Issue("compat.type", packageId, "Observation artifact must be an object.")); continue; }
                RequireObject(artifact, ArtifactNames, packageId + ".artifacts", issues);
                RequireMembers(artifact, ArtifactNames, packageId + ".artifacts", issues);
                foreach (var name in new[] { "artifactId", "kind", "path", "mediaType" }) BoundedText(artifact, name, 4_096, true, issues);
                var artifactId = Text(artifact, "artifactId");
                if (!Id(artifactId) || !artifactIds.Add(artifactId) || !localArtifactIds.Add(artifactId))
                    issues.Add(Issue("compat.duplicate-id", packageId, "Observation artifact identifier is invalid or collides."));
                if (Text(artifact, "kind") != "artifact.observation_entry" || Text(artifact, "mediaType") != "application/json")
                    issues.Add(Issue("compat.artifact", packageId, "Observation artifact kind or media type is unsupported."));
                if (!artifact.TryGetProperty("payloadJson", out var payload) || payload.ValueKind != JsonValueKind.String || (payload.GetString()?.Length ?? 0) > 16 * 1024 * 1024)
                {
                    issues.Add(Issue("compat.length", packageId, "Observation artifact payload is missing or exceeds limits."));
                    continue;
                }
                try
                {
                    var nestedBytes = Encoding.UTF8.GetBytes(payload.GetString()!);
                    StrictJsonValidator.Validate(nestedBytes);
                    using var nested = JsonDocument.Parse(nestedBytes, new JsonDocumentOptions { MaxDepth = 64 });
                    ValidateObservationPayload(nested.RootElement, Text(artifact, "path"), packageId, sourceSessionId, sceneId, issues);
                }
                catch (JsonException) { issues.Add(Issue("compat.json", packageId, "Observation artifact payload is malformed.")); }
            }
            if (!package.TryGetProperty("streams", out var streams) || streams.ValueKind != JsonValueKind.Array)
            {
                issues.Add(Issue("compat.count-limit", packageId, "Observation package must contain exactly two capture streams."));
                continue;
            }
            if (streams.GetArrayLength() != 2)
                issues.Add(Issue("compat.count-limit", packageId, "Observation package must contain exactly two capture streams."));
            var observedKinds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var stream in streams.EnumerateArray())
            {
                if (stream.ValueKind != JsonValueKind.Object) { issues.Add(Issue("compat.type", packageId, "Observation stream must be an object.")); continue; }
                RequireObject(stream, StreamNames, packageId + ".streams", issues);
                RequireMembers(stream, StreamNames, packageId + ".streams", issues);
                var streamId = Text(stream, "streamId");
                var kind = Text(stream, "kind");
                if (!Id(streamId) || !streamIds.Add(streamId)) issues.Add(Issue("compat.duplicate-id", packageId, "Observation stream identifier is invalid or collides."));
                if (kind is not ("stream.win32" or "stream.uia") || !observedKinds.Add(kind) ||
                    Text(stream, "status") != "captured" || Text(stream, "source") != "manual" ||
                    !Date(stream, "observedUtc") || !UnitInterval(stream, "confidence"))
                    issues.Add(Issue("compat.stream", packageId, "Observation stream contract is invalid."));
                if (!stream.TryGetProperty("artifactIds", out var references) || references.ValueKind != JsonValueKind.Array || references.GetArrayLength() is < 1 or > 64)
                {
                    issues.Add(Issue("compat.reference", packageId, "Observation stream artifact references are invalid."));
                    continue;
                }
                foreach (var reference in references.EnumerateArray())
                    if (reference.ValueKind != JsonValueKind.String || !localArtifactIds.Contains(reference.GetString() ?? string.Empty))
                        issues.Add(Issue("compat.reference", packageId, "Observation stream artifact reference is missing."));
            }
            if (!observedKinds.SetEquals(["stream.win32", "stream.uia"]))
                issues.Add(Issue("compat.stream", packageId, "Required Win32 and UI Automation streams are missing."));
        }
    }

    private static void ValidateObservationPayload(JsonElement observation, string artifactPath, string packageId, string expectedSessionId, int expectedFrame, List<ValidationIssue> issues)
    {
        RequireObject(observation, ObservationNames, packageId + ".payload", issues);
        RequireMembers(observation, ObservationNames, packageId + ".payload", issues);
        var frame = Integer(observation, "frameIndex");
        var expectedObservationPath = frame > 0 ? $"raw/observations/frame-{frame:D6}.json" : string.Empty;
        var screenshot = Text(observation, "pngFileName");
        if (Integer(observation, "schemaVersion") != 9 || frame is < 1 or > 1_000_000 ||
            !string.Equals(artifactPath, expectedObservationPath, StringComparison.Ordinal) ||
            !Guid.TryParseExact(Text(observation, "sessionId"), "D", out _) || Text(observation, "sessionId") != expectedSessionId || frame != expectedFrame ||
            !Date(observation, "timestampUtc") ||
            !Date(observation, "observationCapturedUtc") || Text(observation, "captureMode") != "manual_map_step" ||
            Text(observation, "captureReason") != "ManualMapStep" || Text(observation, "triggerKind") != "manual" ||
            (screenshot.Length > 0 && !string.Equals(screenshot, $"raw/frames/frame-{frame:D6}.png", StringComparison.Ordinal)))
            issues.Add(Issue("compat.observation", packageId, "Observation payload identity, path, time, or capture profile is invalid."));
        var width = Integer(observation, "imageWidth");
        var height = Integer(observation, "imageHeight");
        if (width is < 1 or > 16_384 || height is < 1 or > 16_384 || (long)width * height > 16_000_000 ||
            !PositiveNumber(observation, "screenToImageScaleX") || !PositiveNumber(observation, "screenToImageScaleY"))
            issues.Add(Issue("compat.bounds", packageId, "Observation image dimensions or scale are invalid."));
        ValidateRectProperty(observation, "captureRectScreen", packageId, issues);

        var windows = RequiredArray(observation, "windows", 1, RecordingContractLimits.MaxScopedWindows, packageId, issues);
        foreach (var window in windows)
        {
            RequireObject(window, ObservationWindowNames, packageId + ".windows", issues);
            RequireMembers(window, ObservationWindowNames, packageId + ".windows", issues);
            foreach (var name in new[] { "hwndHex", "processName", "title", "className", "resolvedWindowAnchor" }) BoundedText(window, name, 4_096, true, issues);
            if (Text(window, "hwndHex").Length != 0 || Integer(window, "processId") != 0 || !Id(Text(window, "resolvedWindowAnchor")) ||
                Integer(window, "zOrder") == int.MinValue || !Int64(window, "style") || !Int64(window, "exStyle"))
                issues.Add(Issue("compat.window", packageId, "Observation window identity or numeric fields are invalid."));
            foreach (var name in new[] { "isVisible", "isCloaked", "isMinimized", "isToolWindow", "isTopMost" })
                if (!Boolean(window, name)) issues.Add(Issue("compat.type", packageId, "Observation window boolean is invalid."));
            ValidateRectProperty(window, "boundsScreen", packageId, issues);
            ValidateRectProperty(window, "boundsImagePx", packageId, issues);
            StringArray(window, "windowAnchorAliases", 32, 4_096, issues);
        }

        var controls = RequiredArray(observation, "automationControls", 0, 12_000, packageId, issues);
        foreach (var control in controls)
        {
            RequireObject(control, ObservationControlNames, packageId + ".automationControls", issues);
            RequireMembers(control, ObservationControlNames, packageId + ".automationControls", issues);
            foreach (var name in new[] { "windowHwndHex", "windowProcessName", "effectiveWindowKey", "ownerResolutionKind", "automationId", "runtimeId", "name", "className", "frameworkId", "controlType", "localizedControlType", "geometrySource" })
                BoundedText(control, name, 4_096, true, issues);
            if (Text(control, "windowHwndHex").Length != 0 || Integer(control, "windowProcessId") != 0 ||
                !Id(Text(control, "effectiveWindowKey")) || Text(control, "runtimeId").Length == 0 ||
                !UnitInterval(control, "geometryConfidence") || !Boolean(control, "isEnabled") || !Boolean(control, "isOffscreen"))
                issues.Add(Issue("compat.control", packageId, "Observation control identity, state, or confidence is invalid."));
            ValidateRectProperty(control, "boundsScreen", packageId, issues);
            ValidateRectProperty(control, "boundsImagePx", packageId, issues);
            StringArray(control, "supportedPatterns", 128, 512, issues);
            if (!control.TryGetProperty("identity", out var identity)) issues.Add(Issue("compat.required", packageId, "Observation control identity is missing."));
            else
            {
                RequireObject(identity, ObservationIdentityNames, packageId + ".identity", issues);
                RequireMembers(identity, ObservationIdentityNames, packageId + ".identity", issues);
                if (Integer(identity, "processId") != 0 || !Boolean(identity, "isControlElement") || !Boolean(identity, "isContentElement"))
                    issues.Add(Issue("compat.type", packageId, "Observation control identity values are invalid."));
            }
            if (!control.TryGetProperty("relationships", out var relationships)) issues.Add(Issue("compat.required", packageId, "Observation control relationships are missing."));
            else
            {
                RequireObject(relationships, ObservationRelationshipNames, packageId + ".relationships", issues);
                RequireMembers(relationships, new(["containerPath", "controlViewPath", "contentViewPath", "rawViewPath", "rawViewIdentityPath", "nativeWindowLineage"]), packageId + ".relationships", issues);
                foreach (var name in new[] { "containerPath", "controlViewPath", "contentViewPath", "rawViewPath", "rawViewIdentityPath" }) StringArray(relationships, name, 128, 4_096, issues);
                BoundedText(relationships, "nativeWindowLineage", 4_096, true, issues);
                if (relationships.TryGetProperty("parent", out var parent))
                {
                    RequireObject(parent, ObservationParentNames, packageId + ".parent", issues);
                    RequireMembers(parent, ObservationParentNames, packageId + ".parent", issues);
                    BoundedText(parent, "runtimeId", 4_096, true, issues);
                    BoundedText(parent, "summary", 4_096, true, issues);
                }
            }
        }
        foreach (var name in new[] { "visionControls", "textRegions", "inputEventIds" })
            if (!observation.TryGetProperty(name, out var empty) || empty.ValueKind != JsonValueKind.Array || empty.GetArrayLength() != 0)
                issues.Add(Issue("compat.count-limit", packageId, "Unsupported observation payload collection is not empty."));
    }
    private static void RequireMembers(JsonElement value, HashSet<string> required, string path, List<ValidationIssue> issues)
    {
        if (value.ValueKind != JsonValueKind.Object) return;
        foreach (var name in required) if (!value.TryGetProperty(name, out _)) issues.Add(Issue("compat.required", path, "Required member is missing."));
    }
    private static JsonElement Array(JsonElement owner, string name, int min, int max, List<ValidationIssue> issues)
    {
        if (!owner.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array || value.GetArrayLength() < min || value.GetArrayLength() > max)
            issues.Add(Issue("compat.count-limit", name, "Array is missing or exceeds count limits."));
        return value;
    }
    private static void Bounds(JsonElement value, string path, List<ValidationIssue> issues)
    {
        foreach (var name in new[] { "x", "y", "width", "height" })
            if (!value.TryGetProperty(name, out var number) || !number.TryGetInt32(out var parsed) || Math.Abs((long)parsed) > MaxCoordinate || (name is "width" or "height") && parsed < 0)
                issues.Add(Issue("compat.bounds", path, "Bounds are invalid."));
    }
    private static void ValidateRectProperty(JsonElement owner, string name, string path, List<ValidationIssue> issues)
    {
        if (!owner.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object)
        {
            issues.Add(Issue("compat.bounds", path, "Required bounds object is missing."));
            return;
        }
        RequireObject(value, RectNames, path + "." + name, issues);
        RequireMembers(value, RectNames, path + "." + name, issues);
        Bounds(value, path + "." + name, issues);
    }
    private static IEnumerable<JsonElement> RequiredArray(JsonElement owner, string name, int min, int max, string path, List<ValidationIssue> issues)
    {
        if (!owner.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array || value.GetArrayLength() < min || value.GetArrayLength() > max)
        {
            issues.Add(Issue("compat.count-limit", path, "Required nested collection is missing or exceeds limits."));
            return [];
        }
        return value.EnumerateArray().ToArray();
    }
    private static bool UnitInterval(JsonElement owner, string name) =>
        owner.TryGetProperty(name, out var value) && value.TryGetDouble(out var number) && double.IsFinite(number) && number is >= 0 and <= 1;
    private static bool PositiveNumber(JsonElement owner, string name) =>
        owner.TryGetProperty(name, out var value) && value.TryGetDouble(out var number) && double.IsFinite(number) && number > 0;
    private static bool Int64(JsonElement owner, string name) =>
        owner.TryGetProperty(name, out var value) && value.TryGetInt64(out _);
    private static void StringArray(JsonElement owner, string name, int maxCount, int maxLength, List<ValidationIssue> issues)
    {
        if (!owner.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > maxCount) { issues.Add(Issue("compat.count-limit", name, "String array is invalid.")); return; }
        foreach (var item in value.EnumerateArray()) if (item.ValueKind != JsonValueKind.String || (item.GetString()?.Length ?? 0) > maxLength) issues.Add(Issue("compat.length", name, "String array value is invalid."));
    }
    private static void BoundedText(JsonElement owner, string name, int max, bool required, List<ValidationIssue> issues)
    {
        if (!owner.TryGetProperty(name, out var value)) { if (required) issues.Add(Issue("compat.required", name, "String is required.")); return; }
        if (value.ValueKind != JsonValueKind.String || (value.GetString()?.Length ?? 0) > max) issues.Add(Issue("compat.length", name, "String is invalid or exceeds limits."));
    }
    private static void ValidateAcyclic(Dictionary<string, string?> parents, List<ValidationIssue> issues)
    {
        foreach (var id in parents.Keys)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal) { id };
            var current = id;
            for (var depth = 0; depth <= 64 && parents.TryGetValue(current, out var parent) && parent is not null; depth++)
            {
                if (!seen.Add(parent)) { issues.Add(Issue("compat.cycle", id, "Control hierarchy contains a cycle.")); break; }
                current = parent;
                if (depth == 64) issues.Add(Issue("compat.depth", id, "Control hierarchy exceeds depth limit."));
            }
        }
    }
    private static bool AddId(string value, HashSet<string> ids) => Id(value) && ids.Add(value.Normalize(NormalizationForm.FormKC).ToUpperInvariant());
    private static bool Id(string value)
    {
        var separator = value.LastIndexOf('_');
        return separator > 0 && value.Length - separator - 1 == 24 &&
            value[..separator].All(character => character is >= 'a' and <= 'z' or '-') &&
            value[(separator + 1)..].All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    }
    private static bool Hash(string value) => value.Length == 64 && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static string Text(JsonElement owner, string name) => owner.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;
    private static int Integer(JsonElement owner, string name) => owner.TryGetProperty(name, out var value) && value.TryGetInt32(out var parsed) ? parsed : int.MinValue;
    private static bool Boolean(JsonElement owner, string name) => owner.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False;
    private static bool Date(JsonElement owner, string name) => owner.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.TryGetDateTime(out _);
    private static ValidationIssue Issue(string code, string path, string message) => new(code, "error", path, message);
    private static ValidationReport Invalid(string code, string path, string message) => new(false, [Issue(code, path, message)]);
}
