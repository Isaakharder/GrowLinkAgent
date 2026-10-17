namespace FlowMasterAgent.Services;

/// <summary>
/// One GrowLinkAgent per computer, so a file is never picked up by two agents.
/// The first instance holds a named mutex for its lifetime; a second launch signals the
/// first to show its window and exits. The installer uses the same mutex name (AppMutex)
/// to ask for the agent to be closed before files are replaced.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    // Global\: across all signed-in Windows users and sessions on this computer.
    public const string MutexName = @"Global\GrowLinkAgent.SingleInstance";
    public const string ShowEventName = @"Global\GrowLinkAgent.ShowWindow";

    public enum Result { Acquired, RunningForThisUser, RunningForAnotherUser }

    private readonly Mutex? _mutex;
    private readonly EventWaitHandle? _show;
    private RegisteredWaitHandle? _wait;

    public Result State { get; }

    private SingleInstance(Mutex? mutex, EventWaitHandle? show, Result state)
    {
        _mutex = mutex;
        _show = show;
        State = state;
    }

    public static SingleInstance Acquire(string mutexName = MutexName, string showEventName = ShowEventName)
    {
        Mutex mutex;
        bool createdNew;
        try
        {
            mutex = new Mutex(initiallyOwned: true, mutexName, out createdNew);
        }
        catch (UnauthorizedAccessException)
        {
            // Exists, created by another Windows user whose security we can't open.
            return new SingleInstance(null, null, Result.RunningForAnotherUser);
        }

        if (!createdNew)
        {
            // Someone holds it — unless they exited without releasing (abandoned): then it's ours.
            bool ours;
            try { ours = mutex.WaitOne(0); }
            catch (AbandonedMutexException) { ours = true; }
            if (!ours)
            {
                mutex.Dispose();
                try
                {
                    using var show = EventWaitHandle.OpenExisting(showEventName);
                    show.Set();
                    return new SingleInstance(null, null, Result.RunningForThisUser);
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
                {
                    return new SingleInstance(null, null, Result.RunningForAnotherUser);
                }
            }
        }

        var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, showEventName);
        return new SingleInstance(mutex, showEvent, Result.Acquired);
    }

    /// <summary>Runs <paramref name="onShowRequested"/> whenever another launch asks this one to show itself.</summary>
    public void OnShowRequested(Action onShowRequested)
    {
        if (_show is null) return;
        _wait = ThreadPool.RegisterWaitForSingleObject(_show, (_, _) => onShowRequested(), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    public void Dispose()
    {
        _wait?.Unregister(null);
        _show?.Dispose();
        if (_mutex is not null)
        {
            try { _mutex.ReleaseMutex(); } catch (ApplicationException) { /* not held by this thread */ }
            _mutex.Dispose();
        }
    }
}
