using System.Windows.Threading;

namespace UiAtlas.Core.Cli;

/// <summary>One process-lifetime UI dispatcher for the recorder and all mapper windows.</summary>
internal static class RecorderUiDispatcher
{
    // WPF's Fluent theme refresh visits a process-wide window list. All recorder
    // windows must share ownership, including windows without Fluent resources.
    private static readonly Lazy<Dispatcher> Shared = new(Start, LazyThreadSafetyMode.ExecutionAndPublication);
    internal static Dispatcher Instance => Shared.Value;

    private static Dispatcher Start()
    {
        var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            // Publish after the message loop starts, so callers can immediately dispatch initialization.
            dispatcher.BeginInvoke(() => ready.SetResult(dispatcher));
            Dispatcher.Run();
        }) { IsBackground = true, Name = "UiAtlas recorder UI" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return ready.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
    }
}
