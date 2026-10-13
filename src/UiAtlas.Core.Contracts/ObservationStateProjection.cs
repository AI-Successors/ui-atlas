namespace UiAtlas.Core.Contracts;

/// <summary>Read mutable control facts only from the requested recording and observation.</summary>
public static class ObservationStateProjection
{
    private static readonly HashSet<string> ObservationProperties = new(StringComparer.Ordinal)
    {
        "name", "enabled", "offscreen", "effectivelyVisible", "selected", "focused", "toggleState",
        "expandCollapseState", "frameworkId", "supportedPattern", "observedOwnerHwnd", "effectiveOwnerHwnd",
        "ownershipReason", "observationState", "derivation"
    };

    public static GraphNode ForObservation(GraphNode node, string? bundleId, long? frameSequence)
    {
        if (frameSequence is null || bundleId is null) return node;
        var fact = node.Observations?.SingleOrDefault(value =>
            value.BundleId == bundleId && value.FrameSequence == frameSequence);
        if (node.Kind != GraphNodeKind.Control) return node;
        var annotated = node.Properties.Any(value => value.Name == "manualAnnotationId");
        if (annotated && fact is null) return node;
        var properties = node.Properties.Where(value => !ObservationProperties.Contains(value.Name) || fact is null && value.Name == "frameworkId").ToList();
        if (fact is null)
        {
            properties.Add(new("observationState", "unavailable in this graph; rebuild from recording"));
            return node with { Properties = properties };
        }
        var control = fact.Control;
        properties.AddRange([
            new("name", annotated ? node.Label : control.Name, true), new("enabled", control.IsEnabled.ToString()),
            new("offscreen", control.IsOffscreen.ToString()), new("effectivelyVisible", fact.EffectivelyVisible.ToString()),
            new("selected", control.IsSelected.ToString()), new("focused", control.HasKeyboardFocus.ToString()),
            new("frameworkId", control.FrameworkId), new("toggleState", control.ToggleState ?? "unknown"),
            new("expandCollapseState", control.ExpandCollapseState ?? "unknown"),
            new("observedOwnerHwnd", $"0x{control.WindowHwnd:X}", true),
            new("effectiveOwnerHwnd", $"0x{fact.EffectiveOwnerHwnd:X}", true),
            new("ownershipReason", fact.OwnershipReason), new("derivation", fact.Derivation),
            new("observationState", $"{bundleId} / frame {frameSequence}")]);
        properties.AddRange((control.SupportedPatterns ?? []).Select(pattern => new GraphProperty("supportedPattern", pattern)));
        return node with { Label = annotated || string.IsNullOrWhiteSpace(control.Name) ? node.Label : control.Name, Properties = properties };
    }

}
