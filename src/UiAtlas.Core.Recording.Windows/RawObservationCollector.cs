using UiAtlas.Core.Contracts;

namespace UiAtlas.Core.Recording.Windows;

/// <summary>Acquisition-local responses, including empty and unsuccessful reads.</summary>
public sealed class RawObservationCollector(Action onDispose) : IDisposable
{
    private readonly List<RawDataStreamObservation> _streams = [];
    private bool _sealed;
    public DateTimeOffset StartedUtc { get; } = DateTimeOffset.UtcNow;

    public void Add(string source, DateTimeOffset started, DateTimeOffset completed,
        string status, IReadOnlyList<AutomationObservation> controls, DateTimeOffset? queued = null)
    {
        lock (_streams)
        {
            if (_sealed) return; // A late provider cannot alter a sealed observation.
            var separator = source.LastIndexOf("/0x", StringComparison.Ordinal);
            long? target = separator >= 0 && long.TryParse(source.AsSpan(separator + 3),
                System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var hwnd) ? hwnd : null;
            _streams.Add(new(source, source, started, completed, status, controls.ToArray(), controls.Count,
                EvidenceKind: RawObservationSemantics.Kind(source), TargetWindowHwnd: target, QueuedUtc: queued ?? started));
        }
    }

    public IReadOnlyList<RawDataStreamObservation> Snapshot()
    {
        lock (_streams) return _streams.ToArray();
    }

    public IReadOnlyList<RawDataStreamObservation> Seal()
    {
        lock (_streams) { _sealed = true; return _streams.ToArray(); }
    }

    public void Dispose() => onDispose();
}
