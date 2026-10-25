using System.Runtime.InteropServices;

namespace PolyLab.Launcher.Core;

// One launcher per user session: two would install into the same tools folder at once. A second start wakes the
// first window and exits.
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _wake;
    private readonly RegisteredWaitHandle? _wait;

    public bool IsPrimary { get; }

    public SingleInstance(string name, Action onWake)
    {
        _mutex = new Mutex(true, @"Local\" + name + "_mtx", out var created);
        IsPrimary = created;
        _wake = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\" + name + "_wake");
        if (IsPrimary)
        {
            _wait = ThreadPool.RegisterWaitForSingleObject(_wake, (_, _) => onWake(), null, Timeout.Infinite, false);
            return;
        }
        // The woken window may take the foreground only if the process the user just started allows it.
        AllowSetForegroundWindow(-1);
        _wake.Set();
    }

    public void Dispose()
    {
        _wait?.Unregister(null);
        _wake.Dispose();
        if (IsPrimary) _mutex.ReleaseMutex();
        _mutex.Dispose();
    }

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);
}
