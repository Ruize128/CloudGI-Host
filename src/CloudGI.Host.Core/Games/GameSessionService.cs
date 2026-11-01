using CloudGI.Host.Core.Configuration;
using System.ComponentModel;

namespace CloudGI.Host.Core.Games;

public enum GameSessionState { Starting, Running, Exited, Failed }
public sealed record GameSessionSnapshot(string RequestId, GameSessionState State, int? ProcessId = null, string? Error = null);

/// <summary>Bounded, in-memory start operations. HTTP disconnects never cancel or restart the game.</summary>
public sealed class GameSessionService(IGameLaunchService launcher, IGameStatusService status,
    CancellationToken lifetime = default)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, GameSessionSnapshot> _requests = new(StringComparer.Ordinal);
    private string? _starting;

    public GameSessionSnapshot Start(string requestId)
    {
        if (requestId.Length != 32 || !Guid.TryParseExact(requestId, "N", out _))
            throw new ArgumentException("Invalid request ID.", nameof(requestId));
        lock (_gate)
        {
            // Retrying the same request after failure/exit does not launch again.
            if (_requests.TryGetValue(requestId, out var existing)) return existing;
            if (_starting is not null) return _requests[_starting];
            lifetime.ThrowIfCancellationRequested();
            // Only recent completed requests are retained; clients never retry automatically.
            if (_requests.Count >= 64) _requests.Remove(_requests.Keys.First());
            var snapshot = new GameSessionSnapshot(requestId, GameSessionState.Starting);
            _requests.Add(requestId, snapshot);
            _starting = requestId;
            _ = Task.Run(() => LaunchAsync(requestId));
            return snapshot;
        }
    }

    public async Task<GameSessionSnapshot?> GetAsync(string requestId, CancellationToken token = default)
    {
        GameSessionSnapshot? snapshot;
        lock (_gate) snapshot = _requests.GetValueOrDefault(requestId);
        if (snapshot?.State != GameSessionState.Running) return snapshot;
        var runtime = await status.GetStatusAsync(token).ConfigureAwait(false);
        return runtime.State switch
        {
            GameRuntimeState.Running => snapshot with { ProcessId = runtime.ProcessId },
            GameRuntimeState.Unknown => snapshot with { State = GameSessionState.Failed, Error = "无法检测游戏状态，请查看 Host。", ProcessId = null },
            _ => snapshot with { State = GameSessionState.Exited, Error = "游戏已退出，请重新点击启动游戏。", ProcessId = null }
        };
    }

    private async Task LaunchAsync(string requestId)
    {
        GameSessionSnapshot snapshot;
        try
        {
            await launcher.LaunchAsync(lifetime).ConfigureAwait(false);
            // ShellExecute's returned PID can exit or hand off. Confirm the known game process independently.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            while (true)
            {
                var runtime = await status.GetStatusAsync(timeout.Token).ConfigureAwait(false);
                if (runtime.State == GameRuntimeState.Running)
                {
                    snapshot = new(requestId, GameSessionState.Running, runtime.ProcessId);
                    break;
                }
                if (runtime.State == GameRuntimeState.Unknown)
                    throw new InvalidOperationException("无法检测游戏状态，请查看 Host。");
                await Task.Delay(250, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            var message = ex switch
            {
                GameConfigurationException => ex.Message,
                OperationCanceledException when lifetime.IsCancellationRequested => "Host 正在关闭。",
                OperationCanceledException => "未能确认游戏进程，请查看电脑上的授权提示或游戏启动结果；不会自动重启游戏。",
                Win32Exception { NativeErrorCode: 1223 } => "电脑端取消了启动授权，请重新点击启动游戏。",
                Win32Exception => "Windows 未能启动游戏，请检查电脑上的授权提示及游戏文件。",
                InvalidOperationException => "无法确认游戏启动状态，请查看 Host。",
                _ => "游戏配置读取或启动失败，请在 Host 检查游戏路径、磁盘连接和启动参数。"
            };
            snapshot = new(requestId, GameSessionState.Failed, Error: message);
        }
        lock (_gate)
        {
            _requests[requestId] = snapshot;
            _starting = null;
        }
    }
}
