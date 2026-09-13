using CodexManager;

using var instance = new AppInstance(args[0]);
Console.WriteLine(instance.IsOwner ? "OWNER" : "SECONDARY");
if (!instance.IsOwner) { instance.ActivateExisting().GetAwaiter().GetResult(); return; }
_ = instance.Listen(() => Console.WriteLine("ACTIVATED"));
Thread.Sleep(TimeSpan.FromSeconds(30));
