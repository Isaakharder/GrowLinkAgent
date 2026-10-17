using FlowMasterAgent.Services;

namespace GrowLinkAgent.Tests;

public class SingleInstanceTests
{
    [Fact]
    public async Task SecondLaunch_IsRefused_AndAsksTheFirstToShowItself()
    {
        var id = Guid.NewGuid().ToString("N");
        var mutex = $@"Global\GrowLinkAgentTest.{id}";
        var show = $@"Global\GrowLinkAgentTest.Show.{id}";
        var shown = new TaskCompletionSource();

        // Each "launch" on its own thread, as separate processes would be (a mutex is re-entrant per thread).
        var first = await Task.Factory.StartNew(() => SingleInstance.Acquire(mutex, show), TaskCreationOptions.LongRunning);
        Assert.Equal(SingleInstance.Result.Acquired, first.State);
        first.OnShowRequested(() => shown.TrySetResult());

        var second = await Task.Factory.StartNew(() => SingleInstance.Acquire(mutex, show), TaskCreationOptions.LongRunning);
        Assert.Equal(SingleInstance.Result.RunningForThisUser, second.State);
        Assert.Same(shown.Task, await Task.WhenAny(shown.Task, Task.Delay(5000)));

        // Once the first exits, the next launch runs.
        await Task.Factory.StartNew(() => first.Dispose(), TaskCreationOptions.LongRunning);
        var third = await Task.Factory.StartNew(() => SingleInstance.Acquire(mutex, show), TaskCreationOptions.LongRunning);
        Assert.Equal(SingleInstance.Result.Acquired, third.State);
        third.Dispose();
    }
}
