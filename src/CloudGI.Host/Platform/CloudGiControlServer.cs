using CloudGI.Host.Core.Streaming;
using CloudGI.Host.Core.Games;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Security.Cryptography.X509Certificates;

namespace CloudGI.Host.Platform;

/// <summary>Automatic pairing for the explicitly supported trusted-LAN mode. No management credentials leave Host.</summary>
internal sealed class CloudGiControlServer : IDisposable
{
    private readonly WebApplication _application;
    private readonly SemaphoreSlim _pairingGate = new(1, 1);
    private readonly CancellationTokenSource _stopping = new();
    private readonly X509Certificate2 _certificate;

    private CloudGiControlServer(WebApplication application, X509Certificate2 certificate)
    { _application = application; _certificate = certificate; }

    public static async Task<CloudGiControlServer> StartAsync(ISunshineService sunshine, GameSessionService games, GameStreamLease streamLease, SunshineConfiguration configuration,
        SunshineSettings settings, string hostId, CancellationToken token)
    {
        using var pem = X509Certificate2.CreateFromPemFile(configuration.FilePath("credentials/cacert.pem"), configuration.FilePath("credentials/cakey.pem"));
        var certificate = X509CertificateLoader.LoadPkcs12(pem.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.UserKeySet);
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.ClearProviders(); // Never log requests containing automatic pairing secrets.
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Limits.MaxRequestBodySize = 2048;
            options.ListenAnyIP(settings.Port + 2, listener => listener.UseHttps(certificate));
        });
        var application = builder.Build();
        var verifier = new PairedClientRequestVerifier(configuration.FilePath("state.json"));
        var server = new CloudGiControlServer(application, certificate);
        application.Use(async (context, next) =>
        {
            if (!IsLocalNetworkAddress(context.Connection.RemoteIpAddress)) { context.Response.StatusCode = 403; return; }
            await next(context);
        });
        application.MapGet("/cloudgi/info", () => Results.Json(new
        {
            protocolVersion = 1, hostId, name = settings.Name, sunshinePort = settings.Port,
            sunshineHttpsPort = settings.Port - 5, pairingMode = "automatic-lan", gameControl = "signed-request-v1", disconnectPolicy = "close-game-lease-v1"
        }));
        application.MapPost("/cloudgi/pair", async Task<IResult> (AutomaticPairRequest request, HttpContext context) =>
        {
            if (context.Request.Headers["X-CloudGI-Client"] != "1") return Results.BadRequest(new { error = "CloudGI client header required." });
            try { request.Validate(); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, server._stopping.Token);
            linked.CancelAfter(TimeSpan.FromSeconds(30));
            var cancellation = linked.Token;
            if (!await server._pairingGate.WaitAsync(0, cancellation)) return Results.Conflict(new { error = "Another pairing is in progress." });
            try
            {
                var deadline = DateTime.UtcNow.AddSeconds(18);
                while (DateTime.UtcNow < deadline)
                {
                    var pending = (await sunshine.GetPendingPairingsAsync(cancellation))
                        .FirstOrDefault(p => request.Matches(p, context.Connection.RemoteIpAddress!));
                    if (pending is not null)
                    {
                        await sunshine.PairAsync(pending.Id, request.Pin, request.DeviceName, cancellation);
                        return Results.Json(new { paired = true, hostId });
                    }
                    if (sunshine.Snapshot.State != SunshineState.Ready)
                        return Results.Json(new { error = "Streaming service is not ready." }, statusCode: 503);
                    await Task.Delay(150, cancellation);
                }
                return Results.Json(new { error = "Pairing request was not received from this device." }, statusCode: 408);
            }
            catch (OperationCanceledException) { return Results.Json(new { error = "Pairing cancelled or timed out." }, statusCode: 408); }
            catch (Exception ex) when (ex is IOException or HttpRequestException or InvalidOperationException)
            { return Results.Json(new { error = "Pairing did not complete. Retry the connection." }, statusCode: 502); }
            finally { server._pairingGate.Release(); }
        });
        // Signed with the existing Moonlight device key. No new password, PIN UI, or executable parameters.
        async Task<bool> Authorized(HttpContext context) =>
            context.Request.Headers["X-CloudGI-Client"] == "1" &&
            await verifier.VerifyAsync(context.Request.Method, context.Request.Path.Value!,
                context.Request.Headers["X-CloudGI-Device"].ToString(),
                context.Request.Headers["X-CloudGI-Time"].ToString(),
                context.Request.Headers["X-CloudGI-Nonce"].ToString(),
                context.Request.Headers["X-CloudGI-Signature"].ToString(), context.RequestAborted);
        IResult SessionResult(GameSessionSnapshot snapshot) => Results.Json(new
        {
            requestId = snapshot.RequestId, state = snapshot.State.ToString().ToLowerInvariant(),
            processId = snapshot.ProcessId, error = snapshot.Error
        });
        application.MapPost("/cloudgi/game/start/{requestId}", async Task<IResult> (string requestId, HttpContext context) =>
        {
            if (!await Authorized(context)) return Results.Json(new { error = "设备尚未授权，请重新连接配对。" }, statusCode: 403);
            if (context.Request.ContentLength is > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding"))
                return Results.BadRequest(new { error = "启动请求不接受游戏路径或参数。" });
            if (sunshine.Snapshot.State != SunshineState.Ready)
                return Results.Json(new { error = "Host 串流服务尚未就绪，请稍后重试。" }, statusCode: 503);
            if (streamLease.IsClosing) return Results.Conflict(new { error = "游戏正在退出，请稍后重新启动。" });
            try { return SessionResult(games.Start(requestId)); }
            catch (ArgumentException) { return Results.BadRequest(new { error = "启动请求标识无效。" }); }
        });
        application.MapGet("/cloudgi/game/status/{requestId}", async Task<IResult> (string requestId, HttpContext context) =>
        {
            if (!await Authorized(context)) return Results.Json(new { error = "设备尚未授权，请重新连接配对。" }, statusCode: 403);
            var snapshot = await games.GetAsync(requestId, context.RequestAborted);
            return snapshot is null ? Results.NotFound(new { error = "启动请求已失效，请重新点击启动游戏。" }) : SessionResult(snapshot);
        });
        application.MapPost("/cloudgi/game/stream/{requestId}", async Task<IResult> (string requestId, HttpContext context) =>
        {
            if (!await Authorized(context)) return Results.StatusCode(403);
            var game = await games.GetAsync(requestId, context.RequestAborted);
            if (game is not { State: GameSessionState.Running, ProcessId: int pid })
                return Results.Conflict(new { error = "游戏进程尚未就绪，请重新启动。" });
            try { await streamLease.BeginAsync(requestId, pid, context.RequestAborted); return Results.Json(new { ok = true }); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or ArgumentException)
            { return Results.Conflict(new { error = "无法管理游戏进程，请确认 Host 已获管理员授权。" }); }
        });
        application.MapPost("/cloudgi/game/heartbeat/{requestId}", async Task<IResult> (string requestId, HttpContext context) =>
        {
            if (!await Authorized(context)) return Results.StatusCode(403);
            return await streamLease.HeartbeatAsync(requestId, context.RequestAborted)
                ? Results.Json(new { ok = true }) : Results.Conflict(new { error = "串流会话已结束，请重新启动游戏。" });
        });
        application.MapPost("/cloudgi/game/disconnect/{requestId}", async Task<IResult> (string requestId, HttpContext context) =>
        {
            if (!await Authorized(context)) return Results.StatusCode(403);
            await streamLease.EndAsync(requestId, context.RequestAborted);
            return streamLease.LastError is null ? Results.Json(new { ok = true })
                : Results.Json(new { error = streamLease.LastError }, statusCode: 500);
        });
        try
        {
            await application.StartAsync(token).ConfigureAwait(false);
            // Schannel needs a named user key on Windows. Validate a real TLS handshake before reporting ready.
            using var probe = new HttpClient(new HttpClientHandler
            {
                UseProxy = false,
                ServerCertificateCustomValidationCallback = (_, remoteCertificate, _, _) =>
                    remoteCertificate is not null && remoteCertificate.RawData.AsSpan().SequenceEqual(certificate.RawData)
            }) { Timeout = TimeSpan.FromSeconds(5) };
            var info = await probe.GetFromJsonAsync<Dictionary<string, JsonElement>>(
                $"https://127.0.0.1:{settings.Port + 2}/cloudgi/info", token).ConfigureAwait(false);
            if (info is null || info["hostId"].GetString() != hostId) throw new IOException("CloudGI HTTPS self-check failed.");
            return server;
        }
        catch { server.Dispose(); throw; }
    }

    private static bool IsLocalNetworkAddress(IPAddress? address)
    {
        if (address is null) return false;
        if (IPAddress.IsLoopback(address)) return true;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 && (bytes[0] == 10 || bytes[0] == 192 && bytes[1] == 168 ||
            bytes[0] == 172 && bytes[1] is >= 16 and <= 31 || bytes[0] == 169 && bytes[1] == 254);
    }

    public void Dispose()
    {
        _stopping.Cancel();
        _application.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _certificate.Dispose();
    }
}
