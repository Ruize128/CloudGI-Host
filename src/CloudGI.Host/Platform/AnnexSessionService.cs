using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using CloudGI.Host.Core.Games;
using CloudGI.Host.Core.Streaming;
using Microsoft.Win32.SafeHandles;

namespace CloudGI.Host.Platform;

/// <summary>Main-desktop facade; Sunshine, launch/status and stream leases all live in the child agent.</summary>
public sealed class AnnexSessionService : ISunshineService, IGameLaunchService, IGameStatusService
{
    private readonly string _directory;
    private readonly bool _useHardwareEncoding;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private NamedPipeServerStream? _pipe;
    private Process? _viewer, _agent;
    private bool _disposed;
    private readonly Mutex _instance;
    private readonly bool _ownsInstance;
    private SunshineSnapshot _snapshot = new(SunshineState.Stopped, "", 48989);
    public ChildDisplaySettings Display { get; private set; } = new();
    public SunshineSnapshot Snapshot
    {
        get
        {
            if (!_disposed && _snapshot.State == SunshineState.Ready &&
                (_viewer is null || _viewer.HasExited || _agent is null || _agent.HasExited))
                _snapshot = _snapshot with { State = SunshineState.Failed, Error = "后台子会话已退出，请重新启动串流服务。" };
            return _snapshot;
        }
    }
    public string SessionDetail => _agent is { HasExited: false } ? $"Annex 子会话 {_agent.SessionId}" : "Annex 子会话实验";

    public AnnexSessionService(string directory, bool useHardwareEncoding = false)
    {
        if (WindowsHostSession.IsManagedChildSession)
            throw new InvalidOperationException("后台子会话中的 Host 不能再创建子会话。");
        _directory = directory;
        _useHardwareEncoding = useHardwareEncoding;
        _instance = new Mutex(false, @"Local\CloudGI.Annex.Parent." + System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value, out _ownsInstance);
        var path = Path.Combine(directory, "display.json");
        if (File.Exists(path)) Display = (JsonSerializer.Deserialize<ChildDisplaySettings>(File.ReadAllText(path))
            ?? throw new InvalidDataException("子会话显示设置为空。")).Validate();
    }

    public async Task StartAsync(CancellationToken token = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_ownsInstance) throw new InvalidOperationException("实验版 Host 已在运行，请使用原来的窗口。");
            if (Snapshot.State == SunshineState.Ready) return;
            _snapshot = _snapshot with { State = SunshineState.Starting, Error = null };
            // A Sunshine capture/encoder failure does not require another Windows logon.
            if (_pipe is not { IsConnected: true } || _viewer is not { HasExited: false } || _agent is not { HasExited: false })
            {
                await StopTransportAsync().ConfigureAwait(false);
                _snapshot = _snapshot with { State = SunshineState.Starting, Error = null };
                await ConnectAsync(linked.Token).ConfigureAwait(false);
            }
            await ExchangeAsync(new("start", _useHardwareEncoding ? "hardware" : "software"), linked.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (_disposed || linked.IsCancellationRequested || _pipe is not { IsConnected: true } ||
                _viewer is not { HasExited: false } || _agent is not { HasExited: false })
                await StopTransportAsync().ConfigureAwait(false);
            _snapshot = _snapshot with { State = SunshineState.Failed, ProcessId = null, Error = ex.Message };
            if (ex is OperationCanceledException) throw;
        }
        finally { _gate.Release(); }
    }

    private async Task ConnectAsync(CancellationToken token)
    {
        Directory.CreateDirectory(_directory);
        var errorPath = Path.Combine(_directory, "viewer-error.txt");
        File.Delete(errorPath);
        File.Delete(Path.Combine(_directory, "agent-error.txt"));
        var name = "CloudGI.Annex." + Guid.NewGuid().ToString("N");
        _pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var executable = Path.Combine(AppContext.BaseDirectory, "Session", "CloudGI.Host.Session.exe");
        if (!File.Exists(executable)) throw new FileNotFoundException("Annex 子会话组件缺失，请重新编译 Host。", executable);
        using var current = Process.GetCurrentProcess();
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(executable)! };
        foreach (var argument in new[] { current.Id.ToString(), current.StartTime.ToUniversalTime().Ticks.ToString(),
            Environment.ProcessPath!, name, _directory, Display.Width.ToString(), Display.Height.ToString(), Display.Scale.ToString() })
            start.ArgumentList.Add(argument);
        _viewer = Process.Start(start) ?? throw new IOException("无法启动 Annex 子会话窗口。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        var connected = _pipe.WaitForConnectionAsync(timeout.Token);
        while (!connected.IsCompleted)
        {
            if (_viewer.HasExited) throw new IOException("Annex 子会话窗口已退出。");
            if (File.Exists(Path.Combine(_directory, "agent-error.txt")))
                throw new IOException(await File.ReadAllTextAsync(Path.Combine(_directory, "agent-error.txt"), token).ConfigureAwait(false));
            if (File.Exists(errorPath)) throw new IOException(await File.ReadAllTextAsync(errorPath, token).ConfigureAwait(false));
            await Task.WhenAny(connected, Task.Delay(250, token)).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
        }
        await connected.ConfigureAwait(false);
        if (!GetNamedPipeClientProcessId(_pipe.SafePipeHandle, out var pid)) throw new IOException("无法确认子会话代理身份。");
        _agent = Process.GetProcessById(checked((int)pid));
        if (!WTSGetChildSessionId(out var child) || child is 0 or uint.MaxValue || child == current.SessionId ||
            _agent.SessionId != child || !string.Equals(_agent.MainModule?.FileName, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
            throw new IOException("子会话代理的进程路径或 Windows 会话不匹配，已拒绝连接。");
    }

    private async Task<ChildSessionReply> ExchangeAsync(ChildSessionRequest request, CancellationToken token)
    {
        var pipe = _pipe ?? throw new IOException("后台子会话尚未连接。");
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(request.Command == "start" || request.Command == "rename" ? 110 : 60));
            await ChildSessionProtocol.WriteAsync(pipe, request, deadline.Token).ConfigureAwait(false);
            var reply = await ChildSessionProtocol.ReadAsync<ChildSessionReply>(pipe, deadline.Token).ConfigureAwait(false);
            _snapshot = reply.Sunshine;
            if (!reply.Success) throw new GameLaunchException(reply.Error ?? "后台子会话请求失败。");
            return reply;
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // A canceled/timed-out exchange must never leave a late reply for the next request.
            pipe.Dispose();
            _snapshot = _snapshot with { State = SunshineState.Failed, Error = "后台子会话通信已中断，请重新启动。" };
            throw;
        }
    }

    private async Task<ChildSessionReply> RequestAsync(ChildSessionRequest request, CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try { ObjectDisposedException.ThrowIf(_disposed, this); return await ExchangeAsync(request, linked.Token).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    public async Task<GameLaunchResult> LaunchAsync(CancellationToken cancellationToken = default) =>
        (await RequestAsync(new("launch"), cancellationToken).ConfigureAwait(false)).Launch
        ?? throw new IOException("后台未返回游戏启动结果。");

    public async Task<GameRuntimeSnapshot> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        if (Snapshot.State != SunshineState.Ready)
            return new(GameRuntimeState.Unknown, Error: Snapshot.Error ?? "正在等待后台子会话就绪。");
        try { return (await RequestAsync(new("status"), cancellationToken).ConfigureAwait(false)).Game ?? new(GameRuntimeState.Unknown); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return new(GameRuntimeState.Unknown, Error: ex.Message); }
    }

    public async Task RenameAsync(string name, CancellationToken token = default) =>
        await RequestAsync(new("rename", SunshineConfiguration.ValidateName(name)), token).ConfigureAwait(false);
    public async Task<IReadOnlyList<PendingPairing>> GetPendingPairingsAsync(CancellationToken token = default)
    {
        if (Snapshot.State != SunshineState.Ready) return [];
        return (await RequestAsync(new("pending"), token).ConfigureAwait(false)).Pending ?? [];
    }
    public async Task PairAsync(string id, string pin, string name, CancellationToken token = default) =>
        await RequestAsync(new("pair", id, pin, name), token).ConfigureAwait(false);

    public async Task ConfigureDisplayAsync(ChildDisplaySettings display, CancellationToken token = default)
    {
        display.Validate();
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (Snapshot.State == SunshineState.Ready &&
                (await ExchangeAsync(new("status"), token).ConfigureAwait(false)).Game?.State != GameRuntimeState.NotRunning)
                throw new InvalidOperationException("请先断开串流并关闭游戏，再修改后台分辨率。");
            Directory.CreateDirectory(_directory);
            var path = Path.Combine(_directory, "display.json");
            var temporary = path + ".tmp";
            try { await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(display), token).ConfigureAwait(false); File.Move(temporary, path, true); }
            finally { File.Delete(temporary); }
            Display = display;
            await StopTransportAsync().ConfigureAwait(false);
            _snapshot = _snapshot with { State = SunshineState.Stopped, Error = null, ProcessId = null };
        }
        finally { _gate.Release(); }
        await StartAsync(token).ConfigureAwait(false);
    }

    private async Task StopTransportAsync()
    {
        uint endingSession;
        if (!WTSGetChildSessionId(out endingSession)) endingSession = uint.MaxValue;
        if (_pipe is { IsConnected: true })
        {
            try { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)); await ExchangeAsync(new("shutdown"), timeout.Token).ConfigureAwait(false); }
            catch (Exception) { /* Broken peers are still closed; the agent also monitors the parent. */ }
        }
        Interlocked.Exchange(ref _pipe, null)?.Dispose();
        if (Interlocked.Exchange(ref _viewer, null) is { } viewer)
        {
            using (viewer)
            {
                if (!viewer.HasExited)
                {
                    viewer.CloseMainWindow();
                    try { await viewer.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); }
                    catch (TimeoutException) { throw new IOException("后台桌面尚未关闭，请先关闭 Annex 窗口后重试。"); }
                }
            }
        }
        Interlocked.Exchange(ref _agent, null)?.Dispose();
        // The viewer must not block its ActiveX UI thread waiting for logoff.
        // Wait asynchronously here before creating another desktop.
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (endingSession is not (0 or uint.MaxValue) && SessionExists(endingSession))
        {
            if (DateTime.UtcNow >= deadline) throw new IOException("后台桌面仍在注销，请稍后重新启动。");
            await Task.Delay(200).ConfigureAwait(false);
        }
    }

    private static bool SessionExists(uint id)
    {
        if (!WTSQuerySessionInformationW(0, id, 8, out var data, out _))
        {
            var error = Marshal.GetLastWin32Error();
            if (error is 2 or 1168 or 7022) return false;
            throw new System.ComponentModel.Win32Exception(error);
        }
        WTSFreeMemory(data);
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return; _disposed = true; _lifetime.Cancel();
        // Do not block WinUI on a pending user/password prompt. The viewer watches this process too.
        _pipe?.Dispose();
        try { _viewer?.CloseMainWindow(); } catch (InvalidOperationException) { }
        _agent?.Dispose(); _viewer?.Dispose();
        _instance.Dispose();
    }

    [DllImport("wtsapi32.dll", SetLastError = true)] private static extern bool WTSQuerySessionInformationW(nint server, uint id, int type, out nint data, out uint size);
    [DllImport("wtsapi32.dll")] private static extern void WTSFreeMemory(nint memory);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("wtsapi32.dll", SetLastError = true, CallingConvention = CallingConvention.Cdecl)]
    private static extern bool WTSGetChildSessionId(out uint id);
}
