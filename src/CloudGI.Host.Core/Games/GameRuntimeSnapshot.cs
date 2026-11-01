namespace CloudGI.Host.Core.Games;

public enum GameRuntimeState { Unknown, NotRunning, Starting, Running }

/// <summary>Process state only; Running does not imply login or loading has completed.</summary>
public sealed record GameRuntimeSnapshot(GameRuntimeState State, int? ProcessId = null, string? Error = null);

public interface IGameStatusService
{
    Task<GameRuntimeSnapshot> GetStatusAsync(CancellationToken cancellationToken = default);
}

public interface IGameProcessReader
{
    int? FindRunningGame();
}
