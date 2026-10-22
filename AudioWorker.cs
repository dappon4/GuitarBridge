using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace GuitarBridge;

// All driver lifecycle operations stay on one STA, with its own message pump.
// The UI never waits synchronously for driver startup, shutdown, or audio locks.
public sealed class AudioWorker
{
    private readonly TaskCompletionSource<Dispatcher> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public AudioBridge Bridge { get; } = new();
    public AudioWorker()
    {
        var thread = new Thread(() =>
        {
            ready.SetResult(Dispatcher.CurrentDispatcher);
            Dispatcher.Run();
        }) { IsBackground = true, Name = "Guitar Bridge driver control" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }
    public async Task RunAsync(Action action)
    {
        var dispatcher = await ready.Task.ConfigureAwait(false);
        await dispatcher.InvokeAsync(action).Task.ConfigureAwait(false);
    }
    public Task StartAsync(Settings settings) => RunAsync(() => Bridge.Start(settings));
    public Task StopAsync() => RunAsync(Bridge.Stop);
    public async Task CloseAsync()
    {
        try { await StopAsync().ConfigureAwait(false); }
        finally { (await ready.Task.ConfigureAwait(false)).BeginInvokeShutdown(DispatcherPriority.Normal); }
    }
}
