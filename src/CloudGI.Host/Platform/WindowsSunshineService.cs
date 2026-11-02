using CloudGI.Host.Core.Streaming;
using CloudGI.Host.Core.Games;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace CloudGI.Host.Platform;

public sealed class WindowsSunshineService : ISunshineService
{
    private readonly SunshineConfiguration _configuration;
    private readonly GameSessionService _games;
    private readonly GameStreamLease _streamLease;
    private readonly Task _leaseMonitor;
    private readonly string _executable;
    private readonly bool _useHardwareEncoding;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Mutex _instanceMarker;
    private readonly bool _ownsInstance;
    private readonly CancellationTokenSource _lifetime = new();
    private WindowsSunshineProcess? _child;
    private HttpClient? _client;
    private CloudGiControlServer? _control;
    private bool _disposed;
    public SunshineSnapshot Snapshot { get; private set; } = new(SunshineState.Stopped, "", 48989);

    public WindowsSunshineService(string directory, IGameLaunchService launcher, IGameStatusService status, IGameProcessController processes, string? executable = null, bool childSession = false, bool useHardwareEncoding = false)
    {
        _useHardwareEncoding = useHardwareEncoding;
        _configuration = new(directory, childSession);
        _games = new GameSessionService(launcher, status, _lifetime.Token);
        _streamLease = new GameStreamLease(processes);
        _leaseMonitor = Task.Run(MonitorLeaseAsync);
        _executable = executable ?? Path.Combine(AppContext.BaseDirectory, "Sunshine", "sunshine.exe");
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        _instanceMarker = new Mutex(false, @"Local\CloudGI.Sunshine." + sid, out _ownsInstance);
        if (!_ownsInstance) _instanceMarker.Dispose();
    }

    public async Task StartAsync(CancellationToken token = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try { await StartCoreAsync(linked.Token).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private async Task StartCoreAsync(CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_child is not null && !_child.Process.HasExited && Snapshot.State == SunshineState.Ready) return;
        StopChild();
        Snapshot = Snapshot with { State = SunshineState.Starting, Error = null, ProcessId = null, Encoder = null };
        try
        {
            if (!_ownsInstance) throw new InvalidOperationException("另一个 CloudGI Host 正在管理串流实例，请使用原来的窗口。");
            if (!File.Exists(_executable)) throw new FileNotFoundException("随附的 Sunshine 不完整，请重新编译或发布 Host。");
            var settings = await _configuration.LoadAsync(token).ConfigureAwait(false);
            settings = settings with { Port = SunshineConfiguration.FindAvailablePort(settings.Port) };
            await _configuration.SaveAsync(settings, token).ConfigureAwait(false);
            await _configuration.WriteAsync(settings, token, useHardwareEncoding: _useHardwareEncoding).ConfigureAwait(false);
            Snapshot = new(SunshineState.Starting, settings.Name, settings.Port);
            var password = await GetPasswordAsync(token).ConfigureAwait(false);
            // Credentials are initialized before the listener is started. This command exits without starting a server.
            using (var credentials = new WindowsSunshineProcess(_executable,
                       $"\"{_configuration.FilePath("sunshine.conf")}\" --creds cloudgi {password}"))
            {
                await credentials.Process.WaitForExitAsync(token).WaitAsync(TimeSpan.FromSeconds(15), token).ConfigureAwait(false);
                if (credentials.Process.ExitCode != 0) throw new IOException("无法初始化 Sunshine 本机管理凭据。");
            }
            token.ThrowIfCancellationRequested();
            var child = new WindowsSunshineProcess(_executable, $"\"{_configuration.FilePath("sunshine.conf")}\"");
            _child = child;
            if (_disposed) { StopChild(); token.ThrowIfCancellationRequested(); throw new ObjectDisposedException(nameof(WindowsSunshineService)); }
            Snapshot = Snapshot with { ProcessId = child.Process.Id };
            var deadline = DateTime.UtcNow.AddSeconds(90);
            Exception? lastError = null;
            while (DateTime.UtcNow < deadline)
            {
                token.ThrowIfCancellationRequested();
                if (child.Process.HasExited) throw new IOException($"Sunshine 已退出（{child.Process.ExitCode}），请查看 {_configuration.FilePath("sunshine.log")}。");
                try
                {
                    if (_client is null && File.Exists(_configuration.FilePath("credentials/cacert.pem")))
                        _client = CreateClient(password, settings.Port);
                    if (_client is not null)
                    {
                        using var response = await _client.GetAsync("api/pin", token).ConfigureAwait(false);
                        response.EnsureSuccessStatusCode();
                        // Verify the streaming listener advertises our persisted identity and selected port as well.
                        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
                        var xml = XDocument.Parse(await http.GetStringAsync($"http://127.0.0.1:{settings.Port}/serverinfo", token).ConfigureAwait(false));
                        var root = xml.Root ?? throw new IOException("Sunshine 未返回主机信息。");
                        using var state = JsonDocument.Parse(await File.ReadAllTextAsync(_configuration.FilePath("state.json"), token).ConfigureAwait(false));
                        var expected = state.RootElement.GetProperty("root").GetProperty("uniqueid").GetString();
                        if (root.Element("hostname")?.Value != settings.Name || root.Element("uniqueid")?.Value != expected)
                            throw new IOException("Sunshine 响应的主机身份与专属配置不一致。");
                        // Sunshine can fall back even when an encoder is requested. Never advertise
                        // hardware mode until its actual selected encoder is confirmed.
                        using var log = new FileStream(_configuration.FilePath("sunshine.log"), FileMode.Open,
                            FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        using var reader = new StreamReader(log);
                        var encoder = SunshineEncoding.ParseSelectedEncoder(await reader.ReadToEndAsync(token).ConfigureAwait(false));
                        if (encoder is null) throw new IOException("正在等待 Sunshine 确认实际编码器。");
                        if (!SunshineEncoding.MatchesSelection(encoder, _useHardwareEncoding))
                            throw new InvalidOperationException(_useHardwareEncoding
                                ? "当前环境没有可用的硬件编码器，Sunshine 已选择软件编码；服务已停止。请关闭“使用硬件编码”并重启 Host。"
                                : "Sunshine 未能使用软件编码，服务已停止。请检查编码器日志。");
                        Snapshot = Snapshot with { Encoder = encoder };
                        _control = await CloudGiControlServer.StartAsync(this, _games, _streamLease, _configuration, settings, expected!, token).ConfigureAwait(false);
                        Snapshot = Snapshot with { State = SunshineState.Ready, Error = null };
                        return;
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or TaskCanceledException or System.Xml.XmlException)
                { if (token.IsCancellationRequested) throw; lastError = ex; }
                await Task.Delay(500, token).ConfigureAwait(false);
            }
            throw new IOException("Sunshine 启动超时，请查看专属 sunshine.log。" + lastError?.Message);
        }
        catch (Exception ex)
        {
            StopChild();
            Snapshot = Snapshot with { State = SunshineState.Failed, ProcessId = null, Error = ex.Message };
            if (ex is OperationCanceledException) throw;
        }
    }

    private async Task MonitorLeaseAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(_lifetime.Token).ConfigureAwait(false))
            {
                await _streamLease.CheckTimeoutAsync(_lifetime.Token).ConfigureAwait(false);
                if (_streamLease.LastError is { } error) Snapshot = Snapshot with { Error = error };
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }

    private async Task<string> GetPasswordAsync(CancellationToken token)
    {
        var file = _configuration.FilePath("admin.secret");
        if (File.Exists(file))
            return Encoding.UTF8.GetString(WindowsSunshineProcess.Protect(await File.ReadAllBytesAsync(file, token), true));
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        await File.WriteAllBytesAsync(file, WindowsSunshineProcess.Protect(Encoding.UTF8.GetBytes(password)), token);
        return password;
    }

    private HttpClient CreateClient(string password, int port)
    {
        using var certificate = X509Certificate2.CreateFromPem(File.ReadAllText(_configuration.FilePath("credentials/cacert.pem")));
        var pinned = certificate.RawData;
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            ServerCertificateCustomValidationCallback = (request, cert, _, _) =>
                request.RequestUri?.Host == "127.0.0.1" && cert is not null && cert.RawData.AsSpan().SequenceEqual(pinned)
        };
        var client = new HttpClient(handler) { BaseAddress = new Uri($"https://127.0.0.1:{port + 1}/"), Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("cloudgi:" + password)));
        return client;
    }

    public async Task RenameAsync(string name, CancellationToken token = default)
    {
        name = SunshineConfiguration.ValidateName(name);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            if (!_ownsInstance) throw new InvalidOperationException("请在管理串流实例的 Host 窗口中修改名称。");
            var settings = await _configuration.LoadAsync(linked.Token).ConfigureAwait(false);
            await _configuration.SaveAsync(settings with { Name = name }, linked.Token).ConfigureAwait(false);
            StopChild();
            await StartCoreAsync(linked.Token).ConfigureAwait(false);
            if (Snapshot.State == SunshineState.Failed) throw new IOException(Snapshot.Error);
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<PendingPairing>> GetPendingPairingsAsync(CancellationToken token = default)
    {
        if (!_gate.Wait(0)) return [];
        try
        {
            if (Snapshot.State != SunshineState.Ready) return [];
            if (_child is null || _child.Process.HasExited) throw new IOException("Sunshine 意外退出，请点击重新启动。");
            using var response = await _client!.GetAsync("api/pin", token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token).ConfigureAwait(false));
            return body.RootElement.GetProperty("pairings").EnumerateArray().Select(p =>
                new PendingPairing(p.GetProperty("id").GetString()!, p.GetProperty("name").GetString()!, p.GetProperty("address").GetString()!)).ToArray();
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        { Snapshot = Snapshot with { State = SunshineState.Failed, Error = ex.Message }; return []; }
        finally { _gate.Release(); }
    }

    public async Task PairAsync(string id, string pin, string name, CancellationToken token = default)
    {
        if (pin.Length != 4 || pin.Any(c => c is < '0' or > '9')) throw new ArgumentException("请输入手机显示的四位数字 PIN。");
        if (id.Length != 32 || id.Any(c => !Uri.IsHexDigit(c))) throw new ArgumentException("请选择等待配对的手机。");
        name = string.IsNullOrWhiteSpace(name) ? "Moonlight" : name.Trim();
        if (Encoding.UTF8.GetByteCount(name) > 128) throw new ArgumentException("设备名称过长。");
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_client is null || Snapshot.State != SunshineState.Ready) throw new InvalidOperationException("串流服务尚未就绪。");
            using var csrfResponse = await _client.GetAsync("api/csrf-token", token).ConfigureAwait(false);
            csrfResponse.EnsureSuccessStatusCode();
            using var csrf = JsonDocument.Parse(await csrfResponse.Content.ReadAsStringAsync(token).ConfigureAwait(false));
            using var request = new HttpRequestMessage(HttpMethod.Post, "api/pin")
            { Content = JsonContent.Create(new { pairing_id = id, pin, name }) };
            request.Headers.Add("X-CSRF-Token", csrf.RootElement.GetProperty("csrf_token").GetString());
            using var result = await _client.SendAsync(request, token).ConfigureAwait(false);
            result.EnsureSuccessStatusCode();
            using var body = JsonDocument.Parse(await result.Content.ReadAsStringAsync(token).ConfigureAwait(false));
            if (!body.RootElement.GetProperty("status").GetBoolean()) throw new IOException("配对未完成，请在手机重新发起并核对 PIN。");
        }
        finally { _gate.Release(); }
    }

    private void StopChild()
    {
        Interlocked.Exchange(ref _control, null)?.Dispose();
        Interlocked.Exchange(ref _client, null)?.Dispose();
        Interlocked.Exchange(ref _child, null)?.Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        _streamLease.EndCurrentAsync().GetAwaiter().GetResult();
        StopChild();
        if (_ownsInstance) _instanceMarker.Dispose();
        Snapshot = Snapshot with { State = SunshineState.Stopped, ProcessId = null };
    }
}
