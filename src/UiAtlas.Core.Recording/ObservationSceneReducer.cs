using UiAtlas.Core.Contracts;

namespace UiAtlas.Core.Recording;

/// <summary>Deterministic session-local ownership and pixel provenance shared by build and reader.</summary>
public sealed class ObservationSceneReducer
{
    private readonly Dictionary<(long Hwnd, string RuntimeId), (long Owner, RectI Bounds)> _previousOwners = [];
    private readonly Dictionary<(long Hwnd, string RuntimeId), SceneControl> _knowledge = [];
    private long _lastSequence;

    public ObservationScene Reduce(FrameObservation frame)
    {
        if (frame.Sequence <= _lastSequence) throw new InvalidOperationException("Scene observations must be ordered within one recording.");
        _lastSequence = frame.Sequence;
        var windows = frame.ScopedWindows ?? [frame.Window];
        var observed = (frame.ObservedWindowHwnds ?? windows.Select(window => window.Hwnd).ToArray()).ToHashSet();
        var complete = frame.ObservationScope == "full-root" && !frame.AutomationTimedOut && frame.AutomationStatus == "ok";
        var incoming = frame.Automation.Select(control => (control.WindowHwnd, control.RuntimeId)).ToHashSet();
        foreach (var key in _previousOwners.Keys.ToArray())
            if (!windows.Any(window => window.Hwnd == _previousOwners[key].Owner) ||
                complete && observed.Contains(_previousOwners[key].Owner) && !incoming.Contains(key))
                _previousOwners.Remove(key);
        var owners = ReduceOwners(frame, windows, frame.Automation);
        var ownerByControl = new Dictionary<AutomationObservation, long>(ReferenceEqualityComparer.Instance);
        foreach (var pair in owners)
            foreach (var control in pair.Value) ownerByControl[control] = pair.Key;
        foreach (var key in _knowledge.Keys.ToArray())
        {
            var prior = _knowledge[key];
            if (!windows.Any(window => window.Hwnd == prior.EffectiveOwnerHwnd && window.Bounds == prior.WindowBounds) ||
                complete && observed.Contains(prior.EffectiveOwnerHwnd)) _knowledge.Remove(key);
        }
        var current = new List<SceneControl>();
        foreach (var control in frame.Automation)
        {
            var owner = ownerByControl.GetValueOrDefault(control);
            var window = windows.FirstOrDefault(value => value.Hwnd == owner);
            var value = new SceneControl(control, frame.Sequence, frame.TimestampUtc, owner,
                owner == 0 ? "unresolved" : owner == control.WindowHwnd ? "reported" : control.WindowHwnd == 0 ? "capture-scope" : "surface-subtree",
                window?.Bounds, false);
            current.Add(value);
            if (!string.IsNullOrWhiteSpace(control.RuntimeId)) _knowledge[(control.WindowHwnd, control.RuntimeId)] = value;
        }
        var retained = _knowledge.Values.Where(value => value.SourceFrameSequence != frame.Sequence)
            .Select(value => value with { Retained = true }).OrderBy(value => value.SourceFrameSequence)
            .ThenBy(value => value.Control.RuntimeId, StringComparer.Ordinal).ToArray();
        return new(frame.Sequence, complete ? "complete-observation" : frame.ObservationScope == "full-root" ? "partial-observation" : frame.ObservationScope,
            current, retained, owners);
    }

    public static FrameObservation? ResolveScreenshot(FrameObservation observation,
        Func<long, FrameObservation?> findFrame)
    {
        var source = observation;
        var visited = new HashSet<long>();
        for (var depth = 0; depth < 64; depth++)
        {
            if (!visited.Add(source.Sequence)) return null;
            if (!string.IsNullOrEmpty(source.FrameEntry))
                return IsCompatibleScreenshot(observation, source) ? source : null;
            if (source.BaseFrameSequence is not { } sequence || sequence <= 0 || sequence >= source.Sequence)
                return null;
            source = findFrame(sequence);
            if (source is null || source.Sequence != sequence) return null;
        }
        return null;
    }

    public static bool IsCompatibleScreenshot(FrameObservation observation, FrameObservation pixels)
    {
        if (observation.Sequence == pixels.Sequence) return true;
        var windows = pixels.ScopedWindows ?? [pixels.Window];
        var current = observation.ScopedWindows ?? [observation.Window];
        var observed = observation.ObservedWindowHwnds ?? [observation.Window.Hwnd];
        return observed.All(hwnd => current.Any(window => window.Hwnd == hwnd &&
            windows.Any(prior => prior.Hwnd == hwnd && prior.ProcessId == window.ProcessId &&
                prior.ClassName == window.ClassName && prior.Bounds == window.Bounds)));
    }

    public IReadOnlyDictionary<long, IReadOnlyList<AutomationObservation>> ReduceOwners(
        FrameObservation frame,
        IReadOnlyList<WindowObservation> windows,
        IReadOnlyList<AutomationObservation> controls)
    {
        var windowIds = windows.Select(window => window.Hwnd).ToHashSet();
        var targetByIndex = controls.Select(control =>
                control.WindowHwnd != 0 && windowIds.Contains(control.WindowHwnd)
                    ? control.WindowHwnd
                    : control.WindowHwnd == 0 && frame.ObservedWindowHwnds is { Count: 1 } observed && windowIds.Contains(observed[0])
                        ? observed[0] : control.WindowHwnd == 0 && windows.Count == 1 ? windows[0].Hwnd : 0)
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
                .Where(item => item.control.WindowHwnd == window.Hwnd ||
                    !_previousOwners.TryGetValue((item.control.WindowHwnd, item.control.RuntimeId), out var prior) ||
                    prior.Owner == window.Hwnd || prior.Bounds != item.control.Bounds)
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

        foreach (var (control, index) in controls.Select((control, index) => (control, index)))
            if (!string.IsNullOrWhiteSpace(control.RuntimeId))
                _previousOwners[(control.WindowHwnd, control.RuntimeId)] = (targetByIndex[index], control.Bounds);

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

    public static bool SurfaceAnchorMatches(AutomationObservation control, RectI surface)
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

}

public sealed record SceneControl(AutomationObservation Control, long SourceFrameSequence, DateTimeOffset SourceTimestampUtc,
    long EffectiveOwnerHwnd, string OwnershipReason, RectI? WindowBounds, bool Retained);

public sealed record ObservationScene(long FrameSequence, string Coverage, IReadOnlyList<SceneControl> ObservedControls,
    IReadOnlyList<SceneControl> RetainedControls, IReadOnlyDictionary<long, IReadOnlyList<AutomationObservation>> ControlsByWindow);
