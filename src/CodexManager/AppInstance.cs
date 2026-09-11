using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace CodexManager;

public sealed class AppInstance : IDisposable
{
    private readonly Mutex mutex;
    private readonly string name;
    private readonly CancellationTokenSource cancellation = new();
    public bool IsOwner { get; }
    public AppInstance(string profile)
    {
        name = "CodexManager-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(profile).ToUpperInvariant())))[..24];
        mutex = new Mutex(false, name);
        try { IsOwner = mutex.WaitOne(0); } catch (AbandonedMutexException) { IsOwner = true; }
    }
    public async Task ActivateExisting()
    {
        try { await using var pipe = new NamedPipeClientStream(".", name, PipeDirection.Out); await pipe.ConnectAsync(3000); await pipe.WriteAsync(new byte[] { 1 }); }
        catch (IOException) { }
        catch (TimeoutException) { }
    }
    public async Task Listen(Action activate)
    {
        while (!cancellation.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(cancellation.Token);
                if (await pipe.ReadAsync(new byte[1], cancellation.Token) > 0) activate();
            }
            catch (OperationCanceledException) { return; }
            catch (IOException) { }
        }
    }
    public void Dispose() { cancellation.Cancel(); if (IsOwner) mutex.ReleaseMutex(); mutex.Dispose(); }
}
