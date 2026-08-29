namespace VoiceRemoteBridge.App;

internal sealed class SingleInstanceCoordinator : IDisposable
{
    private const string MutexName = "Local\\VoiceRemoteBridge.App.Singleton";
    private const string ShowEventName = "Local\\VoiceRemoteBridge.App.Show";
    private readonly Mutex mutex;
    private readonly EventWaitHandle? showEvent;
    private readonly CancellationTokenSource lifetime = new();
    private Task? listener;
    private bool disposed;

    internal SingleInstanceCoordinator()
    {
        mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        IsPrimary = createdNew;
        if (IsPrimary)
        {
            showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        }
    }

    internal event EventHandler? ShowRequested;

    internal bool IsPrimary { get; }

    internal void StartListening()
    {
        if (!IsPrimary || showEvent is null || listener is not null)
        {
            return;
        }

        listener = Task.Run(() =>
        {
            WaitHandle[] handles = [showEvent, lifetime.Token.WaitHandle];
            while (!lifetime.IsCancellationRequested)
            {
                int index = WaitHandle.WaitAny(handles);
                if (index == 0)
                {
                    ShowRequested?.Invoke(this, EventArgs.Empty);
                }
            }
        });
    }

    internal void SignalPrimary()
    {
        if (IsPrimary)
        {
            return;
        }

        try
        {
            using EventWaitHandle existing = EventWaitHandle.OpenExisting(ShowEventName);
            existing.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        lifetime.Cancel();
        showEvent?.Set();
        if (listener is not null)
        {
            listener.Wait(TimeSpan.FromSeconds(1));
        }

        showEvent?.Dispose();
        lifetime.Dispose();
        mutex.Dispose();
        disposed = true;
    }
}

