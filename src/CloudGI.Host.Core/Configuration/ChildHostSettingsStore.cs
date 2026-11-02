namespace CloudGI.Host.Core.Configuration;

/// <summary>Shares game/encoder settings while keeping the parent's run mode parent-owned.</summary>
public sealed class ChildHostSettingsStore(IGameSettingsStore shared) : IGameSettingsStore
{
    public async Task<GameLaunchSettings> LoadAsync(CancellationToken cancellationToken = default) =>
        ForChild(await shared.LoadAsync(cancellationToken).ConfigureAwait(false));

    public async Task SaveAsync(GameLaunchSettings settings, CancellationToken cancellationToken = default) =>
        await UpdateAsync(_ => settings, cancellationToken).ConfigureAwait(false);

    public async Task<GameLaunchSettings> UpdateAsync(Func<GameLaunchSettings, GameLaunchSettings> update,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        var saved = await shared.UpdateAsync(latest =>
            update(ForChild(latest)) with { UseChildSession = latest.UseChildSession }, cancellationToken).ConfigureAwait(false);
        return ForChild(saved);
    }

    private static GameLaunchSettings ForChild(GameLaunchSettings settings) => settings with { UseChildSession = false };
}
