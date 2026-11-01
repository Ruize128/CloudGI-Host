using CloudGI.Host.Core.Streaming;
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

    public static async Task<CloudGiControlServer> StartAsync(ISunshineService sunshine, SunshineConfiguration configuration,
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
        var server = new CloudGiControlServer(application, certificate);
        application.Use(async (context, next) =>
        {
            if (!IsLocalNetworkAddress(context.Connection.RemoteIpAddress)) { context.Response.StatusCode = 403; return; }
            await next(context);
        });
        application.MapGet("/cloudgi/info", () => Results.Json(new
        {
            protocolVersion = 1, hostId, name = settings.Name, sunshinePort = settings.Port,
            sunshineHttpsPort = settings.Port - 5, pairingMode = "automatic-lan"
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
