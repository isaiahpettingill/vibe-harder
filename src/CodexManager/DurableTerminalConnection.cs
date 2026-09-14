#if !MOBILE_CLIENT
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Avalonia.Threading;
using SvcSystems.UI.Terminal;

namespace CodexManager;

internal sealed class DurableTerminalConnection(string profile, string id, TerminalControlModel model, Action<string> output, Action completed) : IDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private readonly Channel<string> input = Channel.CreateBounded<string>(new BoundedChannelOptions(1024) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private bool disposed;
    private JsonObject Request(string method) => new() { ["method"] = method, ["id"] = id };
    public async Task Start(Workspace workspace, bool resumeOnly)
    {
        var request = Request("open"); request["workspaceId"] = workspace.Id; request["path"] = workspace.Path; request["distro"] = workspace.Distro; request["resumeOnly"] = resumeOnly;
        await TerminalBroker.Request(profile, request, lifetime.Token, launch: !resumeOnly);
        _ = Poll(); _ = Send();
    }
    public void Input(string text)
    {
        if (!disposed && !input.Writer.TryWrite(text)) output("\r\n[Terminal input is busy; wait before typing more.]\r\n");
    }
    private async Task Send()
    {
        try
        {
            await foreach (var text in input.Reader.ReadAllAsync(lifetime.Token))
            {
                var buffer = new StringBuilder(text);
                while (buffer.Length < 65536 && input.Reader.TryRead(out var next)) buffer.Append(next);
                var request = Request("input"); request["text"] = buffer.ToString();
                try { await TerminalBroker.Request(profile, request, lifetime.Token); }
                catch (Exception error) when (!lifetime.IsCancellationRequested)
                {
                    while (input.Reader.TryRead(out _)) { }
                    await Dispatcher.UIThread.InvokeAsync(() => output("\r\n[Terminal input interrupted: " + error.Message + "]\r\n"));
                }
            }
        }
        catch (OperationCanceledException) { }
    }
    private async Task Poll()
    {
        long offset = 0; (int, int) size = default; bool warned = false;
        while (!lifetime.IsCancellationRequested)
        {
            try
            {
                var next = await Dispatcher.UIThread.InvokeAsync(() => (model.Terminal.Cols, model.Terminal.Rows));
                if (size != next)
                {
                    var resize = Request("resize"); resize["cols"] = next.Cols; resize["rows"] = next.Rows;
                    await TerminalBroker.Request(profile, resize, lifetime.Token); size = next;
                }
                var request = Request("read"); request["offset"] = offset;
                var result = await TerminalBroker.Request(profile, request, lifetime.Token);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (disposed) return;
                    if (result["reset"]?.GetValue<bool>() == true) model.Feed("\u001bc");
                    var text = result["text"]!.GetValue<string>();
                    if (text.Length > 0) output(text);
                });
                offset = result["offset"]!.GetValue<long>(); warned = false;
                if (result["exited"]?.GetValue<bool>() == true) { await Dispatcher.UIThread.InvokeAsync(completed); return; }
                await Task.Delay(100, lifetime.Token);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return; }
            catch (Exception error)
            {
                if (!warned) await Dispatcher.UIThread.InvokeAsync(() => { if (!disposed) output("\r\n[Reconnecting to terminal: " + error.Message + "]\r\n"); });
                warned = true;
                try { await Task.Delay(1000, lifetime.Token); } catch (OperationCanceledException) { return; }
            }
        }
    }
    public async Task Close()
    {
        Dispose();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await TerminalBroker.Request(profile, Request("close"), timeout.Token);
    }
    public void Dispose() { disposed = true; lifetime.Cancel(); input.Writer.TryComplete(); }
}
#endif
