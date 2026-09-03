using System.Globalization;
using System.Text;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Recording;

namespace UiAtlas.Core.Build;

public sealed class RecordingGraphBuilder
{
    private const string RawDataStreamsLayer = "raw-data-streams";
    private const string RawLayer = "raw-world";
    private const string SemanticLayer = "semantic-world";

    public UiKnowledgeGraph Build(string bundlePath) => Build(new[] { bundlePath });

    public UiKnowledgeGraph Build(IEnumerable<string> bundlePaths, string? logicalMapId = null)
    {
        ArgumentNullException.ThrowIfNull(bundlePaths);
        var inputs = bundlePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(LoadBundleFrames)
            .ToArray();
        if (inputs.Length == 0)
            throw new InvalidDataException("At least one recording bundle is required.");
        return BuildValidated(inputs, logicalMapId);
    }

    public UiKnowledgeGraph Build(RecordingGraphInput input, string? logicalMapId = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        return Build(new[] { input }, logicalMapId);
    }

    public UiKnowledgeGraph Build(IEnumerable<RecordingGraphInput> inputs, string? logicalMapId = null)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        var values = inputs.ToArray();
        if (values.Length == 0)
            throw new InvalidDataException("At least one recording graph input is required.");
        foreach (var input in values)
            RecordingGraphInputValidator.ThrowIfInvalid(input);
        return BuildValidated(values, logicalMapId);
    }

    private static UiKnowledgeGraph BuildValidated(
        IReadOnlyList<RecordingGraphInput> inputs,
        string? logicalMapId)
    {
        var primaryManifest = inputs[0].Manifest;

        var nodes = new Dictionary<string, MutableNode>(StringComparer.Ordinal);
        var edges = new Dictionary<string, MutableEdge>(StringComparer.Ordinal);
        var rawSurfaces = new Dictionary<string, RawSurfaceInfo>(StringComparer.Ordinal);
        var rawControls = new Dictionary<string, RawControlInfo>(StringComparer.Ordinal);
        var previousVariantBySurface = new Dictionary<string, string>(StringComparer.Ordinal);
        var rawStateByFrameSurface = new Dictionary<string, string>(StringComparer.Ordinal);
        var appKey = StableIdentity.Normalize(primaryManifest.Target.ProcessName);
        var appId = StableIdentity.Create("app", appKey);
        var app = GetNode(nodes, appId, GraphNodeKind.Application, "", appId, primaryManifest.Target.ProcessName);
        app.AddProperty("layer", "shared");
        foreach (var input in inputs)
        {
            var manifest = input.Manifest;
            var rawSurfaceByNativeWindow = new Dictionary<long, RawSurfaceInfo>();
            var curatedObservations = SuppressRecordedVisualDuplicates(
                CarryForwardStableNativeChrome(SuppressDuplicateNativeCaptionButtons(input.Observations)));
            app.AddProperty("processName", manifest.Target.ProcessName, sensitive: true);
            app.AddProperty("productVersion", manifest.Target.ProductVersion);
            app.AddProperty("originalFilename", manifest.Target.OriginalFilename, sensitive: true);
            app.AddProperty("companyName", manifest.Target.CompanyName, sensitive: true);
            app.AddProperty("productName", manifest.Target.ProductName, sensitive: true);

            foreach (var frame in curatedObservations)
            {
                var screenVariantFrame = IsScreenVariantFrame(frame);
                var windows = (frame.ScopedWindows is { Count: > 0 } ? frame.ScopedWindows : [frame.Window])
                    .OrderBy(window => window.ZOrder)
                    .ThenByDescending(window => NativeRole(frame, window) == "root")
                    .ThenBy(window => window.Hwnd)
                    .ToArray();
                var quickMapFrame = IsQuickMapFrame(frame);
                var observedWindowHwnds = frame.ObservedWindowHwnds is { Count: > 0 }
                    ? frame.ObservedWindowHwnds.ToHashSet()
                    : windows.Select(window => window.Hwnd).ToHashSet();
                // The automation payload is direct evidence that its explicitly
                // identified window was observed. Older quick scans could record
                // only a hidden GA_ROOTOWNER in ObservedWindowHwnds even though
                // UIA returned controls from the visible owned application form.
                if (quickMapFrame)
                    observedWindowHwnds.UnionWith(frame.Automation
                        .Where(control => control.WindowHwnd != 0)
                        .Select(control => control.WindowHwnd));
                var observedWindows = windows.Where(window => observedWindowHwnds.Contains(window.Hwnd)).ToArray();
                var frameIsReady = (IsPromotableAutomationFrame(frame) ||
                                    quickMapFrame && frame.Automation.Count > 0 &&
                                    frame.AutomationStatus is "ok" or "node-limit" or "partial" or "timeout") &&
                                   !IsOccludedRootVisualFrame(frame, windows);
                var promotedAutomation = !frameIsReady
                    ? []
                    : string.Equals(frame.ObservationScope, "popup-delta", StringComparison.Ordinal)
                        ? FilterPromotablePopupAutomation(frame, observedWindows)
                        : quickMapFrame
                            ? frame.Automation
                            : FilterPromotableFullRootAutomation(frame);
                var promotedWindows = !frameIsReady
                    ? observedWindows.Where(window => HasPromotablePeerRootNativeEvidence(frame, window)).ToArray()
                    : string.Equals(frame.ObservationScope, "popup-delta", StringComparison.Ordinal) &&
                      promotedAutomation.Count == 0
                        ? []
                        : observedWindows;
                var root = windows.FirstOrDefault(window => NativeRole(frame, window) == "root") ?? frame.Window;
                promotedWindows = promotedWindows
                    .Where(window => window.Bounds.Width > 0 && window.Bounds.Height > 0)
                    .Where(window => NativeRole(frame, window) != "owned" ||
                                     window.RootOwnerHwnd == frame.Window.RootOwnerHwnd ||
                                     IsExplicitDialogCapture(frame, window))
                    .ToArray();
                var rawDataStreamFrame = MaterializeRawDataStreamFrame(
                    manifest, frame, observedWindows, frame.Automation, appId, nodes, edges);
                var rawStreamSurfaceByWindow = rawDataStreamFrame.SurfaceByWindow;
                var surfaceByWindow = new Dictionary<long, RawSurfaceInfo>();
                var effectiveControlsByWindow = AssignEffectiveRawControlOwners(frame, windows, promotedAutomation);
                var roleByWindow = windows.ToDictionary(
                    window => window.Hwnd,
                    window => NativeRole(frame, window));
                var surfaceClassByWindow = windows.ToDictionary(
                    window => window.Hwnd,
                    window => ClassifySurface(window, roleByWindow[window.Hwnd], root.Bounds));
                promotedWindows = promotedWindows
                    .Where(window => surfaceClassByWindow[window.Hwnd] switch
                    {
                        "RawPopupWindow" => HasPromotablePopupContent(
                            window, effectiveControlsByWindow.GetValueOrDefault(window.Hwnd) ?? []),
                        "RawDialogWindow" => HasPromotableDialogContent(
                            window, effectiveControlsByWindow.GetValueOrDefault(window.Hwnd) ?? []),
                        _ => true
                    })
                    .ToArray();
                var fingerprintByWindow = windows.ToDictionary(
                    window => window.Hwnd,
                    window => roleByWindow[window.Hwnd] == "root"
                        ? "root"
                        : BuildSurfaceIdentityFingerprint(
                            window,
                            surfaceClassByWindow[window.Hwnd],
                            effectiveControlsByWindow.GetValueOrDefault(window.Hwnd) ?? []));
                var baseSurfaceKeyByWindow = windows.ToDictionary(
                    window => window.Hwnd,
                    window => string.Join('|', appKey,
                        roleByWindow[window.Hwnd],
                        surfaceClassByWindow[window.Hwnd],
                        StableIdentity.Normalize(window.ClassName),
                        fingerprintByWindow[window.Hwnd]));

                foreach (var window in promotedWindows)
                {
                    var role = roleByWindow[window.Hwnd];
                    var surfaceClass = surfaceClassByWindow[window.Hwnd];
                    var fingerprint = fingerprintByWindow[window.Hwnd];
                    var ownerSurfaceKey = role == "owned"
                        ? baseSurfaceKeyByWindow.GetValueOrDefault(window.OwnerHwnd) ?? baseSurfaceKeyByWindow.GetValueOrDefault(root.Hwnd)
                        : null;
                    var surfaceKey = ownerSurfaceKey is null
                        ? baseSurfaceKeyByWindow[window.Hwnd]
                        : string.Join('|', baseSurfaceKeyByWindow[window.Hwnd], "owner", ownerSurfaceKey);
                    var surfaceId = StableIdentity.Create("surface", RawLayer, surfaceKey);
                    var evidence = Evidence(manifest, frame, window.Bounds);
                    var surfaceNode = GetNode(nodes, surfaceId, GraphNodeKind.Surface, appId, surfaceId,
                        surfaceClass);
                    if (screenVariantFrame)
                        surfaceNode.AddEvidence(evidence);
                    surfaceNode.AddProperty("layer", RawLayer);
                    surfaceNode.AddProperty("surfaceClass", surfaceClass);
                    surfaceNode.AddProperty("role", role);
                    surfaceNode.AddProperty("processName", manifest.Target.ProcessName, sensitive: true);
                    surfaceNode.AddProperty("className", window.ClassName);
                    surfaceNode.AddProperty("title", window.Title, sensitive: true);
                    surfaceNode.AddProperty("dpi", window.Dpi.ToString(CultureInfo.InvariantCulture));
                    surfaceNode.AddProperty("style", window.Style.ToString("x", CultureInfo.InvariantCulture));
                    surfaceNode.AddProperty("exStyle", window.ExStyle.ToString("x", CultureInfo.InvariantCulture));
                    surfaceNode.AddProperty("isToolWindow", window.IsToolWindow.ToString(CultureInfo.InvariantCulture));
                    surfaceNode.AddProperty("isTopMost", window.IsTopMost.ToString(CultureInfo.InvariantCulture));
                    surfaceNode.AddProperty("isCloaked", window.IsCloaked.ToString(CultureInfo.InvariantCulture));
                    surfaceNode.AddProperty("isMinimized", window.IsMinimized.ToString(CultureInfo.InvariantCulture));
                    AddExtractionSurfaceProperties(surfaceNode, frame, window.Hwnd);
                    surfaceNode.AddProperty("nativeGroupKey", StableIdentity.Create("native", appKey, role, window.ClassName));
                    if (rawStreamSurfaceByWindow.TryGetValue(window.Hwnd, out var rawStreamSurfaceId))
                        surfaceNode.AddProperty("sourceRawDataStreamSurfaceId", rawStreamSurfaceId);
                    AddContains(edges, appId, surfaceId, evidence);

                    var surfaceInfo = rawSurfaces.GetValueOrDefault(surfaceId);
                    if (surfaceInfo is null)
                    {
                        surfaceInfo = new(surfaceId, surfaceKey, surfaceClass, role, window.ClassName, window.Title, fingerprint);
                        rawSurfaces.Add(surfaceId, surfaceInfo);
                    }
                    if (screenVariantFrame)
                        surfaceInfo.Evidence.Add(evidence);
                    surfaceByWindow[window.Hwnd] = surfaceInfo;
                    rawSurfaceByNativeWindow[window.Hwnd] = surfaceInfo;
                }

                foreach (var surfaceInfo in surfaceByWindow.Values.DistinctBy(surface => surface.Id, StringComparer.Ordinal))
                {
                    var window = promotedWindows.First(candidate => surfaceByWindow[candidate.Hwnd].Id == surfaceInfo.Id);
                    if (window.OwnerHwnd != 0 && surfaceByWindow.TryGetValue(window.OwnerHwnd, out var directOwner))
                        surfaceInfo.OwnerRawSurfaceId = directOwner.Id;
                    else if (surfaceInfo.Role == "owned")
                    {
                        if (window.OwnerHwnd != 0 && rawSurfaceByNativeWindow.TryGetValue(window.OwnerHwnd, out var priorOwner))
                            surfaceInfo.OwnerRawSurfaceId = priorOwner.Id;
                        else if (surfaceByWindow.TryGetValue(root.Hwnd, out var promotedRoot))
                            surfaceInfo.OwnerRawSurfaceId = promotedRoot.Id;
                    }
                    if (!string.IsNullOrWhiteSpace(surfaceInfo.OwnerRawSurfaceId))
                        nodes[surfaceInfo.Id].AddProperty("ownerRawSurfaceId", surfaceInfo.OwnerRawSurfaceId!);

                    if (surfaceInfo.SurfaceClass == "RawPopupWindow" && frame.InteractionSource is not null)
                    {
                        var source = ResolveInteractionSource(
                            rawControls.Values, rawSurfaces, surfaceInfo.OwnerRawSurfaceId, frame.InteractionSource);
                        if (source is not null)
                        {
                            surfaceInfo.InteractionSourceRawControlId = source.Id;
                            nodes[surfaceInfo.Id].AddProperty("interactionSourceRawControlId", source.Id);
                            nodes[surfaceInfo.Id].AddProperty("interactionSourceName", frame.InteractionSource.Name, sensitive: true);
                            var opens = GetEdge(edges,
                                StableIdentity.Create("edge", source.Id, surfaceInfo.Id, "opens-popup"),
                                "opens-popup", source.Id, surfaceInfo.Id);
                            opens.AddProperty("relationship", "opens popup");
                            opens.AddEvidence(Evidence(manifest, frame, window.Bounds));
                        }
                    }
                }

                foreach (var pair in surfaceByWindow.OrderBy(pair => pair.Key))
                {
                    var window = windows.First(candidate => candidate.Hwnd == pair.Key);
                    var controls = effectiveControlsByWindow.GetValueOrDefault(window.Hwnd) ?? [];
                    var frameControls = MaterializeControls(
                        manifest, frame, window, pair.Value, controls, nodes, edges, rawControls);
                    foreach (var control in frameControls)
                    {
                        var lookupWindow = control.Observation.WindowHwnd != 0 ? control.Observation.WindowHwnd : frame.Window.RootOwnerHwnd;
                        if (!string.IsNullOrWhiteSpace(control.Observation.RuntimeId) &&
                            rawDataStreamFrame.ControlByWindowRuntime.TryGetValue(
                                RdsControlLookupKey(lookupWindow, control.Observation.RuntimeId), out var rawStreamControlId))
                            nodes[rawStreamControlId].AddProperty("stableControlKey", control.Id);
                    }
                    var visibleControls = frameControls
                        .Where(control => IsStateVisible(control.Observation))
                        .OrderBy(control => control.Id, StringComparer.Ordinal)
                        .ToArray();
                    var contentSignature = StableIdentity.Create("shape", pair.Value.Id,
                        string.Join('|', visibleControls.Select(control =>
                            $"{control.Id}:{control.Observation.IsEnabled}:{control.Observation.IsSelected}:" +
                            $"{control.Observation.HasKeyboardFocus}:{control.Observation.ToggleState}:{control.Observation.ExpandCollapseState}")));
                    var variantId = StableIdentity.Create("state", pair.Value.Id, contentSignature);
                    var variantEvidence = Evidence(manifest, frame, window.Bounds);
                    var variant = GetNode(nodes, variantId, GraphNodeKind.State, pair.Value.Id, variantId, "Observed variant");
                    variant.AddEvidence(variantEvidence);
                    variant.AddProperty("layer", RawLayer);
                    variant.AddProperty("contentSignature", contentSignature);
                    variant.AddProperty("trigger", frame.Trigger, sensitive: true);
                    variant.AddProperty("observationScope", VariantObservationScope(frame));
                    var contextLabel = ResolveStateContextLabel(visibleControls);
                    if (!string.IsNullOrWhiteSpace(contextLabel))
                        variant.AddProperty("contextLabel", contextLabel, sensitive: true);
                    variant.AddProperty("controlCount", visibleControls.Length.ToString(CultureInfo.InvariantCulture));
                    AddContains(edges, pair.Value.Id, variantId, variantEvidence);
                    pair.Value.VariantIds.Add(variantId);
                    rawStateByFrameSurface[FrameSurfaceKey(manifest.SessionId, frame.Sequence, pair.Value.Id)] = variantId;

                    foreach (var control in visibleControls)
                        AddContains(edges, variantId, control.Id, Evidence(manifest, frame, control.Observation.Bounds));

                    if (previousVariantBySurface.TryGetValue(pair.Value.Id, out var previousVariant) && previousVariant != variantId)
                    {
                        var transitionId = StableIdentity.Create("edge", previousVariant, variantId, "observed-transition");
                        var transition = GetEdge(edges, transitionId, "observed-transition", previousVariant, variantId);
                        transition.AddProperty("trigger", frame.Trigger, sensitive: true);
                        transition.AddEvidence(variantEvidence);
                    }
                    previousVariantBySurface[pair.Value.Id] = variantId;
                }
            }
        }

        MaterializeInteractionTrace(inputs, nodes, edges, rawSurfaces, rawControls, rawStateByFrameSurface);
        ApplyVerificationProperties(nodes, rawControls);

        MaterializeSemanticWorld(appId, primaryManifest.Target.ProcessName, nodes, edges, rawSurfaces, rawControls);

        var orderedNodes = nodes.Values.Select(node => node.Build()).OrderBy(node => node.Id, StringComparer.Ordinal).ToArray();
        var orderedEdges = edges.Values.Select(edge => edge.Build()).OrderBy(edge => edge.Id, StringComparer.Ordinal).ToArray();
        var semanticHash = GraphSemantics.ComputeHash(orderedNodes, orderedEdges);
        var sourceBundleIds = inputs.Select(input => input.Manifest.SessionId).Distinct(StringComparer.Ordinal).ToArray();
        var effectiveLogicalMapId = string.IsNullOrWhiteSpace(logicalMapId)
            ? (sourceBundleIds.Length == 1 ? sourceBundleIds[0] : StableIdentity.Create("map", string.Join('|', sourceBundleIds.OrderBy(value => value, StringComparer.Ordinal))))
            : logicalMapId;
        var graphId = StableIdentity.Create("graph", effectiveLogicalMapId, semanticHash);
        var graph = new UiKnowledgeGraph(
            new(FormatVersions.Graph, FormatVersions.Tool, graphId,
                inputs.Max(input => input.Manifest.EndedUtc),
                sourceBundleIds[0],
                semanticHash,
                FormatVersions.FullEvidenceProfile,
                sourceBundleIds,
                effectiveLogicalMapId),
            orderedNodes,
            orderedEdges);
        var validation = GraphValidator.Validate(graph);
        if (!validation.IsValid)
            throw new InvalidDataException("Constructed graph failed integrity validation: " +
                string.Join(", ", validation.Issues.Where(issue => issue.Severity == "error").Take(8)
                    .Select(issue => $"{issue.Code}@{issue.Path}: {issue.Message}")));
        return graph;
    }

    private static RecordingGraphInput LoadBundleFrames(string bundlePath)
    {
        var bundleValidation = RecordingBundleValidator.Validate(bundlePath);
        if (!bundleValidation.IsValid)
            throw new InvalidDataException("Recording bundle validation failed.");

        using var bundle = RecordingBundle.Open(bundlePath);
        var manifest = bundle.ReadJson<RecordingManifest>("manifest.json");
        var statebook = bundle.ReadJson<DerivedStatebook>("derived/statebook.json");
        var interactions = bundle.Entries.Contains("raw/interactions.jsonl")
            ? bundle.ReadText("raw/interactions.jsonl")
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => System.Text.Json.JsonSerializer.Deserialize<InteractionObservation>(line, JsonDefaults.Options)
                    ?? throw new InvalidDataException("Null interaction observation."))
                .OrderBy(interaction => interaction.Sequence)
                .ToArray()
            : [];
        var representativeFrames = statebook.RepresentativeFrames
            .Concat(interactions.Select(interaction => interaction.SourceFrameSequence))
            .Concat(interactions.SelectMany(interaction => interaction.ResultFrameSequences))
            .ToHashSet();
        var observations = bundle.Entries
            .Where(entry => entry.StartsWith("raw/observations/frame-", StringComparison.Ordinal) &&
                            entry.EndsWith(".json", StringComparison.Ordinal))
            .Select(bundle.ReadJson<FrameObservation>)
            .Where(frame => representativeFrames.Contains(frame.Sequence))
            .OrderBy(frame => frame.Sequence)
            .ToArray();
        return new(manifest, observations, interactions);
    }

    private static void MaterializeInteractionTrace(
        IReadOnlyList<RecordingGraphInput> inputs,
        IDictionary<string, MutableNode> nodes,
        IDictionary<string, MutableEdge> edges,
        IReadOnlyDictionary<string, RawSurfaceInfo> rawSurfaces,
        IReadOnlyDictionary<string, RawControlInfo> rawControls,
        IReadOnlyDictionary<string, string> rawStateByFrameSurface)
    {
        foreach (var raw in rawControls.Values)
        {
            foreach (var affordance in DiscoverAffordances(raw.Observation))
                nodes[raw.Id].AddProperty("affordance", affordance);
        }

        foreach (var input in inputs)
        foreach (var interaction in input.Interactions)
        {
            if (interaction.SourceControl is null) continue;
            var sourceControlEvidenceFrameSequence = interaction.SourceFrameSequence;
            var sourceControl = ResolveInteractionSource(rawControls.Values, rawSurfaces, null, interaction.SourceControl,
                input.Manifest.SessionId, interaction.SourceFrameSequence);
            if (sourceControl is null && IsPointerObservedCanvasTarget(interaction.SourceControl))
            {
                var lastCausalFrame = interaction.ResultFrameSequences.DefaultIfEmpty(interaction.SourceFrameSequence).Max();
                var observedSource = rawControls.Values
                    .Where(control => control.Observation.RuntimeId == interaction.SourceControl.RuntimeId)
                    .SelectMany(control => control.Evidence
                        .Where(evidence => evidence.BundleId == input.Manifest.SessionId &&
                                           evidence.FrameSequence >= interaction.SourceFrameSequence &&
                                           evidence.FrameSequence <= lastCausalFrame)
                        .Select(evidence => new { Control = control, evidence.FrameSequence }))
                    .OrderBy(candidate => candidate.FrameSequence)
                    .FirstOrDefault();
                if (observedSource is not null)
                {
                    sourceControl = observedSource.Control;
                    sourceControlEvidenceFrameSequence = observedSource.FrameSequence;
                }
            }
            if (sourceControl is null) continue;
            if (interaction.Outcome == InteractionOutcome.Succeeded)
                sourceControl.WasConfirmed = true;
            var sourceState = rawStateByFrameSurface.GetValueOrDefault(
                FrameSurfaceKey(input.Manifest.SessionId, interaction.SourceFrameSequence, sourceControl.RawSurfaceId));
            if (sourceState is null) continue;

            var targetStates = interaction.Outcome == InteractionOutcome.Succeeded
                ? interaction.ResultFrameSequences
                    .SelectMany(sequence => rawStateByFrameSurface
                        .Where(pair => pair.Key.StartsWith(FramePrefix(input.Manifest.SessionId, sequence), StringComparison.Ordinal))
                        .Select(pair => pair.Value))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray()
                : [sourceState];

            foreach (var targetState in targetStates)
            {
                var id = StableIdentity.Create("edge", "interaction", input.Manifest.SessionId,
                    interaction.InteractionId, sourceState, targetState);
                var edge = GetEdge(edges, id, "interaction", sourceState, targetState);
                edge.AddProperty("interactionId", interaction.InteractionId);
                edge.AddProperty("sessionId", input.Manifest.SessionId);
                edge.AddProperty("operationId", interaction.OperationId);
                edge.AddProperty("attempt", interaction.Attempt.ToString(CultureInfo.InvariantCulture));
                edge.AddProperty("sequence", interaction.Sequence.ToString(CultureInfo.InvariantCulture));
                edge.AddProperty("sourceFrameSequence", interaction.SourceFrameSequence.ToString(CultureInfo.InvariantCulture));
                if (sourceControlEvidenceFrameSequence != interaction.SourceFrameSequence)
                    edge.AddProperty("sourceControlEvidenceFrameSequence",
                        sourceControlEvidenceFrameSequence.ToString(CultureInfo.InvariantCulture));
                foreach (var inputSequence in interaction.InputSequences)
                    edge.AddProperty("inputSequence", inputSequence.ToString(CultureInfo.InvariantCulture));
                foreach (var resultFrame in interaction.ResultFrameSequences)
                    edge.AddProperty("resultFrameSequence", resultFrame.ToString(CultureInfo.InvariantCulture));
                edge.AddProperty("startedUtc", interaction.StartedUtc.ToString("O", CultureInfo.InvariantCulture));
                edge.AddProperty("completedUtc", interaction.CompletedUtc.ToString("O", CultureInfo.InvariantCulture));
                edge.AddProperty("actor", interaction.Actor.ToString());
                edge.AddProperty("gesture", interaction.Gesture.ToString());
                edge.AddProperty("action", interaction.Action.ToString());
                edge.AddProperty("outcome", interaction.Outcome.ToString());
                edge.AddProperty("sourceControlId", sourceControl.Id);
                edge.AddProperty("diagnosticCode", interaction.DiagnosticCode);
                var evidence = nodes[targetState].EvidenceFor(input.Manifest.SessionId, interaction.ResultFrameSequences)
                               ?? nodes[sourceState].EvidenceFor(input.Manifest.SessionId, [interaction.SourceFrameSequence]);
                if (evidence is not null) edge.AddEvidence(evidence);
            }
        }
    }

    private static bool IsPointerObservedCanvasTarget(AutomationObservation control) =>
        control.ControlType.Equals("CanvasItem", StringComparison.OrdinalIgnoreCase) &&
        control.FrameworkId.Equals("UiAtlas.Pointer", StringComparison.OrdinalIgnoreCase);

    private static bool IsQuickMapFrame(FrameObservation frame) =>
        frame.Trigger.StartsWith("quick-map:", StringComparison.Ordinal);

    private static void ApplyVerificationProperties(
        IDictionary<string, MutableNode> nodes,
        IReadOnlyDictionary<string, RawControlInfo> rawControls)
    {
        foreach (var control in rawControls.Values)
        {
            nodes[control.Id].AddProperty("verificationStatus", control.WasConfirmed
                ? "Confirmed"
                : control.WasObservedVisible ? "Observed" : "Unverified");
            nodes[control.Id].AddProperty("effectivelyVisible", control.WasObservedVisible.ToString(CultureInfo.InvariantCulture));
        }
    }

    private static IReadOnlyList<string> DiscoverAffordances(AutomationObservation control)
    {
        var values = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pattern in control.SupportedPatterns ?? [])
        {
            if (pattern.Contains("Invoke", StringComparison.OrdinalIgnoreCase)) values.Add("Invoke");
            if (pattern.Contains("SelectionItem", StringComparison.OrdinalIgnoreCase)) values.Add("Select");
            if (pattern.Contains("ExpandCollapse", StringComparison.OrdinalIgnoreCase))
            {
                values.Add("Expand");
                values.Add("Collapse");
            }
            if (pattern.Contains("Toggle", StringComparison.OrdinalIgnoreCase)) values.Add("Toggle");
            if (pattern.Contains("Value", StringComparison.OrdinalIgnoreCase)) values.Add("SetValue");
            if (pattern.Contains("RangeValue", StringComparison.OrdinalIgnoreCase) ||
                pattern.Contains("Scroll", StringComparison.OrdinalIgnoreCase)) values.Add("Scroll");
            if (pattern.Contains("Transform", StringComparison.OrdinalIgnoreCase)) values.Add("MoveResize");
        }
        var type = NormalizeControlType(control);
        if (type is "Button" or "SplitButton" or "MenuItem" or "Hyperlink") values.Add("Invoke");
        if (type is "TabItem" or "ListItem" or "TreeItem" or "DataItem") values.Add("Select");
        if (type is "Edit" or "ComboBox") values.Add("SetValue");
        return values.Order(StringComparer.Ordinal).ToArray();
    }

    private static string FramePrefix(string bundleId, long frameSequence) =>
        $"{bundleId}\u001f{frameSequence}\u001f";

    private static string FrameSurfaceKey(string bundleId, long frameSequence, string surfaceId) =>
        FramePrefix(bundleId, frameSequence) + surfaceId;

    private static RawDataStreamFrameInfo MaterializeRawDataStreamFrame(
        RecordingManifest manifest,
        FrameObservation frame,
        IReadOnlyList<WindowObservation> windows,
        IReadOnlyList<AutomationObservation> controls,
        string appId,
        IDictionary<string, MutableNode> nodes,
        IDictionary<string, MutableEdge> edges)
    {
        var surfaceByWindow = new Dictionary<long, string>();
        var packageId = StableIdentity.Create("package", manifest.SessionId, frame.Sequence.ToString(CultureInfo.InvariantCulture));
        var effectivelyVisible = AutomationObservationVisibility.FilterEffectivelyVisible(controls).ToHashSet();
        foreach (var window in windows)
        {
            var windowRole = NativeRole(frame, window);
            var id = StableIdentity.Create("rds-surface", appId,
                frame.Sequence.ToString(CultureInfo.InvariantCulture), windowRole,
                window.ClassName, window.ZOrder.ToString(CultureInfo.InvariantCulture));
            var label = string.IsNullOrWhiteSpace(window.Title)
                ? SurfaceDisplay(ClassifySurface(window, windowRole, frame.Window.Bounds))
                : window.Title;
            var evidence = Evidence(manifest, frame, window.Bounds);
            var node = GetNode(nodes, id, GraphNodeKind.Window, appId, id, label);
            node.AddEvidence(evidence);
            node.AddProperty("layer", RawDataStreamsLayer);
            node.AddProperty("sourcePackageId", packageId);
            node.AddProperty("sourceStateId", frame.Sequence.ToString(CultureInfo.InvariantCulture));
            node.AddProperty("captureReason", frame.Trigger, sensitive: true);
            node.AddProperty("hwnd", $"0x{window.Hwnd:X}", sensitive: true);
            node.AddProperty("rootOwnerHwnd", $"0x{window.RootOwnerHwnd:X}", sensitive: true);
            node.AddProperty("ownerHwnd", window.OwnerHwnd == 0 ? null : $"0x{window.OwnerHwnd:X}", sensitive: true);
            node.AddProperty("role", windowRole);
            node.AddProperty("processName", manifest.Target.ProcessName, sensitive: true);
            node.AddProperty("className", window.ClassName);
            node.AddProperty("title", window.Title, sensitive: true);
            node.AddProperty("nativeWindowType", ClassifySurface(window, windowRole, frame.Window.Bounds));
            node.AddProperty("style", window.Style.ToString("x", CultureInfo.InvariantCulture));
            node.AddProperty("exStyle", window.ExStyle.ToString("x", CultureInfo.InvariantCulture));
            node.AddProperty("dpi", window.Dpi.ToString(CultureInfo.InvariantCulture));
            node.AddProperty("zOrder", window.ZOrder.ToString(CultureInfo.InvariantCulture));
            node.AddProperty("isVisible", window.IsVisible.ToString(CultureInfo.InvariantCulture));
            node.AddProperty("isCloaked", window.IsCloaked.ToString(CultureInfo.InvariantCulture));
            node.AddProperty("isMinimized", window.IsMinimized.ToString(CultureInfo.InvariantCulture));
            AddContains(edges, appId, id, evidence);
            surfaceByWindow[window.Hwnd] = id;
        }

        var orderedControls = controls
            .Select((control, ordinal) => new { Control = control, Ordinal = ordinal })
            .OrderBy(item => item.Control.WindowHwnd)
            .ThenBy(item => item.Control.Bounds.Y)
            .ThenBy(item => item.Control.Bounds.X)
            .ThenBy(item => item.Ordinal)
            .ToArray();
        var idByRuntimeAndWindow = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in orderedControls)
        {
            var windowHwnd = item.Control.WindowHwnd != 0
                ? item.Control.WindowHwnd
                : frame.Window.RootOwnerHwnd;
            if (!surfaceByWindow.TryGetValue(windowHwnd, out var surfaceId))
                continue;
            var instance = !string.IsNullOrWhiteSpace(item.Control.RuntimeId)
                ? item.Control.RuntimeId
                : $"ordinal:{item.Ordinal.ToString(CultureInfo.InvariantCulture)}";
            var id = StableIdentity.Create("rds-control", surfaceId, instance,
                item.Ordinal.ToString(CultureInfo.InvariantCulture));
            if (!string.IsNullOrWhiteSpace(item.Control.RuntimeId))
                idByRuntimeAndWindow.TryAdd($"{windowHwnd:x}|{item.Control.RuntimeId}", id);
        }

        foreach (var item in orderedControls)
        {
            var control = item.Control;
            var windowHwnd = control.WindowHwnd != 0 ? control.WindowHwnd : frame.Window.RootOwnerHwnd;
            if (!surfaceByWindow.TryGetValue(windowHwnd, out var surfaceId))
                continue;
            var instance = !string.IsNullOrWhiteSpace(control.RuntimeId)
                ? control.RuntimeId
                : $"ordinal:{item.Ordinal.ToString(CultureInfo.InvariantCulture)}";
            var id = StableIdentity.Create("rds-control", surfaceId, instance,
                item.Ordinal.ToString(CultureInfo.InvariantCulture));
            var parentId = !string.IsNullOrWhiteSpace(control.ParentRuntimeId) &&
                           idByRuntimeAndWindow.TryGetValue($"{windowHwnd:x}|{control.ParentRuntimeId}", out var parent)
                ? parent
                : surfaceId;
            var evidence = Evidence(manifest, frame, control.Bounds);
            var node = GetNode(nodes, id, GraphNodeKind.Control, parentId, id, StableDisplay(control));
            node.AddEvidence(evidence);
            node.AddProperty("layer", RawDataStreamsLayer);
            node.AddProperty("sourcePackageId", packageId);
            node.AddProperty("sourceStateId", frame.Sequence.ToString(CultureInfo.InvariantCulture));
            node.AddProperty("rawDataStreamSurfaceId", surfaceId);
            node.AddProperty("runtimeId", control.RuntimeId, sensitive: true);
            node.AddProperty("parentRuntimeId", control.ParentRuntimeId, sensitive: true);
            node.AddProperty("automationId", control.AutomationId);
            node.AddProperty("name", control.Name, sensitive: true);
            node.AddProperty("controlType", NormalizeControlType(control));
            node.AddProperty("className", control.ClassName);
            node.AddProperty("frameworkId", control.FrameworkId);
            node.AddProperty("enabled", control.IsEnabled.ToString(CultureInfo.InvariantCulture));
            node.AddProperty("offscreen", control.IsOffscreen.ToString(CultureInfo.InvariantCulture));
            node.AddProperty("effectivelyVisible", effectivelyVisible.Contains(control).ToString(CultureInfo.InvariantCulture));
            node.AddProperty("verificationStatus", effectivelyVisible.Contains(control) ? "Observed" : "Unverified");
            node.AddProperty("selected", control.IsSelected.ToString(CultureInfo.InvariantCulture));
            node.AddProperty("focused", control.HasKeyboardFocus.ToString(CultureInfo.InvariantCulture));
            node.AddProperty("toggleState", control.ToggleState);
            node.AddProperty("expandCollapseState", control.ExpandCollapseState);
            foreach (var pattern in control.SupportedPatterns ?? []) node.AddProperty("supportedPattern", pattern);
            foreach (var selector in StableSelectors(control)) node.AddProperty("stableSelector", selector);
            AddExtractionControlProperties(node, frame, control);
            AddVisualIdentityProperties(node, control);
            AddContains(edges, parentId, id, evidence);
        }

        return new(surfaceByWindow, idByRuntimeAndWindow);
    }

    private static void AddExtractionSurfaceProperties(MutableNode node, FrameObservation frame, long hwnd)
    {
        if (frame.Extraction is not { } extraction) return;
        var candidates = extraction.Candidates.Where(candidate =>
            candidate.Control.WindowHwnd == hwnd || candidate.Control.WindowHwnd == 0 && hwnd == frame.Window.RootOwnerHwnd).ToArray();
        node.AddProperty("extractionCoverageStatus", extraction.CoverageStatus.ToString());
        node.AddProperty("extractionStopReason", extraction.StopReason);
        node.AddProperty("extractionCandidateCount", candidates.Length.ToString(CultureInfo.InvariantCulture));
        node.AddProperty("extractionProbeCount", extraction.ProbeCount.ToString(CultureInfo.InvariantCulture));
        if (hwnd == frame.Window.RootOwnerHwnd)
            node.AddProperty("coverageGapCount", extraction.Gaps.Count.ToString(CultureInfo.InvariantCulture));
    }

    private static IReadOnlyList<AutomationObservation> FilterPromotableFullRootAutomation(
        FrameObservation frame)
    {
        // Partial means incomplete, not invalid. Native controls returned before a
        // provider timeout are still direct evidence and must remain available next
        // to the independent visual fallback. This is especially important for
        // stable application chrome recovered through MSAA or a bounded UIA band.
        var visible = AutomationObservationVisibility.FilterEffectivelyVisible(frame.Automation).ToHashSet();
        if (frame.AutomationStatus is not ("ok" or "node-limit" or "partial"))
        {
            var windowBounds = frame.Automation
                .Where(control => NormalizeControlType(control) == "Window")
                .GroupBy(control => control.WindowHwnd)
                .ToDictionary(group => group.Key, group => group.OrderByDescending(control =>
                    (long)control.Bounds.Width * control.Bounds.Height).First().Bounds);
            return frame.Automation.Where(control => IsShadowControl(control) ||
                EffectiveControlWindowHwnd(frame, control) == EffectiveRootWindowHwnd(frame) &&
                visible.Contains(control) && IsStableNativeChromeForCarry(control,
                    windowBounds.GetValueOrDefault(control.WindowHwnd) ?? new RectI(0, 0, 0, 0))).ToArray();
        }
        return frame.Automation.Where(control => visible.Contains(control) || IsShadowControl(control)).ToArray();
    }

    private static IReadOnlyList<FrameObservation> SuppressDuplicateNativeCaptionButtons(
        IReadOnlyList<FrameObservation> observations) => observations.Select(frame =>
    {
        var indexed = frame.Automation.Select((control, index) => (Control: control, Index: index)).ToArray();
        var remove = new HashSet<int>();
        foreach (var group in indexed
                     .Select(item => (item.Control, item.Index, Role: CaptionButtonRole(item.Control)))
                     .Where(item => item.Role is not null && IsLikelyCaptionButton(frame, item.Control))
                     .GroupBy(item => (WindowHwnd: EffectiveControlWindowHwnd(frame, item.Control), item.Role)))
        {
            if (group.Count() < 2) continue;
            var preferred = group
                .OrderByDescending(item => CaptionButtonProviderPriority(item.Control))
                .ThenByDescending(item => (long)item.Control.Bounds.Width * item.Control.Bounds.Height)
                .ThenBy(item => item.Index)
                .First();
            foreach (var duplicate in group.Where(item => item.Index != preferred.Index &&
                         LooksLikeSameCaptionButtonCluster(item.Control.Bounds, preferred.Control.Bounds)))
                remove.Add(duplicate.Index);
        }
        if (remove.Count == 0) return frame;
        return frame with
        {
            Automation = indexed.Where(item => !remove.Contains(item.Index))
                .Select(item => item.Control).ToArray()
        };
    }).ToArray();

    private static string? CaptionButtonRole(AutomationObservation control)
    {
        if (NormalizeControlType(control) != "Button") return null;
        var identity = string.IsNullOrWhiteSpace(control.AutomationId)
            ? control.Name
            : control.AutomationId;
        return identity switch
        {
            "Minimize" => "minimize",
            "Maximize" or "Restore" or "Restore Down" => "maximize-restore",
            "Close" => "close",
            _ => null
        };
    }

    private static bool IsLikelyCaptionButton(FrameObservation frame, AutomationObservation control)
    {
        var hwnd = EffectiveControlWindowHwnd(frame, control);
        var window = (frame.ScopedWindows is { Count: > 0 } ? frame.ScopedWindows : [frame.Window])
            .FirstOrDefault(candidate => candidate.Hwnd == hwnd) ?? frame.Window;
        if (!window.Bounds.IsValid || !control.Bounds.IsValid) return false;
        var topBandBottom = window.Bounds.Y + Math.Max(80, (int)Math.Ceiling(window.Bounds.Height * .12));
        var rightBandLeft = window.Bounds.X + Math.Max(0, window.Bounds.Width - Math.Max(360, window.Bounds.Width / 4));
        return control.Bounds.Y < topBandBottom &&
               control.Bounds.Y + control.Bounds.Height > window.Bounds.Y - 16 &&
               control.Bounds.X + control.Bounds.Width > rightBandLeft;
    }

    private static int CaptionButtonProviderPriority(AutomationObservation control)
    {
        var priority = control.IsEnabled && !control.IsOffscreen ? 1_000 : 0;
        if (!control.FrameworkId.Equals("UiAtlas.Cached", StringComparison.OrdinalIgnoreCase)) priority += 500;
        if (control.ClassName.Equals("NetUIAppFrameHelper", StringComparison.OrdinalIgnoreCase)) priority += 250;
        else if (!string.IsNullOrWhiteSpace(control.ClassName)) priority += 100;
        return priority;
    }

    private static bool LooksLikeSameCaptionButtonCluster(RectI first, RectI second)
    {
        var firstCenterX = first.X + first.Width / 2.0;
        var firstCenterY = first.Y + first.Height / 2.0;
        var secondCenterX = second.X + second.Width / 2.0;
        var secondCenterY = second.Y + second.Height / 2.0;
        return Math.Abs(firstCenterX - secondCenterX) <= Math.Max(first.Width, second.Width) * 2 &&
               Math.Abs(firstCenterY - secondCenterY) <= Math.Max(first.Height, second.Height) * 2;
    }

    private static IReadOnlyList<FrameObservation> CarryForwardStableNativeChrome(
        IReadOnlyList<FrameObservation> observations)
    {
        if (observations.Count < 2) return observations;

        var knownByWindow = new Dictionary<long, IReadOnlyList<AutomationObservation>>();
        var result = new List<FrameObservation>(observations.Count);
        foreach (var frame in observations.OrderBy(item => item.Sequence))
        {
            var controls = frame.Automation.ToList();
            var incomplete = string.Equals(frame.ObservationScope, "control-delta", StringComparison.Ordinal) ||
                             frame.AutomationTimedOut ||
                             frame.AutomationStatus is "partial" or "timeout" or "visual-only";
            if (incomplete)
            {
                var scopedWindowHwnds = (frame.ScopedWindows is { Count: > 0 }
                        ? frame.ScopedWindows
                        : [frame.Window])
                    .Where(window => window.IsVisible && !window.IsCloaked && !window.IsMinimized &&
                                     window.Bounds.Width > 0 && window.Bounds.Height > 0)
                    .Select(window => window.Hwnd)
                    .ToHashSet();
                foreach (var windowHwnd in scopedWindowHwnds)
                {
                    if (!knownByWindow.TryGetValue(windowHwnd, out var known)) continue;
                    foreach (var control in known)
                    {
                        if (controls.Any(current => SameNativeControlIdentity(current, control))) continue;
                        controls.Add(control);
                    }
                }
            }

            var enriched = controls.Count == frame.Automation.Count
                ? frame
                : frame with { Automation = controls.ToArray() };
            result.Add(enriched);

            if (!incomplete)
            {
                foreach (var windowHwnd in (frame.ScopedWindows is { Count: > 0 }
                             ? frame.ScopedWindows
                             : [frame.Window]).Select(window => window.Hwnd))
                    knownByWindow.Remove(windowHwnd);
            }
            foreach (var group in StableNativeChromeClosure(enriched.Automation)
                         .GroupBy(control => control.WindowHwnd))
                knownByWindow[group.Key] = group.ToArray();
        }
        return result;
    }

    private static IReadOnlyList<AutomationObservation> StableNativeChromeClosure(
        IReadOnlyList<AutomationObservation> controls)
    {
        var native = controls.Where(control => !IsShadowControl(control) &&
                                               control.WindowHwnd != 0 &&
                                               control.Bounds.Width > 0 && control.Bounds.Height > 0 &&
                                               !control.IsOffscreen)
            .ToArray();
        var windowBounds = native
            .Where(control => NormalizeControlType(control) == "Window")
            .GroupBy(control => control.WindowHwnd)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(control =>
                (long)control.Bounds.Width * control.Bounds.Height).First().Bounds);
        var retainedRuntimeIds = native.Where(control =>
                IsStableNativeChromeForCarry(control,
                    windowBounds.GetValueOrDefault(control.WindowHwnd) ?? new RectI(0, 0, 0, 0)))
            .Select(control => control.RuntimeId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);
        if (retainedRuntimeIds.Count == 0) return [];

        var byRuntimeId = native.Where(control => !string.IsNullOrWhiteSpace(control.RuntimeId))
            .GroupBy(control => control.RuntimeId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var pending = retainedRuntimeIds.ToArray();
        foreach (var runtimeId in pending)
        {
            var current = byRuntimeId.GetValueOrDefault(runtimeId);
            for (var depth = 0; depth < 32 && current is not null &&
                                      !string.IsNullOrWhiteSpace(current.ParentRuntimeId) &&
                                      byRuntimeId.TryGetValue(current.ParentRuntimeId, out var parent); depth++)
            {
                retainedRuntimeIds.Add(parent.RuntimeId);
                current = parent;
            }
        }

        return native.Where(control => retainedRuntimeIds.Contains(control.RuntimeId)).ToArray();
    }

    private static bool IsStableNativeChromeForCarry(AutomationObservation control, RectI windowBounds)
    {
        if (string.IsNullOrWhiteSpace(control.AutomationId) &&
            string.IsNullOrWhiteSpace(control.RuntimeId)) return false;
        var type = NormalizeControlType(control);
        if (type is "MenuBar" or "MenuItem" or "ToolBar") return true;
        if (!control.ClassName.Equals("TAbacreButton", StringComparison.OrdinalIgnoreCase) || !windowBounds.IsValid)
            return false;
        var chromeBottom = windowBounds.Y + Math.Max(96, (int)Math.Ceiling(windowBounds.Height * .22));
        return control.Bounds.Y + control.Bounds.Height <= chromeBottom;
    }

    private static bool SameNativeControlIdentity(
        AutomationObservation left,
        AutomationObservation right)
    {
        if (left.WindowHwnd != right.WindowHwnd) return false;
        if (!string.IsNullOrWhiteSpace(left.RuntimeId) &&
            left.RuntimeId.Equals(right.RuntimeId, StringComparison.Ordinal)) return true;
        return !string.IsNullOrWhiteSpace(left.AutomationId) &&
               left.AutomationId.Equals(right.AutomationId, StringComparison.OrdinalIgnoreCase) &&
               NormalizeControlType(left) == NormalizeControlType(right) &&
               left.ClassName.Equals(right.ClassName, StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<FrameObservation> SuppressRecordedVisualDuplicates(
        IReadOnlyList<FrameObservation> observations)
    {
        var suppressors = observations
            .SelectMany(frame => frame.Automation.Select(control => new
            {
                FrameSequence = frame.Sequence,
                WindowHwnd = EffectiveControlWindowHwnd(frame, control),
                Control = control
            }))
            .Where(item => IsTrustedNativeVisualSuppressor(item.Control) ||
                           IsVisualStructure(item.Control))
            .ToArray();
        if (suppressors.Length == 0) return observations;

        return observations.Select(frame =>
        {
            var retained = frame.Automation.Where(control =>
                !IsVisualHypothesis(control) ||
                !suppressors.Any(suppressor =>
                    (suppressor.FrameSequence == frame.Sequence ||
                     IsStableCrossFrameNativeSuppressor(suppressor.Control)) &&
                    suppressor.WindowHwnd == EffectiveControlWindowHwnd(frame, control) &&
                    !string.Equals(suppressor.Control.RuntimeId, control.RuntimeId, StringComparison.Ordinal) &&
                    SuppressesVisualHypothesis(suppressor.Control, control))).ToArray();
            return retained.Length == frame.Automation.Count
                ? frame
                : frame with { Automation = retained };
        }).ToArray();
    }

    private static long EffectiveControlWindowHwnd(FrameObservation frame, AutomationObservation control) =>
        control.WindowHwnd != 0 ? control.WindowHwnd : EffectiveRootWindowHwnd(frame);

    private static bool IsTrustedNativeVisualSuppressor(AutomationObservation control)
    {
        if (IsShadowControl(control) || control.IsOffscreen ||
            control.Bounds.Width <= 0 || control.Bounds.Height <= 0)
            return false;
        var type = NormalizeControlType(control);
        return type is "Button" or "SplitButton" or "MenuItem" or "Hyperlink" or "CheckBox" or
                   "RadioButton" or "ComboBox" or "Edit" or "ListItem" or "TabItem" or
                   "Header" or "HeaderItem" or "DataItem" or "Table" or "DataGrid" or "List" or "Tree" or "Tab" ||
               control.ClassName.EndsWith("Button", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsVisualHypothesis(AutomationObservation control) =>
        control.ClassName.Equals("UiAtlas.VisualControlRegion", StringComparison.OrdinalIgnoreCase) &&
        control.FrameworkId is "UiAtlas.Visual.Ocr" or "UiAtlas.Visual.Geometry";

    private static bool IsStableCrossFrameNativeSuppressor(AutomationObservation control) =>
        !IsVisualHypothesis(control) &&
        control.ClassName.EndsWith("Button", StringComparison.OrdinalIgnoreCase);

    private static bool IsVisualStructure(AutomationObservation control) =>
        IsVisualHypothesis(control) &&
        (NormalizeControlType(control) is "Table" or "DataGrid" or "Tree" or "Tab" ||
         control.VisualRole is "table" or "tree" or "tab-strip");

    private static bool SuppressesVisualHypothesis(
        AutomationObservation nativeControl,
        AutomationObservation visualControl)
    {
        if (ContainedOverlap(nativeControl.Bounds, visualControl.Bounds) < .78) return false;
        if (IsOpaqueGalleryContainer(nativeControl) &&
            visualControl.ParentRuntimeId.Equals(nativeControl.RuntimeId, StringComparison.Ordinal))
            return false;
        var nativeType = NormalizeControlType(nativeControl);
        if (nativeType is "Table" or "DataGrid" or "List" or "Tree" or "Tab")
            return NormalizeControlType(visualControl) is "Button" or "Edit" or "List";
        return true;
    }

    private static bool IsOpaqueGalleryContainer(AutomationObservation control)
    {
        var type = NormalizeControlType(control);
        var galleryIdentity = control.AutomationId.Contains("Gallery", StringComparison.OrdinalIgnoreCase) ||
                              control.ClassName.Contains("Gallery", StringComparison.OrdinalIgnoreCase) ||
                              control.Name.Contains("Gallery", StringComparison.OrdinalIgnoreCase);
        return galleryIdentity && type is "MenuItem" or "Custom" or "Group" or "List" &&
               (control.SupportedPatterns ?? []).Any(pattern =>
                   pattern.Contains("ExpandCollapse", StringComparison.OrdinalIgnoreCase));
    }

    private static double ContainedOverlap(RectI first, RectI second)
    {
        var width = Math.Max(0,
            Math.Min(first.X + first.Width, second.X + second.Width) - Math.Max(first.X, second.X));
        var height = Math.Max(0,
            Math.Min(first.Y + first.Height, second.Y + second.Height) - Math.Max(first.Y, second.Y));
        var intersection = (long)width * height;
        var smaller = Math.Max(1L,
            Math.Min((long)first.Width * first.Height, (long)second.Width * second.Height));
        return intersection / (double)smaller;
    }

    private static void AddExtractionControlProperties(MutableNode node, FrameObservation frame, AutomationObservation control)
    {
        var candidate = ResolveExtractionCandidate(frame, control);
        if (candidate is null) return;
        node.AddProperty("extractionCandidateId", candidate.CandidateId);
        node.AddProperty("extractionSurfaceId", candidate.SurfaceId);
        node.AddProperty("extractionConfidence", candidate.Confidence.ToString("0.0000", CultureInfo.InvariantCulture));
        node.AddProperty("coverageStatus", candidate.CoverageStatus.ToString());
        node.AddProperty("evidenceConflict", candidate.HasConflict.ToString(CultureInfo.InvariantCulture));
        foreach (var source in candidate.Sources) node.AddProperty("evidenceSource", source.ToString());
        foreach (var evidenceId in candidate.EvidenceIds) node.AddProperty("evidenceId", evidenceId);
    }

    private static MergedControlCandidate? ResolveExtractionCandidate(
        FrameObservation frame,
        AutomationObservation control)
    {
        if (frame.Extraction is not { } extraction) return null;
        var candidate = extraction.Candidates.FirstOrDefault(value =>
            !string.IsNullOrWhiteSpace(control.RuntimeId) && value.Control.RuntimeId == control.RuntimeId &&
            (value.Control.WindowHwnd == control.WindowHwnd || value.Control.WindowHwnd == 0 || control.WindowHwnd == 0));
        candidate ??= extraction.Candidates.FirstOrDefault(value =>
            !string.IsNullOrWhiteSpace(control.AutomationId) &&
            value.Control.AutomationId.Equals(control.AutomationId, StringComparison.OrdinalIgnoreCase) &&
            value.Control.Bounds == control.Bounds);
        return candidate;
    }

    private static string RdsControlLookupKey(long windowHwnd, string runtimeId) => $"{windowHwnd:x}|{runtimeId}";

    private static string NativeRole(FrameObservation frame, WindowObservation window)
    {
        if (window.Hwnd == EffectiveRootWindowHwnd(frame)) return "root";
        if (window.Hwnd == frame.Window.RootOwnerHwnd) return "root-owner";
        return window.Hwnd == window.RootOwnerHwnd && window.OwnerHwnd == 0
            ? "peer-root"
            : "owned";
    }

    private static long EffectiveRootWindowHwnd(FrameObservation frame)
    {
        var windows = frame.ScopedWindows is { Count: > 0 } ? frame.ScopedWindows : [frame.Window];
        var nativeRoot = windows.FirstOrDefault(window => window.Hwnd == frame.Window.RootOwnerHwnd);
        if (nativeRoot is { IsVisible: true, IsCloaked: false, IsMinimized: false, Bounds.Width: > 0, Bounds.Height: > 0 })
            return nativeRoot.Hwnd;

        // Delphi and similar legacy applications keep an invisible TApplication
        // as GA_ROOTOWNER. The largest visible non-tool window is the actual app
        // surface; treating every owned form as a dialog collapses entire pages
        // and strands their visual controls in Raw Data Streams.
        return windows
            .Where(candidate => candidate.Hwnd != frame.Window.RootOwnerHwnd &&
                                candidate.RootOwnerHwnd == frame.Window.RootOwnerHwnd &&
                                candidate.IsVisible && !candidate.IsCloaked && !candidate.IsMinimized &&
                                !candidate.IsToolWindow &&
                                candidate.Bounds.Width > 0 && candidate.Bounds.Height > 0)
            .OrderByDescending(candidate => (long)candidate.Bounds.Width * candidate.Bounds.Height)
            .ThenBy(candidate => candidate.ZOrder)
            .Select(candidate => candidate.Hwnd)
            .FirstOrDefault(frame.Window.RootOwnerHwnd);
    }

    private static IReadOnlyList<AutomationObservation> FilterPromotablePopupAutomation(
        FrameObservation frame,
        IReadOnlyList<WindowObservation> observedWindows)
    {
        var hasVisualFallback = frame.Automation.Any(IsShadowControl);
        if (((frame.AutomationTimedOut || frame.AutomationStatus is not ("ok" or "node-limit")) && !hasVisualFallback) ||
            observedWindows.Count != 1)
            return [];
        var popup = observedWindows[0];
        if (popup.Hwnd == frame.Window.RootOwnerHwnd) return [];

        var candidates = frame.Automation
            .Where(control => (control.WindowHwnd == 0 || control.WindowHwnd == popup.Hwnd) &&
                              IsInsidePopup(control.Bounds, popup.Bounds) &&
                              !IsWorksheetPopupContamination(control))
            .ToArray();
        var root = candidates.FirstOrDefault(control =>
            string.IsNullOrWhiteSpace(control.ParentRuntimeId) &&
            IsPopupSurfaceRoot(control) && PopupSurfaceCoverage(control.Bounds, popup.Bounds) >= 0.72);
        if (root is null || string.IsNullOrWhiteSpace(root.RuntimeId)) return [];

        var accepted = new HashSet<string>(StringComparer.Ordinal) { root.RuntimeId };
        var remaining = candidates.Where(control => control.RuntimeId != root.RuntimeId).ToList();
        var changed = true;
        while (changed)
        {
            changed = false;
            for (var index = remaining.Count - 1; index >= 0; index--)
            {
                var control = remaining[index];
                if (!accepted.Contains(control.ParentRuntimeId)) continue;
                accepted.Add(control.RuntimeId);
                remaining.RemoveAt(index);
                changed = true;
            }
        }

        var result = candidates.Where(control => accepted.Contains(control.RuntimeId)).ToArray();
        return result.Any(control => control.RuntimeId != root.RuntimeId && IsMeaningfulPopupControl(control))
            ? result
            : [];
    }

    private static bool IsPromotableAutomationFrame(FrameObservation frame) =>
        !frame.AutomationTimedOut && frame.AutomationStatus is "ok" or "node-limit" ||
        frame.Automation.Any(IsShadowControl) ||
        HasStableNativeRootChrome(frame);

    private static bool IsScreenVariantFrame(FrameObservation frame) =>
        !string.Equals(frame.ObservationScope, "control-delta", StringComparison.Ordinal) ||
        frame.Trigger.StartsWith("quick-map:", StringComparison.Ordinal) ||
        frame.Trigger.StartsWith("quick-map-screen:", StringComparison.Ordinal);

    private static string VariantObservationScope(FrameObservation frame) =>
        string.Equals(frame.ObservationScope, "control-delta", StringComparison.Ordinal) &&
        (frame.Trigger.StartsWith("quick-map:", StringComparison.Ordinal) ||
         frame.Trigger.StartsWith("quick-map-screen:", StringComparison.Ordinal))
            ? "full-root"
            : frame.ObservationScope;

    private static bool HasStableNativeRootChrome(FrameObservation frame)
    {
        var rootHwnd = EffectiveRootWindowHwnd(frame);
        var rootBounds = (frame.ScopedWindows is { Count: > 0 } ? frame.ScopedWindows : [frame.Window])
            .FirstOrDefault(window => window.Hwnd == rootHwnd)?.Bounds ?? new RectI(0, 0, 0, 0);
        return frame.Automation.Any(control =>
            EffectiveControlWindowHwnd(frame, control) == rootHwnd &&
            IsStableNativeChromeForCarry(control, rootBounds));
    }

    private static bool IsStateVisible(AutomationObservation control) =>
        control.Bounds.Width > 0 && control.Bounds.Height > 0 &&
        (!control.IsOffscreen || IsShadowControl(control));

    private static bool IsOccludedRootVisualFrame(
        FrameObservation frame,
        IReadOnlyList<WindowObservation> windows)
    {
        if (!frame.Automation.Any(IsShadowControl) ||
            !frame.AutomationTimedOut && frame.AutomationStatus is "ok" or "node-limit" ||
            !string.Equals(frame.Trigger, "adaptive-root-change", StringComparison.Ordinal))
            return false;

        var rootHwnd = EffectiveRootWindowHwnd(frame);
        var root = windows.FirstOrDefault(window => window.Hwnd == rootHwnd);
        if (root is null) return false;
        return windows.Any(window =>
            window.Hwnd != rootHwnd &&
            NativeRole(frame, window) == "owned" &&
            window.IsVisible && !window.IsCloaked && !window.IsMinimized &&
            window.Bounds.Width > 0 && window.Bounds.Height > 0 &&
            ClassifySurface(window, "owned", root.Bounds) == "RawDialogWindow");
    }

    private static bool HasPromotablePeerRootNativeEvidence(
        FrameObservation frame,
        WindowObservation window) =>
        window.Hwnd != frame.Window.RootOwnerHwnd &&
        window.Hwnd == window.RootOwnerHwnd &&
        window.OwnerHwnd == 0 &&
        !string.IsNullOrWhiteSpace(window.Title) &&
        !string.IsNullOrWhiteSpace(frame.FrameEntry);

    private static bool IsMeaningfulPopupControl(AutomationObservation control)
    {
        if (control.IsOffscreen || control.Bounds.Width <= 0 || control.Bounds.Height <= 0 ||
            IsWorksheetPopupContamination(control)) return false;
        var type = NormalizeControlType(control);
        if (type is "Menu" or "Window" or "Pane" or "Group" or "DataGrid" or "Image" or
            "Separator" or "ToolBar") return false;
        if (type == "Text") return !string.IsNullOrWhiteSpace(control.Name);
        return type is "Button" or "CheckBox" or "ComboBox" or "DataItem" or "Edit" or "Hyperlink" or
            "List" or "ListItem" or "MenuItem" or "RadioButton" or "ScrollBar" or "Slider" or "Spinner" or
            "SplitButton" or "TabItem" or "Thumb" or "Tree" or "TreeItem" or "Custom" ||
            control.SupportedPatterns is { Count: > 0 };
    }

    private static bool HasPromotablePopupContent(
        WindowObservation popup,
        IReadOnlyList<AutomationObservation> controls)
    {
        var candidates = controls
            .Where(control => IsInsidePopup(control.Bounds, popup.Bounds) &&
                              !IsWorksheetPopupContamination(control) &&
                              !string.IsNullOrWhiteSpace(control.RuntimeId))
            .ToArray();
        if (candidates.Length < 2) return false;

        var byRuntime = candidates
            .GroupBy(control => control.RuntimeId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        return candidates.Any(control =>
            IsMeaningfulPopupControl(control) &&
            !string.IsNullOrWhiteSpace(control.ParentRuntimeId) &&
            byRuntime.ContainsKey(control.ParentRuntimeId));
    }

    private static bool HasPromotableDialogContent(
        WindowObservation dialog,
        IReadOnlyList<AutomationObservation> controls)
    {
        var candidates = controls
            .Where(control => !control.IsOffscreen &&
                              IsInsidePopup(control.Bounds, dialog.Bounds) &&
                              !IsWorksheetPopupContamination(control))
            .ToArray();
        if (candidates.Length < 2) return false;

        var roots = candidates
            .Where(control => string.IsNullOrWhiteSpace(control.ParentRuntimeId) &&
                              SurfaceAnchorMatches(control, dialog.Bounds) &&
                              NormalizeControlType(control) is "Window" or "Pane" or "Custom")
            .Select(control => control.RuntimeId)
            .Where(runtimeId => !string.IsNullOrWhiteSpace(runtimeId))
            .ToHashSet(StringComparer.Ordinal);
        if (roots.Count == 0) return false;

        var connected = new HashSet<string>(roots, StringComparer.Ordinal);
        var remaining = candidates
            .Where(control => !roots.Contains(control.RuntimeId))
            .ToList();
        var changed = true;
        while (changed)
        {
            changed = false;
            for (var index = remaining.Count - 1; index >= 0; index--)
            {
                if (!connected.Contains(remaining[index].ParentRuntimeId)) continue;
                connected.Add(remaining[index].RuntimeId);
                remaining.RemoveAt(index);
                changed = true;
            }
        }

        return candidates.Any(control => connected.Contains(control.RuntimeId) &&
            !roots.Contains(control.RuntimeId) && IsMeaningfulDialogControl(control));
    }

    private static bool IsExplicitDialogCapture(FrameObservation frame, WindowObservation window) =>
        frame.Trigger.StartsWith("adaptive-dialog:", StringComparison.Ordinal) &&
        frame.ObservedWindowHwnds?.Contains(window.Hwnd) == true &&
        window.ProcessId == frame.Window.ProcessId &&
        frame.Automation.Any(control =>
            control.WindowHwnd == window.Hwnd &&
            string.IsNullOrWhiteSpace(control.ParentRuntimeId) &&
            SurfaceAnchorMatches(control, window.Bounds));

    private static bool IsMeaningfulDialogControl(AutomationObservation control)
    {
        if (control.Bounds.Width <= 0 || control.Bounds.Height <= 0 || control.IsOffscreen)
            return false;
        var type = NormalizeControlType(control);
        return type is "Button" or "CheckBox" or "ComboBox" or "Edit" or "List" or "ListItem" or
            "RadioButton" or "Slider" or "Spinner" or "Tab" or "TabItem" or "Tree" or "TreeItem";
    }

    private static bool IsWorksheetPopupContamination(AutomationObservation control)
    {
        var type = NormalizeControlType(control);
        return type is "Document" ||
               control.ClassName.StartsWith("XLSpreadsheet", StringComparison.OrdinalIgnoreCase) ||
               control.ClassName.StartsWith("XLGrid", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPopupSurfaceRoot(AutomationObservation control)
    {
        var type = NormalizeControlType(control);
        return type is "Menu" or "List" or "Tree" or "Window" or "Pane" or "Custom" or "ToolBar";
    }

    private static double PopupSurfaceCoverage(RectI candidate, RectI popup)
    {
        var left = Math.Max(candidate.X, popup.X);
        var top = Math.Max(candidate.Y, popup.Y);
        var right = Math.Min(candidate.X + candidate.Width, popup.X + popup.Width);
        var bottom = Math.Min(candidate.Y + candidate.Height, popup.Y + popup.Height);
        if (right <= left || bottom <= top || popup.Width <= 0 || popup.Height <= 0) return 0;
        return (right - left) * (double)(bottom - top) / (popup.Width * (double)popup.Height);
    }

    private static bool IsInsidePopup(RectI bounds, RectI popupBounds) =>
        bounds.Width > 0 && bounds.Height > 0 &&
        bounds.X >= popupBounds.X - 8 && bounds.Y >= popupBounds.Y - 8 &&
        bounds.X + bounds.Width <= popupBounds.X + popupBounds.Width + 8 &&
        bounds.Y + bounds.Height <= popupBounds.Y + popupBounds.Height + 8;

    private static IReadOnlyDictionary<long, IReadOnlyList<AutomationObservation>> AssignEffectiveRawControlOwners(
        FrameObservation frame,
        IReadOnlyList<WindowObservation> windows,
        IReadOnlyList<AutomationObservation> controls)
    {
        var windowIds = windows.Select(window => window.Hwnd).ToHashSet();
        var targetByIndex = controls.Select(control =>
                control.WindowHwnd != 0 && windowIds.Contains(control.WindowHwnd)
                    ? control.WindowHwnd
                    : frame.Window.RootOwnerHwnd)
            .ToArray();
        var claimed = new HashSet<int>();
        var childrenByParent = controls
            .Select((control, index) => new { control, index })
            .Where(item => !string.IsNullOrWhiteSpace(item.control.ParentRuntimeId))
            .GroupBy(item => $"{item.control.WindowHwnd:x}|{item.control.ParentRuntimeId}", StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(item => item.index).ToArray(), StringComparer.Ordinal);

        foreach (var window in windows
                     .Where(candidate => candidate.Hwnd != frame.Window.RootOwnerHwnd)
                     .OrderByDescending(candidate => OwnershipDepth(candidate, windows))
                     .ThenBy(candidate => Math.Max(1L, (long)candidate.Bounds.Width * candidate.Bounds.Height))
                     .ThenBy(candidate => candidate.Hwnd))
        {
            var candidates = controls.Select((control, index) => new { control, index })
                .Where(item => SurfaceAnchorMatches(item.control, window.Bounds))
                .Select(item => new
                {
                    item.index,
                    Members = CollectRuntimeSubtree(item.index, controls, childrenByParent),
                    ExplicitOwner = item.control.WindowHwnd == window.Hwnd
                })
                .OrderByDescending(candidate => candidate.Members.Count)
                .ThenByDescending(candidate => candidate.ExplicitOwner)
                .ThenBy(candidate => candidate.index)
                .ToArray();
            var best = candidates.FirstOrDefault();
            if (best is null) continue;
            foreach (var index in best.Members)
            {
                if (claimed.Add(index)) targetByIndex[index] = window.Hwnd;
            }
        }

        return controls.Select((control, index) => new { control, Target = targetByIndex[index] })
            .Where(item => windowIds.Contains(item.Target))
            .GroupBy(item => item.Target)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<AutomationObservation>)group.Select(item => item.control).ToArray());
    }

    private static int OwnershipDepth(WindowObservation window, IReadOnlyList<WindowObservation> windows)
    {
        var byHwnd = windows.ToDictionary(candidate => candidate.Hwnd);
        var depth = 0;
        var owner = window.OwnerHwnd;
        var visited = new HashSet<long>();
        while (owner != 0 && visited.Add(owner) && byHwnd.TryGetValue(owner, out var parent))
        {
            depth++;
            owner = parent.OwnerHwnd;
        }
        return depth;
    }

    private static IReadOnlyList<int> CollectRuntimeSubtree(
        int rootIndex,
        IReadOnlyList<AutomationObservation> controls,
        IReadOnlyDictionary<string, int[]> childrenByParent)
    {
        var result = new List<int>();
        var pending = new Stack<int>();
        var visited = new HashSet<int>();
        pending.Push(rootIndex);
        while (pending.Count > 0)
        {
            var index = pending.Pop();
            if (!visited.Add(index)) continue;
            result.Add(index);
            var control = controls[index];
            if (string.IsNullOrWhiteSpace(control.RuntimeId)) continue;
            if (!childrenByParent.TryGetValue($"{control.WindowHwnd:x}|{control.RuntimeId}", out var children)) continue;
            for (var child = children.Length - 1; child >= 0; child--) pending.Push(children[child]);
        }
        return result;
    }

    private static bool SurfaceAnchorMatches(AutomationObservation control, RectI surface)
    {
        if (control.IsOffscreen || control.Bounds.Width <= 0 || control.Bounds.Height <= 0 ||
            surface.Width <= 0 || surface.Height <= 0)
            return false;
        var toleranceX = Math.Max(4, surface.Width / 50);
        var toleranceY = Math.Max(4, surface.Height / 50);
        return Math.Abs((long)control.Bounds.X - surface.X) <= toleranceX &&
               Math.Abs((long)control.Bounds.Y - surface.Y) <= toleranceY &&
               Math.Abs((long)control.Bounds.Width - surface.Width) <= toleranceX * 2L &&
               Math.Abs((long)control.Bounds.Height - surface.Height) <= toleranceY * 2L;
    }

    private static IReadOnlyList<FrameControl> MaterializeControls(
        RecordingManifest manifest,
        FrameObservation frame,
        WindowObservation window,
        RawSurfaceInfo surface,
        IReadOnlyList<AutomationObservation> controls,
        IDictionary<string, MutableNode> nodes,
        IDictionary<string, MutableEdge> edges,
        IDictionary<string, RawControlInfo> rawControls)
    {
        var effectivelyVisible = AutomationObservationVisibility.FilterEffectivelyVisible(controls).ToHashSet();
        var ordered = controls
            .OrderBy(control => control.Bounds.Y)
            .ThenBy(control => control.Bounds.X)
            .ThenBy(control => BaseControlSignature(control, window.Bounds), StringComparer.Ordinal)
            .ThenBy(control => control.RuntimeId, StringComparer.Ordinal)
            .Select((control, ordinal) => new IndexedControl(control, $"item-{ordinal:D6}"))
            .ToArray();
        var byRuntime = ordered
            .Where(item => !string.IsNullOrWhiteSpace(item.Observation.RuntimeId))
            .GroupBy(item => item.Observation.RuntimeId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var basePaths = new Dictionary<string, string>(StringComparer.Ordinal);
        string ResolveBasePath(IndexedControl item, HashSet<string>? visiting = null)
        {
            if (basePaths.TryGetValue(item.InstanceKey, out var cached)) return cached;
            visiting ??= new(StringComparer.Ordinal);
            if (!visiting.Add(item.InstanceKey)) return BaseControlSignature(item.Observation, window.Bounds);
            // Legacy toolbar button parent panes are frequently re-reported with
            // different cached runtime paths even though the stable automation ID
            // and native class remain unchanged. Keep that chrome identity rooted
            // at the surface so one button does not split into per-frame clones.
            var parentPath = !IsSurfaceRootedStableControl(item.Observation) &&
                             !string.IsNullOrWhiteSpace(item.Observation.ParentRuntimeId) &&
                             byRuntime.TryGetValue(item.Observation.ParentRuntimeId, out var parent)
                ? ResolveBasePath(parent, visiting)
                : string.Empty;
            visiting.Remove(item.InstanceKey);
            var path = string.IsNullOrEmpty(parentPath)
                ? BaseControlSignature(item.Observation, window.Bounds)
                : $"{parentPath}/{BaseControlSignature(item.Observation, window.Bounds)}";
            basePaths[item.InstanceKey] = path;
            return path;
        }

        foreach (var item in ordered) ResolveBasePath(item);
        var finalPathByInstance = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var group in ordered.GroupBy(item => basePaths[item.InstanceKey], StringComparer.Ordinal))
        {
            var members = group.OrderBy(item => item.Observation.Bounds.Y).ThenBy(item => item.Observation.Bounds.X)
                .ThenBy(item => item.Observation.RuntimeId, StringComparer.Ordinal).ThenBy(item => item.InstanceKey, StringComparer.Ordinal).ToArray();
            for (var index = 0; index < members.Length; index++)
                finalPathByInstance[members[index].InstanceKey] = index == 0 ? group.Key : $"{group.Key}#{index + 1}";
        }

        var idByInstance = finalPathByInstance.ToDictionary(
            pair => pair.Key,
            pair => StableIdentity.Create("control", RawLayer, surface.Id, pair.Value),
            StringComparer.Ordinal);
        var idByRuntime = ordered
            .Where(item => !string.IsNullOrWhiteSpace(item.Observation.RuntimeId))
            .GroupBy(item => item.Observation.RuntimeId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => idByInstance[group.First().InstanceKey], StringComparer.Ordinal);
        var result = new List<FrameControl>(ordered.Length);
        foreach (var item in ordered)
        {
            var control = item.Observation;
            var path = finalPathByInstance[item.InstanceKey];
            var id = idByInstance[item.InstanceKey];
            var parentId = !IsSurfaceRootedStableControl(control) &&
                           !string.IsNullOrWhiteSpace(control.ParentRuntimeId) &&
                           idByRuntime.TryGetValue(control.ParentRuntimeId, out var resolvedParent)
                ? resolvedParent
                : surface.Id;
            var evidence = Evidence(manifest, frame, control.Bounds);
            var label = StableDisplay(control);
            var node = GetNode(nodes, id, GraphNodeKind.Control, parentId, id, label);
            node.AddEvidence(evidence);
            node.AddProperty("layer", RawLayer);
            node.AddProperty("rawSurfaceId", surface.Id);
            node.AddProperty("controlPath", path);
            node.AddProperty("automationId", control.AutomationId);
            node.AddProperty("name", control.Name, sensitive: true);
            node.AddProperty("controlType", NormalizeControlType(control));
            node.AddProperty("className", control.ClassName);
            node.AddProperty("frameworkId", control.FrameworkId);
            node.AddProperty("enabled", control.IsEnabled.ToString(CultureInfo.InvariantCulture));
            node.AddProperty("offscreen", control.IsOffscreen.ToString(CultureInfo.InvariantCulture));
            node.AddProperty("hasKeyboardFocus", control.HasKeyboardFocus.ToString(CultureInfo.InvariantCulture));
            node.AddProperty("selected", control.IsSelected.ToString(CultureInfo.InvariantCulture));
            node.AddProperty("focused", control.HasKeyboardFocus.ToString(CultureInfo.InvariantCulture));
            node.AddProperty("toggleState", control.ToggleState);
            node.AddProperty("expandCollapseState", control.ExpandCollapseState);
            foreach (var pattern in control.SupportedPatterns ?? []) node.AddProperty("supportedPattern", pattern);
            foreach (var selector in StableSelectors(control)) node.AddProperty("stableSelector", selector);
            AddExtractionControlProperties(node, frame, control);
            AddVisualIdentityProperties(node, control);
            AddContains(edges, parentId, id, evidence);

            if (!rawControls.TryGetValue(id, out var info))
            {
                info = new(id, surface.Id, parentId == surface.Id ? null : parentId, path, control,
                    effectivelyVisible.Contains(control));
                rawControls.Add(id, info);
            }
            else
            {
                info.Observe(control, effectivelyVisible.Contains(control));
            }
            info.ObserveExtraction(ResolveExtractionCandidate(frame, control));
            info.Evidence.Add(evidence);
            result.Add(new(id, control));
        }
        return result;
    }

    private static bool IsSurfaceRootedStableControl(AutomationObservation control) =>
        !string.IsNullOrWhiteSpace(control.AutomationId) &&
        (control.ClassName.Equals("TAbacreButton", StringComparison.OrdinalIgnoreCase) ||
         NormalizeControlType(control) is "MenuBar" or "ToolBar");

    private static void MaterializeSemanticWorld(
        string appId,
        string processName,
        IDictionary<string, MutableNode> nodes,
        IDictionary<string, MutableEdge> edges,
        IReadOnlyDictionary<string, RawSurfaceInfo> rawSurfaces,
        IReadOnlyDictionary<string, RawControlInfo> rawControls)
    {
        var semanticSurfaceByRaw = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in rawSurfaces.Values.OrderBy(surface => surface.Id, StringComparer.Ordinal))
        {
            string parentId = appId;
            string semanticKind;
            string semanticSurfaceId;
            if (raw.SurfaceClass == "RawPopupWindow")
            {
                var ownerToken = raw.OwnerRawSurfaceId ?? appId;
                var familyId = StableIdentity.Create("surface", SemanticLayer, "popup-family", ownerToken, raw.ClassName);
                var family = GetNode(nodes, familyId, GraphNodeKind.Surface, appId, familyId, "Popup family");
                family.AddProperty("layer", SemanticLayer);
                family.AddProperty("semanticSurfaceKind", "PopupFamily");
                family.AddProperty("className", raw.ClassName);
                foreach (var evidence in raw.Evidence) family.AddEvidence(evidence);
                family.AddProperty("sourceRawSurfaceId", raw.Id);
                AddContains(edges, appId, familyId, raw.Evidence.FirstOrDefault());
                parentId = familyId;
                semanticKind = "PopupVariant";
                semanticSurfaceId = StableIdentity.Create("surface", SemanticLayer, familyId, raw.Fingerprint);
            }
            else
            {
                semanticKind = "Window";
                semanticSurfaceId = StableIdentity.Create("surface", SemanticLayer, raw.StableKey);
            }

            var semantic = GetNode(nodes, semanticSurfaceId, GraphNodeKind.Surface, parentId, semanticSurfaceId,
                SemanticSurfaceDisplay(raw, processName, rawControls.Values.Where(control => control.RawSurfaceId == raw.Id)));
            semantic.AddProperty("layer", SemanticLayer);
            semantic.AddProperty("semanticSurfaceKind", semanticKind);
            semantic.AddProperty("semanticClass", SemanticSurfaceClass(raw.SurfaceClass));
            semantic.AddProperty("surfaceClass", raw.SurfaceClass);
            semantic.AddProperty("className", raw.ClassName);
            semantic.AddProperty("sourceRawSurfaceId", raw.Id);
            if (parentId != appId) semantic.AddProperty("semanticPopupFamilyId", parentId);
            if (!string.IsNullOrWhiteSpace(raw.OwnerRawSurfaceId)) semantic.AddProperty("sourceOwnerRawSurfaceId", raw.OwnerRawSurfaceId!);
            foreach (var evidence in raw.Evidence) semantic.AddEvidence(evidence);
            AddContains(edges, parentId, semanticSurfaceId, raw.Evidence.FirstOrDefault());
            semanticSurfaceByRaw[raw.Id] = semanticSurfaceId;
        }

        var semanticControlByRaw = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in rawControls.Values.OrderBy(control => control.Path.Count(character => character == '/'))
                     .ThenBy(control => control.Path, StringComparer.Ordinal))
        {
            if (!semanticSurfaceByRaw.TryGetValue(raw.RawSurfaceId, out var semanticSurfaceId)) continue;
            var parentId = raw.ParentRawControlId is not null && semanticControlByRaw.TryGetValue(raw.ParentRawControlId, out var semanticParent)
                ? semanticParent
                : semanticSurfaceId;
            var semanticId = StableIdentity.Create("control", SemanticLayer, semanticSurfaceId, SemanticControlSignature(raw.Observation, raw.Path));
            semanticControlByRaw[raw.Id] = semanticId;
            var node = GetNode(nodes, semanticId, GraphNodeKind.Control, parentId, semanticId, StableDisplay(raw.Observation));
            node.AddProperty("layer", SemanticLayer);
            node.AddProperty("semanticSurfaceId", semanticSurfaceId);
            node.AddProperty("sourceRawControlId", raw.Id);
            node.AddProperty("automationId", raw.Observation.AutomationId);
            node.AddProperty("name", raw.Observation.Name, sensitive: true);
            node.AddProperty("controlType", NormalizeControlType(raw.Observation));
            node.AddProperty("className", raw.Observation.ClassName);
            node.AddProperty("frameworkId", raw.Observation.FrameworkId);
            node.AddProperty("controlPath", raw.Path);
            node.AddProperty("semanticControlKind", NormalizeControlType(raw.Observation));
            node.AddProperty("offscreen", (!raw.WasObservedVisible).ToString(CultureInfo.InvariantCulture));
            node.AddProperty("effectivelyVisible", raw.WasObservedVisible.ToString(CultureInfo.InvariantCulture));
            node.AddProperty("verificationStatus", raw.WasConfirmed
                ? "Confirmed"
                : raw.WasObservedVisible ? "Observed" : "Unverified");
            foreach (var pattern in raw.Observation.SupportedPatterns ?? []) node.AddProperty("supportedPattern", pattern);
            foreach (var selector in StableSelectors(raw.Observation)) node.AddProperty("stableSelector", selector);
            foreach (var candidateId in raw.ExtractionCandidateIds) node.AddProperty("extractionCandidateId", candidateId);
            foreach (var extractionSurfaceId in raw.ExtractionSurfaceIds) node.AddProperty("extractionSurfaceId", extractionSurfaceId);
            foreach (var evidenceSource in raw.EvidenceSources) node.AddProperty("evidenceSource", evidenceSource);
            foreach (var evidenceId in raw.ExtractionEvidenceIds) node.AddProperty("evidenceId", evidenceId);
            foreach (var coverageStatus in raw.ExtractionCoverageStatuses) node.AddProperty("coverageStatus", coverageStatus);
            if (raw.MaximumExtractionConfidence is { } confidence)
                node.AddProperty("extractionConfidence", confidence.ToString("0.0000", CultureInfo.InvariantCulture));
            if (raw.HasExtractionConflict) node.AddProperty("evidenceConflict", bool.TrueString);
            AddVisualIdentityProperties(node, raw.Observation);
            foreach (var evidence in raw.Evidence) node.AddEvidence(evidence);
            AddContains(edges, node.ParentId, semanticId, raw.Evidence.FirstOrDefault());
        }

        foreach (var rawPopup in rawSurfaces.Values.Where(surface =>
                     surface.SurfaceClass == "RawPopupWindow" &&
                     !string.IsNullOrWhiteSpace(surface.InteractionSourceRawControlId)))
        {
            if (!semanticSurfaceByRaw.TryGetValue(rawPopup.Id, out var semanticPopupId) ||
                !semanticControlByRaw.TryGetValue(rawPopup.InteractionSourceRawControlId!, out var semanticSourceId))
                continue;
            nodes[semanticPopupId].AddProperty("interactionSourceControlId", semanticSourceId);
            nodes[semanticPopupId].AddProperty("interactionSourceRawControlId", rawPopup.InteractionSourceRawControlId!);
            var opens = GetEdge(edges,
                StableIdentity.Create("edge", semanticSourceId, semanticPopupId, "opens-popup"),
                "opens-popup", semanticSourceId, semanticPopupId);
            opens.AddProperty("relationship", "opens popup");
            foreach (var evidence in rawPopup.Evidence) opens.AddEvidence(evidence);
        }
    }

    private static RawControlInfo? ResolveInteractionSource(
        IEnumerable<RawControlInfo> controls,
        IReadOnlyDictionary<string, RawSurfaceInfo> surfaces,
        string? ownerRawSurfaceId,
        AutomationObservation source,
        string? bundleId = null,
        long? frameSequence = null)
    {
        var candidates = controls
            .Where(control => ownerRawSurfaceId is null || control.RawSurfaceId == ownerRawSurfaceId ||
                              surfaces.GetValueOrDefault(control.RawSurfaceId)?.Role == "root")
            .Where(control => bundleId is null || frameSequence is null || control.Evidence.Any(evidence =>
                evidence.BundleId == bundleId && evidence.FrameSequence == frameSequence))
            .Select(control => new
            {
                Control = control,
                Score = InteractionSourceMatchScore(control.Observation, source)
            })
            .Where(item => item.Score > 0)
            .OrderByDescending(item => item.Score)
            .ThenBy(item => BoundsDistance(item.Control.Observation.Bounds, source.Bounds))
            .FirstOrDefault();
        return candidates?.Control;
    }

    private static int InteractionSourceMatchScore(AutomationObservation candidate, AutomationObservation source)
    {
        var score = 0;
        if (!string.IsNullOrWhiteSpace(source.RuntimeId) && candidate.RuntimeId == source.RuntimeId) score += 100;
        if (!string.IsNullOrWhiteSpace(source.AutomationId) &&
            candidate.AutomationId.Equals(source.AutomationId, StringComparison.OrdinalIgnoreCase)) score += 40;
        if (!string.IsNullOrWhiteSpace(source.Name) &&
            candidate.Name.Equals(source.Name, StringComparison.OrdinalIgnoreCase)) score += 25;
        if (NormalizeControlType(candidate) == NormalizeControlType(source)) score += 10;
        if (candidate.Bounds == source.Bounds) score += 30;
        else if (BoundsDistance(candidate.Bounds, source.Bounds) <= 16) score += 15;
        return score;
    }

    private static long BoundsDistance(RectI left, RectI right) =>
        Math.Abs((long)left.X - right.X) + Math.Abs((long)left.Y - right.Y) +
        Math.Abs((long)left.Width - right.Width) + Math.Abs((long)left.Height - right.Height);

    private static string ClassifySurface(WindowObservation window, string role, RectI rootBounds)
    {
        if (role is "root" or "peer-root") return "RawWindow";
        if (string.Equals(window.ClassName, "#32770", StringComparison.OrdinalIgnoreCase)) return "RawDialogWindow";
        // Office dialog windows can briefly carry WS_EX_TOPMOST while opening
        // and drop it after the first tab interaction. That transient bit must
        // not turn the first dialog page into a separate popup surface.
        if (window.ClassName.StartsWith("bosa_sdm_", StringComparison.OrdinalIgnoreCase)) return "RawDialogWindow";
        if (string.Equals(window.ClassName, "#32768", StringComparison.OrdinalIgnoreCase)) return "RawPopupWindow";
        if (LooksLikeTransientPopup(window, rootBounds)) return "RawPopupWindow";
        if (window.IsToolWindow) return "RawToolWindow";
        return "RawDialogWindow";
    }

    private static bool LooksLikeTransientPopup(WindowObservation window, RectI rootBounds)
    {
        var token = $"{window.ClassName} {window.Title}";
        var hasPopupToken = new[] { "netui", "popup", "flyout", "dropdown", "tooltip", "menu" }
            .Any(value => token.Contains(value, StringComparison.OrdinalIgnoreCase));
        var area = (long)Math.Max(0, window.Bounds.Width) * Math.Max(0, window.Bounds.Height);
        var rootArea = (long)Math.Max(0, rootBounds.Width) * Math.Max(0, rootBounds.Height);
        var transientGeometry = rootArea > 0 && area > 0 && area <= rootArea * 45 / 100;
        const long framedStyle = 0x00C00000L | 0x00040000L | 0x00800000L | 0x00400000L;
        var borderless = (window.Style & framedStyle) == 0;
        return hasPopupToken ||
               window.IsToolWindow && (window.IsTopMost || transientGeometry || borderless) ||
               window.IsTopMost && transientGeometry;
    }

    private static string BuildSurfaceFingerprint(WindowObservation window, IReadOnlyList<AutomationObservation> controls)
    {
        var signatures = controls
            .Where(control => !control.IsOffscreen)
            .OrderBy(control => control.Bounds.Y)
            .ThenBy(control => control.Bounds.X)
            .Select(control => string.Join('@', BaseControlSignature(control, window.Bounds), RelativeGeometryToken(control.Bounds, window.Bounds)))
            .Distinct(StringComparer.Ordinal)
            .Take(64)
            .ToArray();
        return StableIdentity.Create("fingerprint", window.ClassName, window.Style.ToString("x", CultureInfo.InvariantCulture),
            window.ExStyle.ToString("x", CultureInfo.InvariantCulture), string.Join('|', signatures));
    }

    private static string BuildSurfaceIdentityFingerprint(
        WindowObservation window,
        string surfaceClass,
        IReadOnlyList<AutomationObservation> controls)
    {
        if (!string.Equals(surfaceClass, "RawDialogWindow", StringComparison.Ordinal))
            return BuildSurfaceFingerprint(window, controls);

        // Dialog pages and selected tabs are states of one native dialog, not
        // separate surfaces. Using all descendant controls in the identity split
        // Excel's Format Cells/Page Setup dialogs every time their tab changed.
        // Keep the shell identity stable; materially different contents remain
        // independently addressable as state variants below that surface.
        var title = StableIdentity.Normalize(window.Title);
        if (string.IsNullOrWhiteSpace(title)) title = "untitled-dialog";
        const long topMostExtendedStyle = 0x00000008L;
        var stableExStyle = window.ExStyle & ~topMostExtendedStyle;
        return StableIdentity.Create(
            "dialog-shell",
            title,
            window.Style.ToString("x", CultureInfo.InvariantCulture),
            stableExStyle.ToString("x", CultureInfo.InvariantCulture));
    }

    private static string BaseControlSignature(AutomationObservation control, RectI windowBounds)
    {
        var type = NormalizeControlType(control);
        var automation = StableToken(control.AutomationId);
        var className = StableToken(control.ClassName);
        // Hover discovery deliberately starts as a disabled Custom control and is
        // promoted to a Button after a real click. Keep both observations on the
        // same raw path so confirmation replaces the shadow node instead of
        // creating a second, overlapping control.
        if (IsShadowControl(control) && automation.Length > 0)
            return $"shadow-region:aid:{automation}:class:{className}";
        if (automation.Length > 0) return $"{type}:aid:{automation}:class:{className}";
        return $"{type}:class:{className}:slot:{RelativeGeometryToken(control.Bounds, windowBounds)}";
    }

    private static string SemanticControlSignature(AutomationObservation control, string rawPath)
        => IsShadowControl(control)
            ? string.Join('|', "shadow-region", StableToken(control.AutomationId),
                StableToken(control.ClassName), rawPath)
            : string.Join('|', NormalizeControlType(control), StableToken(control.AutomationId),
                StableToken(control.ClassName), StableToken(control.FrameworkId), rawPath);

    private static bool IsShadowControl(AutomationObservation control) =>
        control.ClassName.Equals("UiAtlas.HoverRegion", StringComparison.OrdinalIgnoreCase) &&
        (control.AutomationId.StartsWith("shadow:", StringComparison.OrdinalIgnoreCase) ||
         control.RuntimeId.StartsWith("shadow-hover:", StringComparison.OrdinalIgnoreCase)) ||
        control.ClassName.Equals("UiAtlas.VisualControlRegion", StringComparison.OrdinalIgnoreCase) &&
        (control.AutomationId.StartsWith("visual:", StringComparison.OrdinalIgnoreCase) ||
         control.RuntimeId.StartsWith("visual:", StringComparison.OrdinalIgnoreCase));

    private static (string Version, string Fingerprint)? VisualFingerprint(AutomationObservation control)
    {
        var identity = !string.IsNullOrWhiteSpace(control.AutomationId)
            ? control.AutomationId
            : control.RuntimeId;
        foreach (var version in new[] { "v3", "v2" })
        {
            var prefix = $"visual:{version}:";
            if (!identity.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var fingerprint = identity[prefix.Length..];
            return fingerprint.Length > 0 && fingerprint.All(Uri.IsHexDigit)
                ? (version, fingerprint.ToLowerInvariant())
                : null;
        }
        return null;
    }

    private static void AddVisualIdentityProperties(MutableNode node, AutomationObservation control)
    {
        if (VisualFingerprint(control) is not { } visualIdentity) return;
        node.AddProperty("identityBasis", visualIdentity.Version == "v3"
            ? "visual-semantic-v3"
            : "visual-perceptual-v2");
        node.AddProperty("visualFingerprint", visualIdentity.Fingerprint);
        node.AddProperty("coordinateInvariant", bool.TrueString);
        node.AddProperty("scaleInvariant", bool.TrueString);
        node.AddProperty("visualRole", control.VisualRole);
        node.AddProperty("ocrText", control.OcrText, sensitive: true);
        node.AddProperty("visualGroupId", control.VisualGroupId);
        if (control.TableRow is { } row)
            node.AddProperty("tableRow", row.ToString(CultureInfo.InvariantCulture));
        if (control.TableColumn is { } column)
            node.AddProperty("tableColumn", column.ToString(CultureInfo.InvariantCulture));
    }

    private static IEnumerable<string> StableSelectors(AutomationObservation control)
    {
        if (!string.IsNullOrWhiteSpace(control.AutomationId)) yield return $"automationId:{control.AutomationId.Trim()}";
        if (!string.IsNullOrWhiteSpace(control.ClassName)) yield return $"className:{control.ClassName.Trim()}";
        if (!string.IsNullOrWhiteSpace(control.FrameworkId)) yield return $"frameworkId:{control.FrameworkId.Trim()}";
        if (!string.IsNullOrWhiteSpace(control.ControlType)) yield return $"controlType:{NormalizeControlType(control)}";
    }

    private static string StableDisplay(AutomationObservation control)
    {
        if (!string.IsNullOrWhiteSpace(control.Name) && control.Name != "[redacted]") return control.Name;
        if (!string.IsNullOrWhiteSpace(control.AutomationId)) return control.AutomationId;
        return NormalizeControlType(control);
    }

    private static string ResolveStateContextLabel(IEnumerable<FrameControl> controls)
    {
        var selectedTab = controls
            .Select(control => control.Observation)
            .Where(control => control.IsSelected &&
                              string.Equals(NormalizeControlType(control), "TabItem", StringComparison.OrdinalIgnoreCase))
            .Select(StableDisplay)
            .FirstOrDefault(label => !string.IsNullOrWhiteSpace(label) && !string.Equals(label, "TabItem", StringComparison.OrdinalIgnoreCase));
        return selectedTab ?? string.Empty;
    }

    private static string NormalizeControlType(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        return text.StartsWith("ControlType.", StringComparison.OrdinalIgnoreCase) ? text[12..] : text.Length == 0 ? "Unknown" : text;
    }

    private static string NormalizeControlType(AutomationObservation control)
    {
        var type = NormalizeControlType(control.ControlType);
        return type is "Pane" or "Custom" &&
               !string.IsNullOrWhiteSpace(control.Name) &&
               control.ClassName.EndsWith("Button", StringComparison.OrdinalIgnoreCase)
            ? "Button"
            : type;
    }

    private static string StableToken(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        if (text.Length == 0 || text == "[redacted]" || Guid.TryParse(text, out _)) return string.Empty;
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && text[2..].All(Uri.IsHexDigit)) return string.Empty;
        if (text.Length >= 8 && text.All(Uri.IsHexDigit)) return string.Empty;
        var builder = new StringBuilder(text.Length);
        var separator = false;
        foreach (var character in text.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(character);
                separator = false;
            }
            else if (!separator)
            {
                builder.Append('-');
                separator = true;
            }
        }
        return builder.ToString().Trim('-');
    }

    private static string RelativeGeometryToken(RectI value, RectI container)
    {
        static int Ratio(int value, int total) => total <= 0 ? 0 : Math.Clamp((int)Math.Round(value * 1000d / total), -4000, 4000);
        return string.Join(',', Ratio(value.X - container.X, container.Width), Ratio(value.Y - container.Y, container.Height),
            Ratio(value.Width, container.Width), Ratio(value.Height, container.Height));
    }
    private static string SurfaceDisplay(string surfaceClass) => surfaceClass.Replace("Raw", string.Empty, StringComparison.Ordinal);

    private static string SemanticSurfaceClass(string surfaceClass) => surfaceClass switch
    {
        "RawPopupWindow" => "SemanticPopupWindow",
        "RawDialogWindow" => "SemanticDialogWindow",
        "RawToolWindow" => "SemanticToolWindow",
        _ => "SemanticWindow"
    };

    private static string SemanticSurfaceDisplay(RawSurfaceInfo surface, string processName, IEnumerable<RawControlInfo> controls)
    {
        var title = (surface.Title ?? string.Empty).Trim();
        if (surface.SurfaceClass == "RawWindow")
        {
            var application = ApplicationDisplayName(processName);
            if (LooksLikeDocumentTitle(title)) return application.Length > 0 ? $"{application} document window" : "Document window";
            if (IsMeaningfulHostTitle(title, surface.ClassName, processName)) return title;
            return application.Length > 0 ? $"{application} window" : "Application window";
        }

        if (surface.SurfaceClass == "RawDialogWindow" && IsMeaningfulHostTitle(title, surface.ClassName, processName))
            return title;
        if (surface.SurfaceClass == "RawToolWindow" && IsMeaningfulHostTitle(title, surface.ClassName, processName))
            return title;

        var labels = controls.Select(control => StableDisplay(control.Observation))
            .Where(label => !string.IsNullOrWhiteSpace(label) && label is not "Unknown" and not "Control")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3).ToArray();
        if (labels.Length > 0) return string.Join(", ", labels);
        return surface.SurfaceClass switch
        {
            "RawPopupWindow" => "Popup",
            "RawDialogWindow" => "Dialog window",
            "RawToolWindow" => "Tool window",
            _ => "Application window"
        };
    }

    private static bool LooksLikeDocumentTitle(string value)
    {
        var lower = value.ToLowerInvariant();
        return new[] { ".doc", ".docx", ".docm", ".xls", ".xlsx", ".ppt", ".pptx", ".pdf" }
            .Any(lower.Contains);
    }

    private static bool IsMeaningfulHostTitle(string value, string className, string processName) =>
        value.Length > 0 &&
        !string.Equals(value, className, StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(value, processName, StringComparison.OrdinalIgnoreCase) &&
        !new[] { "net ui tool window", "tool window", "popup", "window" }.Contains(value, StringComparer.OrdinalIgnoreCase);

    private static string ApplicationDisplayName(string value)
    {
        var normalized = StableIdentity.Normalize(value);
        if (normalized.Contains("winword", StringComparison.Ordinal)) return "Word";
        if (normalized.Contains("excel", StringComparison.Ordinal)) return "Excel";
        if (normalized.Contains("powerpoint", StringComparison.Ordinal)) return "PowerPoint";
        return value.Trim().Replace(".exe", string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    private static EvidenceRef Evidence(RecordingManifest manifest, FrameObservation frame, RectI bounds)
        => new(manifest.SessionId, frame.Sequence, $"raw/observations/frame-{frame.Sequence:D6}.json", bounds,
            manifest.Privacy.ScreenshotsRetained && !string.IsNullOrEmpty(frame.FrameEntry) ? frame.FrameEntry : null);

    private static MutableNode GetNode(
        IDictionary<string, MutableNode> nodes,
        string id,
        GraphNodeKind kind,
        string parentId,
        string stableKey,
        string label)
    {
        if (nodes.TryGetValue(id, out var existing)) return existing;
        var created = new MutableNode(id, kind, parentId, stableKey, label);
        nodes.Add(id, created);
        return created;
    }

    private static MutableEdge GetEdge(
        IDictionary<string, MutableEdge> edges,
        string id,
        string kind,
        string fromId,
        string toId)
    {
        if (edges.TryGetValue(id, out var existing)) return existing;
        var created = new MutableEdge(id, kind, fromId, toId);
        edges.Add(id, created);
        return created;
    }

    private static void AddContains(
        IDictionary<string, MutableEdge> edges,
        string fromId,
        string toId,
        EvidenceRef? evidence)
    {
        var id = StableIdentity.Create("edge", fromId, toId, "contains");
        var edge = GetEdge(edges, id, "contains", fromId, toId);
        if (evidence is not null) edge.AddEvidence(evidence);
    }

    private sealed class MutableNode(
        string id,
        GraphNodeKind kind,
        string parentId,
        string stableKey,
        string label)
    {
        private readonly Dictionary<string, GraphProperty> _properties = new(StringComparer.Ordinal);
        private readonly Dictionary<string, EvidenceRef> _evidence = new(StringComparer.Ordinal);
        public string ParentId { get; } = parentId;

        public void AddProperty(string name, string? value, bool sensitive = false)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            var bounded = value.Length <= 4_096 ? value : value[..4_096];
            _properties[$"{name}\u001f{bounded}\u001f{sensitive}"] = new(name, bounded, sensitive);
        }

        public void AddEvidence(EvidenceRef evidence) => _evidence[EvidenceKey(evidence)] = evidence;

        public EvidenceRef? EvidenceFor(string bundleId, IReadOnlyList<long> frameSequences) =>
            _evidence.Values.FirstOrDefault(item =>
                item.BundleId == bundleId && frameSequences.Contains(item.FrameSequence));

        public GraphNode Build() => new(id, kind, ParentId, stableKey, label,
            _properties.Values.OrderBy(property => property.Name, StringComparer.Ordinal)
                .ThenBy(property => property.Value, StringComparer.Ordinal).ToArray(),
            _evidence.Values.OrderBy(item => item.BundleId, StringComparer.Ordinal)
                .ThenBy(item => item.FrameSequence)
                .ThenBy(EvidenceKey, StringComparer.Ordinal)
                .ToArray());
    }

    private sealed class MutableEdge(string id, string kind, string fromId, string toId)
    {
        private readonly Dictionary<string, GraphProperty> _properties = new(StringComparer.Ordinal);
        private readonly Dictionary<string, EvidenceRef> _evidence = new(StringComparer.Ordinal);

        public void AddProperty(string name, string? value, bool sensitive = false)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            var bounded = value.Length <= 4_096 ? value : value[..4_096];
            _properties[$"{name}\u001f{bounded}\u001f{sensitive}"] = new(name, bounded, sensitive);
        }

        public void AddEvidence(EvidenceRef evidence) => _evidence[EvidenceKey(evidence)] = evidence;

        public GraphEdge Build() => new(id, kind, fromId, toId,
            _properties.Values.OrderBy(property => property.Name, StringComparer.Ordinal)
                .ThenBy(property => property.Value, StringComparer.Ordinal).ToArray(),
            _evidence.Values.OrderBy(item => item.BundleId, StringComparer.Ordinal)
                .ThenBy(item => item.FrameSequence)
                .ThenBy(EvidenceKey, StringComparer.Ordinal)
                .ToArray());
    }

    private sealed class RawSurfaceInfo(
        string id,
        string stableKey,
        string surfaceClass,
        string role,
        string className,
        string title,
        string fingerprint)
    {
        public string Id { get; } = id;
        public string StableKey { get; } = stableKey;
        public string SurfaceClass { get; } = surfaceClass;
        public string Role { get; } = role;
        public string ClassName { get; } = className;
        public string Title { get; } = title;
        public string Fingerprint { get; } = fingerprint;
        public string? OwnerRawSurfaceId { get; set; }
        public string? InteractionSourceRawControlId { get; set; }
        public List<EvidenceRef> Evidence { get; } = [];
        public HashSet<string> VariantIds { get; } = new(StringComparer.Ordinal);
    }

    private sealed class RawControlInfo(
        string id,
        string rawSurfaceId,
        string? parentRawControlId,
        string path,
        AutomationObservation observation,
        bool wasObservedVisible)
    {
        public string Id { get; } = id;
        public string RawSurfaceId { get; } = rawSurfaceId;
        public string? ParentRawControlId { get; } = parentRawControlId;
        public string Path { get; } = path;
        public AutomationObservation Observation { get; private set; } = observation;
        public bool WasObservedVisible { get; private set; } = wasObservedVisible;
        public bool WasConfirmed { get; set; }
        public List<EvidenceRef> Evidence { get; } = [];
        public SortedSet<string> ExtractionCandidateIds { get; } = new(StringComparer.Ordinal);
        public SortedSet<string> ExtractionSurfaceIds { get; } = new(StringComparer.Ordinal);
        public SortedSet<string> EvidenceSources { get; } = new(StringComparer.Ordinal);
        public SortedSet<string> ExtractionEvidenceIds { get; } = new(StringComparer.Ordinal);
        public SortedSet<string> ExtractionCoverageStatuses { get; } = new(StringComparer.Ordinal);
        public double? MaximumExtractionConfidence { get; private set; }
        public bool HasExtractionConflict { get; private set; }

        public void Observe(AutomationObservation observation, bool visible)
        {
            if (visible || !WasObservedVisible)
                Observation = observation;
            WasObservedVisible |= visible;
        }

        public void ObserveExtraction(MergedControlCandidate? candidate)
        {
            if (candidate is null) return;
            ExtractionCandidateIds.Add(candidate.CandidateId);
            ExtractionSurfaceIds.Add(candidate.SurfaceId);
            foreach (var source in candidate.Sources) EvidenceSources.Add(source.ToString());
            foreach (var evidenceId in candidate.EvidenceIds) ExtractionEvidenceIds.Add(evidenceId);
            ExtractionCoverageStatuses.Add(candidate.CoverageStatus.ToString());
            MaximumExtractionConfidence = Math.Max(MaximumExtractionConfidence ?? 0, candidate.Confidence);
            HasExtractionConflict |= candidate.HasConflict;
        }
    }

    private sealed record FrameControl(string Id, AutomationObservation Observation);
    private sealed record IndexedControl(AutomationObservation Observation, string InstanceKey);
    private sealed record RawDataStreamFrameInfo(
        IReadOnlyDictionary<long, string> SurfaceByWindow,
        IReadOnlyDictionary<string, string> ControlByWindowRuntime);

    private static string EvidenceKey(EvidenceRef item)
        => string.Join('|', item.BundleId, item.FrameSequence, item.ObservationEntry, item.ScreenshotEntry,
            item.Bounds?.X, item.Bounds?.Y, item.Bounds?.Width, item.Bounds?.Height);
}
