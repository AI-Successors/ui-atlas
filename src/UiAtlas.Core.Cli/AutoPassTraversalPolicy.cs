using UiAtlas.Core.Contracts;
using UiAtlas.Core.Recording.Windows;

namespace UiAtlas.Core.Cli;

internal static class AutoPassTraversalPolicy
{
    public static IReadOnlyList<AutoRibbonCommandCandidate> OrderCommandsInVisualSequence(
        IEnumerable<AutoRibbonCommandCandidate> candidates) =>
        OrderRibbonTargetsInVisualSequence(
            candidates,
            candidate => candidate.Observation,
            candidate => candidate.DisplayName,
            candidate => candidate.StableKey);

    public static IReadOnlyList<AutoRibbonDialogLauncherCandidate> OrderDialogLaunchersInVisualSequence(
        IEnumerable<AutoRibbonDialogLauncherCandidate> candidates) =>
        OrderRibbonTargetsInVisualSequence(
            candidates,
            candidate => candidate.Observation,
            candidate => candidate.DisplayName,
            candidate => candidate.StableKey);

    public static IReadOnlyList<AutoTabCandidate> OrderTabsInVisualSequence(
        IEnumerable<AutoTabCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        return candidates
            .OrderBy(candidate => candidate.IsBackstage ? 1 : 0)
            .ThenBy(candidate => candidate.Observation.Bounds.X)
            .ThenBy(candidate => candidate.Observation.Bounds.Y)
            .ThenBy(candidate => candidate.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.StableKey, StringComparer.Ordinal)
            .ToArray();
    }

    public static HashSet<string> PrepareVisitedTabsForExplicitSweep(
        IEnumerable<string> recordedTabKeys,
        string? initiallyActiveTabKey)
    {
        ArgumentNullException.ThrowIfNull(recordedTabKeys);
        var visited = recordedTabKeys.ToHashSet(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(initiallyActiveTabKey))
            visited.Remove(initiallyActiveTabKey);
        return visited;
    }

    public static bool ShouldSweepCommandsForActiveTab(
        string? activeTabKey,
        IReadOnlySet<string> visitedTabKeys,
        IReadOnlySet<string> commandSweptTabKeys)
    {
        ArgumentNullException.ThrowIfNull(visitedTabKeys);
        ArgumentNullException.ThrowIfNull(commandSweptTabKeys);
        return !string.IsNullOrWhiteSpace(activeTabKey) &&
               visitedTabKeys.Contains(activeTabKey) &&
               !commandSweptTabKeys.Contains(activeTabKey);
    }

    public static AutoTabCandidate? ResolveActiveTab(
        IReadOnlyList<AutoTabCandidate> discoveredTabs,
        string? currentVisibleLayerKey,
        string? explicitlyActivatedTabKey)
    {
        ArgumentNullException.ThrowIfNull(discoveredTabs);
        if (discoveredTabs.Count == 0) return null;

        // Excel can keep IsSelected=true on the previously active tab for one or
        // more UIA reads after a physical tab click. The tab that this pass just
        // activated (or the overlay layer derived from its materialized frame) is
        // stronger evidence than that stale provider bit.
        return discoveredTabs.FirstOrDefault(candidate =>
                   string.Equals(candidate.StableKey, explicitlyActivatedTabKey, StringComparison.Ordinal))
               ?? discoveredTabs.FirstOrDefault(candidate =>
                   string.Equals(candidate.StableKey,
                       OutlookNavigationDiscovery.ExtractTabLayerKey(currentVisibleLayerKey), StringComparison.Ordinal))
               ?? discoveredTabs.FirstOrDefault(candidate => candidate.IsSelected);
    }

    private static IReadOnlyList<T> OrderRibbonTargetsInVisualSequence<T>(
        IEnumerable<T> candidates,
        Func<T, AutomationObservation> observation,
        Func<T, string> displayName,
        Func<T, string> stableKey)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        return candidates
            .OrderBy(candidate => observation(candidate).Bounds.X)
            .ThenBy(candidate => observation(candidate).Bounds.Y)
            .ThenBy(displayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(stableKey, StringComparer.Ordinal)
            .ToArray();
    }
}
