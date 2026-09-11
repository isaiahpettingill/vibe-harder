using System.Threading.Channels;
using Microsoft.Data.Sqlite;

namespace CodexManager;

// One ordered writer owns its connection; UI reads use a different connection.
internal sealed class StoreWriter
{
    private readonly Channel<(Action<SqliteConnection> Write, TaskCompletionSource? Barrier)> queue = Channel.CreateUnbounded<(Action<SqliteConnection>, TaskCompletionSource?)>(new() { SingleReader = true });
    private readonly Task worker;
    private Exception? failure;
    public StoreWriter(string connectionString)
    {
        worker = Task.Run(async () =>
        {
            using var connection = new SqliteConnection(connectionString); connection.Open();
            using (var settings = connection.CreateCommand()) { settings.CommandText = "PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;"; settings.ExecuteNonQuery(); }
            while (await queue.Reader.WaitToReadAsync())
            {
                var barriers = new List<TaskCompletionSource>();
                try
                {
                    using var transaction = connection.BeginTransaction();
                    for (var count = 0; count < 128 && queue.Reader.TryRead(out var item); count++)
                    {
                        if (item.Barrier is not null) barriers.Add(item.Barrier);
                        item.Write(connection);
                    }
                    transaction.Commit();
                }
                catch (Exception error) { failure ??= error; }
                foreach (var barrier in barriers) { if (failure is null) barrier.TrySetResult(); else barrier.TrySetException(failure); }
            }
            if (failure is not null) throw new IOException("Could not persist app data.", failure);
        });
    }
    public void Enqueue(Action<SqliteConnection> write)
    { if (!queue.Writer.TryWrite((write, null))) throw new ObjectDisposedException(nameof(StoreWriter)); }
    public async Task Flush()
    {
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!queue.Writer.TryWrite((static _ => { }, barrier))) { await worker; return; }
        var finished = await Task.WhenAny(barrier.Task, worker); await finished;
    }
    public async Task Close() { queue.Writer.TryComplete(); await worker; }
}
