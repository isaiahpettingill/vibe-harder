using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace CodexManager;

public sealed class AppInstance : IDisposable
{
    private readonly Mutex? mutex;
    private readonly FileStream? profileLock;
    private bool disposed;
    private readonly string name;
    private readonly CancellationTokenSource cancellation = new();
    public bool IsOwner { get; }
    public AppInstance(string profile)
    {
        profile = Path.TrimEndingDirectorySeparator(Path.GetFullPath(profile));
        name = "CodexManager-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(OperatingSystem.IsWindows() ? profile.ToUpperInvariant() : profile)))[..24];
        if (!OperatingSystem.IsWindows())
        {
            // Native AOT named mutexes on Unix do not exclude other processes.
            // FileShare.None holds an OS lock, released even after a crash. Never unlink
            // this file: replacing its inode would allow two simultaneous owners.
            Directory.CreateDirectory(profile);
            try
            {
                profileLock = new FileStream(Path.Combine(profile, ".instance.lock"), new FileStreamOptions
                {
                    Mode = FileMode.OpenOrCreate,
                    Access = FileAccess.ReadWrite,
                    Share = FileShare.None,
                    UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
                });
                IsOwner = true;
            }
            catch (IOException) { IsOwner = false; }
            return;
        }
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
            catch (IOException) { try { await Task.Delay(100, cancellation.Token); } catch (OperationCanceledException) { return; } }
        }
    }
    public void Dispose() { if (disposed) return; disposed = true; cancellation.Cancel(); profileLock?.Dispose(); if (IsOwner) mutex?.ReleaseMutex(); mutex?.Dispose(); }
}
