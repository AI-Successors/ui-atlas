using System.Windows.Threading;
using UiAtlas.Core.Contracts;

namespace UiAtlas.Core.Mcp;

/// <summary>One attended request owns one STA and one capsule until input and restoration end.</summary>
internal sealed class GridExplorationPresentation : IAsyncDisposable, IProgress<AcquisitionProgress>
{
    private readonly TaskCompletionSource<bool> _approval = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _stop;
    private readonly FloatingCapsuleWindow _window;
    private readonly CancellationTokenRegistration _registration;
    private int _disposed;
    internal GridExplorationChoice? SelectedChoice { get; private set; }

    private GridExplorationPresentation(FloatingCapsuleWindow window, CancellationTokenSource stop)
    {
        _window = window;
        _stop = stop;
        window.Approve += () =>
        {
            if (stop.IsCancellationRequested) { Cancel(); return; }
            SelectedChoice = window.SelectedChoice;
            window.Hide(); // Remove the approval card before target activation or safety checks.
            _approval.TrySetResult(true);
        };
        window.Cancel += Cancel;
        window.Stop += Cancel;
        window.Closed += (_, _) =>
        {
            if (Volatile.Read(ref _disposed) == 0) Cancel();
            _approval.TrySetResult(false);
            window.Dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
        };
        _registration = stop.Token.Register(() =>
        {
            _approval.TrySetResult(false);
            Post(() => { if (window.IsExploring) window.ShowStopping(); else window.Hide(); });
        });
    }

    internal static Task<GridExplorationPresentation> OpenAsync(string table, string application,
        GridTargetIdentity target, RectI bounds, CancellationTokenSource stop,
        IReadOnlyList<GridExplorationChoice>? choices = null, bool readTable = false)
    {
        stop.Token.ThrowIfCancellationRequested();
        var ready = new TaskCompletionSource<GridExplorationPresentation>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            using var dpi = new FloatingCapsuleWindow.DpiScope();
            GridExplorationPresentation? session = null;
            try
            {
                var window = new FloatingCapsuleWindow(table, application, target.SelectedHwnd, bounds, choices, readTable);
                session = new(window, stop);
                window.ShowApproval();
                ready.TrySetResult(session);
                Dispatcher.Run();
            }
            catch (Exception error)
            {
                var startupFailed = ready.TrySetException(error);
                session?.Cancel();
                if (session is not null)
                {
                    session._window.Close();
                    if (startupFailed) session._registration.Dispose();
                }
            }
            finally
            {
                session?._approval.TrySetResult(false);
                session?._closed.TrySetResult();
            }
        }) { IsBackground = true, Name = "UI Atlas exploration capsule" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return ready.Task;
    }

    internal async Task<bool> ConfirmAsync(CancellationToken cancellation)
    {
        try { return await _approval.Task.WaitAsync(cancellation).ConfigureAwait(false) && !_stop.IsCancellationRequested; }
        catch (OperationCanceledException) { Cancel(); return false; }
    }

    internal async Task StartAsync()
    {
        if (!_approval.Task.IsCompletedSuccessfully || !_approval.Task.Result)
            throw new InvalidOperationException("approval-required");
        _stop.Token.ThrowIfCancellationRequested();
        await _window.Dispatcher.InvokeAsync(() =>
        {
            _stop.Token.ThrowIfCancellationRequested();
            _window.ShowExploration();
        }).Task.ConfigureAwait(false);
    }

    public void Report(AcquisitionProgress value) => Post(() => _window.UpdateProgress(value));

    internal async Task CountdownAsync(CancellationToken cancellation)
    {
        for (var remaining = 5; remaining > 0; remaining--)
        {
            var seconds = remaining;
            await _window.Dispatcher.InvokeAsync(() => _window.ShowStarting(seconds)).Task.ConfigureAwait(false);
            await Task.Delay(1000, cancellation).ConfigureAwait(false);
        }
        await _window.Dispatcher.InvokeAsync(() => _window.UpdateProgress(new("", GridAcquisitionLifecycle.Running,
            GridAcquisitionStage.Probe, 0, 0, 0, 0, "Preparing capture"))).Task.ConfigureAwait(false);
    }

    private void Cancel()
    {
        _approval.TrySetResult(false);
        try { _stop.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private void Post(Action action)
    {
        if (Volatile.Read(ref _disposed) != 0 || _window.Dispatcher.HasShutdownStarted) return;
        _window.Dispatcher.BeginInvoke(action);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _registration.DisposeAsync().ConfigureAwait(false);
        if (!_window.Dispatcher.HasShutdownStarted)
            await _window.Dispatcher.InvokeAsync(_window.Close).Task.ConfigureAwait(false);
        await _closed.Task.ConfigureAwait(false);
    }
}
