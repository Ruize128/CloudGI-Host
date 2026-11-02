namespace CloudGI.Host.Core.Configuration;

public sealed record GameLaunchSettings
{
    public int SchemaVersion { get; init; } = 1;
    public string GameExecutablePath { get; init; } = "";
    public string LaunchArguments { get; init; } = "";
    public bool Fullscreen { get; init; } = true;
    public bool TouchscreenMode { get; init; } = true;
    // Applied once by the foreground Host at startup. Child agents never create another child.
    public bool UseChildSession { get; init; } = true;
    // One encoder choice for both execution modes; software preserves the verified default.
    public bool UseHardwareEncoding { get; init; } = false;
}

public interface IGameSettingsStore
{
    Task<GameLaunchSettings> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(GameLaunchSettings settings, CancellationToken cancellationToken = default);
    async Task<GameLaunchSettings> UpdateAsync(Func<GameLaunchSettings, GameLaunchSettings> update,
        CancellationToken cancellationToken = default)
    {
        var settings = update(await LoadAsync(cancellationToken));
        await SaveAsync(settings, cancellationToken);
        return settings;
    }
}
