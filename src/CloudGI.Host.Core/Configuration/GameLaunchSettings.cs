namespace CloudGI.Host.Core.Configuration;

public sealed record GameLaunchSettings
{
    public int SchemaVersion { get; init; } = 1;
    public string GameExecutablePath { get; init; } = "";
    public string LaunchArguments { get; init; } = "";
}

public interface IGameSettingsStore
{
    Task<GameLaunchSettings> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(GameLaunchSettings settings, CancellationToken cancellationToken = default);
}
