using CloudGI.Host.Core.Configuration;

namespace CloudGI.Host.Core.Games;

public enum GameLaunchStatus { Started, AlreadyRunning }
public sealed record GameLaunchResult(GameLaunchStatus Status, int ProcessId);
public sealed record GameStartRequest(string ExecutablePath, string Arguments, string WorkingDirectory);

public interface IGameProcessLauncher
{
    int? FindRunningGame(string executablePath);
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
    IGameProcessLauncher processLauncher) : IGameLaunchService
{
    private readonly SemaphoreSlim _launchGate = new(1, 1);

    public async Task<GameLaunchResult> LaunchAsync(CancellationToken cancellationToken = default)
    {
        await _launchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var saved = await settingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            var settings = validator.Normalize(saved);
            return await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (processLauncher.FindRunningGame(settings.GameExecutablePath) is int existingId)
                    return new GameLaunchResult(GameLaunchStatus.AlreadyRunning, existingId);

                var request = new GameStartRequest(settings.GameExecutablePath, settings.LaunchArguments,
                    Path.GetDirectoryName(settings.GameExecutablePath)!);
                // Once Start succeeds, report the result even if the caller cancels meanwhile.
                return new GameLaunchResult(GameLaunchStatus.Started, processLauncher.Start(request));
            }, CancellationToken.None).ConfigureAwait(false);
        }
        finally { _launchGate.Release(); }
    }
}
