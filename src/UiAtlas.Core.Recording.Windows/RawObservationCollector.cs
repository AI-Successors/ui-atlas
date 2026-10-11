using UiAtlas.Core.Contracts;

namespace UiAtlas.Core.Recording.Windows;

/// <summary>Acquisition-local responses, including empty and unsuccessful reads.</summary>
public sealed class RawObservationCollector(Action onDispose) : IDisposable
{
    private readonly List<RawDataStreamObservation> _streams = [];
    public DateTimeOffset StartedUtc { get; } = DateTimeOffset.UtcNow;

    public void Add(string source, DateTimeOffset started, DateTimeOffset completed,
        string status, IReadOnlyList<AutomationObservation> controls)
    {
        lock (_streams)
            _streams.Add(new(source, source, started, completed, status, controls.ToArray(), controls.Count));
    }

    public IReadOnlyList<RawDataStreamObservation> Snapshot()
    {
        lock (_streams) return _streams.ToArray();
    }

    public void Dispose() => onDispose();
}
