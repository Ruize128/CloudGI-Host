using CloudGI.Host.Core.Configuration;

namespace CloudGI.Host.Core.Games;

public enum GameLaunchStatus { Started, AlreadyRunning }
public sealed record GameLaunchResult(GameLaunchStatus Status, int ProcessId);
public sealed record GameStartRequest(string ExecutablePath, string Arguments, string WorkingDirectory);

public interface IGameProcessLauncher : IGameProcessReader
{
    int Start(GameStartRequest request);
}

public interface IGameLaunchService
{
    Task<GameLaunchResult> LaunchAsync(CancellationToken cancellationToken = default);
}

/// <summary>Uses Host-owned saved settings; callers do not supply commands or executable paths.</summary>
public sealed class GameLaunchService(
    IGameSettingsStore settingsStore,
    GameLaunchValidator validator,
    IGameProcessLauncher processLauncher) : IGameLaunchService, IGameStatusService
{
    private int _isLaunching;
    private readonly SemaphoreSlim _launchGate = new(1, 1);

    public Task<GameRuntimeSnapshot> GetStatusAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                // No dependency on saved settings: also discover games started outside Host.
                if (processLauncher.FindRunningGame() is int processId)
                    return new GameRuntimeSnapshot(GameRuntimeState.Running, processId);
                return new GameRuntimeSnapshot(Volatile.Read(ref _isLaunching) != 0
                    ? GameRuntimeState.Starting : GameRuntimeState.NotRunning);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return new GameRuntimeSnapshot(GameRuntimeState.Unknown, Error: ex.Message);
            }
        }, cancellationToken);

    public async Task<GameLaunchResult> LaunchAsync(CancellationToken cancellationToken = default)
    {
        await _launchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref _isLaunching, 1);
        try
        {
            // Reconnect to a running game even when the saved path is temporarily unavailable.
            var existingId = await Task.Run(processLauncher.FindRunningGame, cancellationToken).ConfigureAwait(false);
            if (existingId is int runningId)
                return new GameLaunchResult(GameLaunchStatus.AlreadyRunning, runningId);
            var saved = await settingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            return await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var settings = validator.Normalize(saved);
                cancellationToken.ThrowIfCancellationRequested();
                if (processLauncher.FindRunningGame() is int currentId)
                    return new GameLaunchResult(GameLaunchStatus.AlreadyRunning, currentId);

                var request = new GameStartRequest(settings.GameExecutablePath, settings.LaunchArguments,
                    Path.GetDirectoryName(settings.GameExecutablePath)!);
                // Once Start succeeds, report the result even if the caller cancels meanwhile.
                return new GameLaunchResult(GameLaunchStatus.Started, processLauncher.Start(request));
            }, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _isLaunching, 0);
            _launchGate.Release();
        }
    }
}
