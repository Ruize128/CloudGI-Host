namespace CloudGI.Host.Core.Games;

public sealed record GameProcessIdentity(int ProcessId, long StartTimeUtcTicks, int SessionId);
public interface IGameProcessController
{
    GameProcessIdentity Capture(int processId);
    Task StopAsync(GameProcessIdentity identity, CancellationToken token = default);
}

/// <summary>One streaming lease. Explicit disconnect or missed heartbeats closes only its captured game.</summary>
public sealed class GameStreamLease(IGameProcessController processes, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _requestId;
    private GameProcessIdentity? _game;
    private long _renewed;
    private bool _connected;
    private volatile bool _closing;
    public bool IsClosing => _closing;
    public string? LastError { get; private set; }

    public async Task BeginAsync(string requestId, int processId, CancellationToken token = default)
    {
        if (!_gate.Wait(0)) throw new InvalidOperationException("游戏正在退出，请稍后重新启动。");
        try
        {
            token.ThrowIfCancellationRequested();
            if (_requestId == requestId) return;
            if (_requestId is not null) throw new InvalidOperationException("已有串流会话，请先断开原来的连接。");
            _game = processes.Capture(processId);
            _requestId = requestId;
            _connected = false;
            _renewed = _clock.GetTimestamp();
            LastError = null;
        }
        finally { _gate.Release(); }
        await Task.CompletedTask;
    }

    public async Task<bool> HeartbeatAsync(string requestId, CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_requestId != requestId) return false;
            _connected = true;
            _renewed = _clock.GetTimestamp();
            return true;
        }
        finally { _gate.Release(); }
    }

    public async Task EndAsync(string requestId, CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try { if (_requestId == requestId) await CloseCoreAsync().ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    public async Task CheckTimeoutAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            // Allow the initial Moonlight connection time, then require a heartbeat every eight seconds.
            if (_requestId is not null && _clock.GetElapsedTime(_renewed) >= TimeSpan.FromSeconds(_connected ? 8 : 45))
                await CloseCoreAsync().ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task EndCurrentAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await CloseCoreAsync().ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private async Task CloseCoreAsync()
    {
        if (_game is null) return;
        var game = _game;
        _requestId = null;
        _game = null;
        _closing = true;
        try
        {
            // Once closing starts, a lost HTTP request must not interrupt cleanup.
            await processes.StopAsync(game, CancellationToken.None).ConfigureAwait(false);
            LastError = null;
        }
        catch (Exception) { LastError = "自动退出游戏失败，请检查 Host 管理员权限并在电脑上关闭游戏。"; }
        finally { _closing = false; }
    }
}
