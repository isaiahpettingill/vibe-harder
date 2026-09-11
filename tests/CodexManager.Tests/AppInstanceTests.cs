namespace CodexManager.Tests;

public class AppInstanceTests
{
    [Fact]
    public async Task SecondLaunchActivatesOwnerInsteadOfOwningProfile()
    {
        var profile = Path.Combine(Path.GetTempPath(), "codex-instance", Guid.NewGuid().ToString("N"));
        using var ready = new ManualResetEventSlim(); using var finish = new ManualResetEventSlim();
        var activated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var owner = new AppInstance(profile); Assert.True(owner.IsOwner);
                _ = owner.Listen(() => activated.TrySetResult()); ready.Set(); finish.Wait(TimeSpan.FromSeconds(10));
            }
            catch (Exception error) { failure = error; ready.Set(); }
        });
        thread.Start(); Assert.True(ready.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        try
        {
            Assert.Null(failure);
            using var second = new AppInstance(profile); Assert.False(second.IsOwner);
            await second.ActivateExisting(); await activated.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
        finally { finish.Set(); thread.Join(); }
        Assert.Null(failure);
    }
    [Fact]
    public void StartupCommandQuotesSpaces() => Assert.Equal("\"C:\\Program Files\\Vibe Harder\\CodexManager.exe\" --startup", StartupRegistration.WindowsCommand(@"C:\Program Files\Vibe Harder\CodexManager.exe"));
}
