#if !MOBILE_CLIENT
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace CodexManager;

public sealed class WebServer : IAsyncDisposable
{
    private readonly WebApplication app;
    private readonly X509Certificate2 certificate;
    private readonly PhysicalFileProvider files;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim connections = new(32, 32);
    private sealed record DownloadTicket(string Path, string Device, string Secret, DateTimeOffset Expires);
    private readonly ConcurrentDictionary<string, DownloadTicket> downloads = new();
    private readonly SemaphoreSlim downloadConnections = new(4, 4);
    private WebServer(string assets, string directory, string address, int port, Func<JsonObject, Task<JsonNode?>> handle, Func<string, string, CancellationToken, Action, Task>? showPairing)
    {
        certificate = RemoteTrust.Certificate(Path.Combine(directory, "web"));
        files = new PhysicalFileProvider(Path.GetFullPath(assets));
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], ContentRootPath = assets });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Limits.MaxRequestBodySize = 0;
            options.Limits.MaxConcurrentConnections = 64;
            var bind = IPAddress.TryParse(address, out var parsed) ? parsed : IPAddress.Any;
            void Configure(ListenOptions endpoint) { endpoint.Protocols = HttpProtocols.Http1; endpoint.UseHttps(certificate); }
            if (bind.Equals(IPAddress.Any) || bind.Equals(IPAddress.IPv6Any)) options.ListenAnyIP(port, Configure);
            else options.Listen(bind, port, Configure);
        });
        app = builder.Build();
        app.Use(async (context, next) =>
        {
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.Headers["X-Frame-Options"] = "DENY";
            context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self' 'wasm-unsafe-eval'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; font-src 'self' data:; connect-src 'self'; worker-src 'self' blob:; object-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'";
            context.Response.Headers["Cache-Control"] = "no-cache";
            await next(context);
        });
        app.UseWebSockets();
        var protocol = new RemoteSessionProtocol(directory, handle, showPairing);
        async Task<JsonNode?> BrowserRequest(string device, string secret, JsonObject request)
        {
            if (request["method"]?.GetValue<string>() != "file/download") return await handle(request);
            foreach (var item in downloads.Where(p => p.Value.Expires < DateTimeOffset.UtcNow)) downloads.TryRemove(item.Key, out _);
            if (downloads.Count >= 32) throw new IOException("Too many pending downloads. Try again shortly.");
            var result = await handle(request);
            var path = result?["path"]?.GetValue<string>() ?? throw new IOException("File download unavailable.");
            var key = RemoteKey.NewSecret();
            downloads[key] = new(path, device, secret, DateTimeOffset.UtcNow.AddSeconds(30));
            return new JsonObject { ["url"] = "/download/" + key };
        }
        app.MapGet("/download/{key}", async context =>
        {
            context.Response.Headers["Cache-Control"] = "no-store";
            if (!downloads.TryRemove(context.Request.RouteValues["key"]?.ToString() ?? "", out var ticket) || ticket.Expires < DateTimeOffset.UtcNow || !RemoteTrust.Authorized(directory, ticket.Device, ticket.Secret))
            { context.Response.StatusCode = 403; return; }
            if (!await downloadConnections.WaitAsync(0, context.RequestAborted)) { context.Response.StatusCode = 429; return; }
            try
            {
                await using var file = new FileStream(ticket.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
                context.Response.ContentType = "application/octet-stream";
                var disposition = new Microsoft.Net.Http.Headers.ContentDispositionHeaderValue("attachment");
                disposition.SetHttpFileName(Path.GetFileName(ticket.Path)); context.Response.Headers.ContentDisposition = disposition.ToString();
                context.Response.ContentLength = file.Length;
                var buffer = new byte[65536]; int count;
                while ((count = await file.ReadAsync(buffer, context.RequestAborted)) != 0)
                {
                    if (!RemoteTrust.Authorized(directory, ticket.Device, ticket.Secret)) { context.Abort(); return; }
                    await context.Response.Body.WriteAsync(buffer.AsMemory(0, count), context.RequestAborted);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or OperationCanceledException)
            { if (context.Response.HasStarted) context.Abort(); else context.Response.StatusCode = 404; }
            finally { downloadConnections.Release(); }
        });
        app.Map("/remote", async context =>
        {
            // Browser cookies are not authentication, and other websites cannot open our socket.
            var origin = "https://" + context.Request.Host;
            if (!context.WebSockets.IsWebSocketRequest || context.Request.Headers.Origin.ToString() != origin)
            { context.Response.StatusCode = StatusCodes.Status403Forbidden; return; }
            if (!await connections.WaitAsync(0, lifetime.Token)) { context.Response.StatusCode = 503; return; }
            try
            {
                using var socket = await context.WebSockets.AcceptWebSocketAsync();
                using var session = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, context.RequestAborted);
                // HTTPS authenticates the origin. Unlike native clients, JS cannot inspect TLS certificates.
                var binding = "web:" + new Uri(origin).GetLeftPart(UriPartial.Authority);
                await protocol.Serve((token, maximum) => WebSocketWire.Read(socket, token, maximum),
                    (value, token) => WebSocketWire.Write(socket, value, token), socket.Abort, binding, session.Token, BrowserRequest);
            }
            catch (Exception error) when (error is IOException or OperationCanceledException or System.Net.WebSockets.WebSocketException or System.Text.Json.JsonException or InvalidOperationException) { }
            finally { connections.Release(); }
        });
        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
        var contentTypes = new FileExtensionContentTypeProvider();
        contentTypes.Mappings[".dat"] = "application/octet-stream";
        app.UseStaticFiles(new StaticFileOptions { FileProvider = files, ContentTypeProvider = contentTypes, ServeUnknownFileTypes = false });
    }
    public static async Task<WebServer> Start(string assets, string directory, string address, int port, Func<JsonObject, Task<JsonNode?>> handle, Func<string, string, CancellationToken, Action, Task>? showPairing, CancellationToken token)
    {
        var server = new WebServer(assets, directory, address, port, handle, showPairing);
        try { await server.app.StartAsync(token); return server; }
        catch { await server.DisposeAsync(); throw; }
    }
    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await app.StopAsync(timeout.Token); }
        finally { await app.DisposeAsync(); files.Dispose(); certificate.Dispose(); lifetime.Dispose(); }
    }
}
#endif
