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
    public async Task<bool> ActivateExisting(string? directory = null)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await using var pipe = new NamedPipeClientStream(".", name, PipeDirection.Out);
            await pipe.ConnectAsync(timeout.Token);
            if (directory is null) await pipe.WriteAsync(new byte[] { 1 }, timeout.Token);
            else
            {
                var bytes = Encoding.UTF8.GetBytes(directory);
                if (bytes.Length > 65536) return false;
                var header = new byte[5]; header[0] = 2;
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(1), bytes.Length);
                await pipe.WriteAsync(header, timeout.Token); await pipe.WriteAsync(bytes, timeout.Token);
            }
            return true;
        }
        catch (IOException) { return false; }
        catch (OperationCanceledException) { return false; }
    }
    public Task Listen(Action activate) => Listen(_ => activate());
    public async Task Listen(Action<string?> activate)
    {
        while (!cancellation.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(cancellation.Token);
                using var request = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
                request.CancelAfter(TimeSpan.FromSeconds(5));
                var kind = new byte[1]; await pipe.ReadExactlyAsync(kind, request.Token);
                if (kind[0] == 1) activate(null);
                else if (kind[0] == 2)
                {
                    var header = new byte[4]; await pipe.ReadExactlyAsync(header, request.Token);
                    var length = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(header);
                    if (length is <= 0 or > 65536) continue;
                    var bytes = new byte[length]; await pipe.ReadExactlyAsync(bytes, request.Token);
                    activate(Encoding.UTF8.GetString(bytes));
                }
            }
            catch (OperationCanceledException) { if (cancellation.IsCancellationRequested) return; }
            catch (IOException) { try { await Task.Delay(100, cancellation.Token); } catch (OperationCanceledException) { return; } }
        }
    }
    public void Dispose() { if (disposed) return; disposed = true; cancellation.Cancel(); profileLock?.Dispose(); if (IsOwner) mutex?.ReleaseMutex(); mutex?.Dispose(); }
}
