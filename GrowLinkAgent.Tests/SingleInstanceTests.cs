using FlowMasterAgent.Services;

namespace GrowLinkAgent.Tests;

public class SingleInstanceTests
{
    [Fact]
    public async Task SecondLaunch_IsRefused_AndAsksTheFirstToShowItself()
    {
        var (mutex, show) = UniqueNames();
        var shown = new TaskCompletionSource();

        using (var first = new Launch(mutex, show, () => shown.TrySetResult()))
        {
            Assert.Equal(SingleInstance.Result.Acquired, (await first.Instance).State);

            using var second = new Launch(mutex, show);
            Assert.Equal(SingleInstance.Result.RunningForThisUser, (await second.Instance).State);
            Assert.Same(shown.Task, await Task.WhenAny(shown.Task, Task.Delay(5000)));
        }

        // Once the first exits, the next launch runs.
        using var third = new Launch(mutex, show);
        Assert.Equal(SingleInstance.Result.Acquired, (await third.Instance).State);
    }

    [Fact]
    public async Task LaunchAfterACrash_TakesOverTheAbandonedMutex()
    {
        var (mutex, show) = UniqueNames();

        // An agent that ends without releasing the mutex, as a crashed or killed process would.
        await Task.Factory.StartNew(() => SingleInstance.Acquire(mutex, show), TaskCreationOptions.LongRunning);

        using var next = new Launch(mutex, show);
        Assert.Equal(SingleInstance.Result.Acquired, (await next.Instance).State);
    }

    private static (string Mutex, string Show) UniqueNames()
    {
        var id = Guid.NewGuid().ToString("N");
        return ($@"Global\GrowLinkAgentTest.{id}", $@"Global\GrowLinkAgentTest.Show.{id}");
    }

    /// <summary>
    /// One "launch" of the agent: acquires on its own thread and keeps that thread alive until
    /// disposed, as a running agent's main thread does. A mutex is owned by a thread, so if that
    /// thread ended while still holding it the mutex would be abandoned, and the next launch
    /// would rightly take it over.
    /// </summary>
    private sealed class Launch : IDisposable
    {
        private readonly Thread _thread;
        private readonly ManualResetEventSlim _exit = new();
        private readonly TaskCompletionSource<SingleInstance> _instance = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Launch(string mutex, string show, Action? onShowRequested = null)
        {
            _thread = new Thread(() =>
            {
                SingleInstance instance;
                try
                {
                    instance = SingleInstance.Acquire(mutex, show);
                    if (onShowRequested is not null) instance.OnShowRequested(onShowRequested);
                }
                catch (Exception ex)
                {
                    _instance.SetException(ex);
                    return;
                }
                using (instance)
                {
                    _instance.SetResult(instance);
                    _exit.Wait();
                }
            }) { IsBackground = true };
            _thread.Start();
        }

        public Task<SingleInstance> Instance => _instance.Task;

        public void Dispose()
        {
            _exit.Set();
            _thread.Join();
            _exit.Dispose();
        }
    }
}
