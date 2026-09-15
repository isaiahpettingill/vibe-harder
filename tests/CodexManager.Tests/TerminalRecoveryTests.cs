using System.Reflection;
using System.Text.Json.Nodes;
using Avalonia.Headless.XUnit;
using Porta.Pty;
using SvcSystems.UI.Terminal;

namespace CodexManager.Tests;

public class TerminalRecoveryTests
{
    [AvaloniaFact]
    public async Task BrokenPtyReadReportsCompletionExactlyOnce()
    {
        using var session = new TerminalSession();
        var connection = DispatchProxy.Create<IPtyConnection, BrokenPty>();
        typeof(TerminalSession).GetField("connection", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(session, connection);
        var completions = 0; session.Completed += () => completions++;
        var pump = typeof(TerminalSession).GetMethod("Pump", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)pump.Invoke(session, null)!;
        Assert.Equal(1, completions);
        session.Input("must not be sent");
        await (Task)pump.Invoke(session, null)!;
        Assert.Equal(1, completions);
    }

    public class BrokenPty : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
        {
            "get_ReaderStream" => throw new IOException("WSL connection was disconnected"),
            "get_WriterStream" => throw new InvalidOperationException("Input reached a dead terminal"),
            _ => null
        };
    }

    [AvaloniaFact]
    public async Task LostBrokerSessionCompletesInsteadOfRetryingForever()
    {
        var profile = Directory.CreateTempSubdirectory("terminal-recovery-").FullName;
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var server = TerminalBroker.Serve(profile, lifetime.Token);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var type = typeof(TerminalBroker).Assembly.GetType("CodexManager.DurableTerminalConnection")!;
        using var connection = (IDisposable)Activator.CreateInstance(type, profile, "missing-shell", new TerminalControlModel(new TerminalOptions()), (Action<string>)(_ => { }), (Action)(() => completed.TrySetResult()))!;
        try
        {
            var poll = (Task)type.GetMethod("Poll", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(connection, null)!;
            await completed.Task.WaitAsync(lifetime.Token);
            await poll.WaitAsync(lifetime.Token);
        }
        finally { lifetime.Cancel(); try { await server; } catch (OperationCanceledException) { } }
    }

    [AvaloniaFact]
    public async Task RemoteTerminalRetriesTransientHostErrors()
    {
        var failing = true;
        using var view = new RemoteTerminalView(request => Task.FromResult<JsonNode?>(request["method"]!.GetValue<string>() switch
        {
            "terminal/open" => new JsonObject { ["id"] = "shell" },
            "terminal/read" when failing => new JsonObject { ["error"] = "The pipe has been ended." },
            "terminal/read" => new JsonObject { ["text"] = "", ["offset"] = 0L },
            _ => JsonValue.Create(true)
        }));
        await view.Open("w", "WSL");
        Assert.False(view.InputReady); Assert.Equal("shell", view.TerminalId);
        failing = false;
        await (Task)typeof(RemoteTerminalView).GetMethod("Poll", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, null)!;
        Assert.True(view.InputReady);
    }

    [AvaloniaFact]
    public async Task RemoteTerminalCanReopenAfterLosingItsSession()
    {
        var missing = false;
        using var view = new RemoteTerminalView(request => Task.FromResult<JsonNode?>(request["method"]!.GetValue<string>() switch
        {
            "terminal/open" => new JsonObject { ["id"] = "shell" },
            "terminal/read" when missing => new JsonObject { ["error"] = "This terminal has closed." },
            "terminal/read" => new JsonObject { ["text"] = "", ["offset"] = 0L },
            _ => JsonValue.Create(true)
        }));
        await view.Open("w", "WSL"); Assert.True(view.InputReady);
        missing = true;
        await (Task)typeof(RemoteTerminalView).GetMethod("Poll", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, null)!;
        Assert.False(view.InputReady); Assert.Null(view.TerminalId);
        missing = false;
        await view.Open("w", "WSL"); Assert.True(view.InputReady);
        Assert.True(await view.SendKeystroke(new("hello")));
    }
}
