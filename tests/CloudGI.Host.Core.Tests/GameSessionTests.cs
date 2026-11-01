using CloudGI.Host.Core.Configuration;
using CloudGI.Host.Core.Games;
using Xunit;

namespace CloudGI.Host.Core.Tests;

public sealed class GameSessionTests
{
    private static string Id() => Guid.NewGuid().ToString("N");

    [Fact]
    public async Task RepeatedClicksSharePendingLaunchAndConfirmActualGamePid()
    {
        var game = new FakeGame();
        var sessions = new GameSessionService(game, game);
        var first = sessions.Start(Id());
        var repeated = sessions.Start(Id());
        Assert.Equal(first.RequestId, repeated.RequestId);
        await game.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, game.Starts);
        game.Pid = 2468; // The Windows shell's PID is deliberately different.
        game.Finish.SetResult(new(GameLaunchStatus.Started, 123));
        var running = await WaitAsync(sessions, first.RequestId);
        Assert.Equal(GameSessionState.Running, running.State);
        Assert.Equal(2468, running.ProcessId);
        game.Pid = null; // Disconnect/status queries do not start or kill anything.
        Assert.Equal(GameSessionState.Exited, (await sessions.GetAsync(first.RequestId))!.State);
        sessions.Start(first.RequestId);
        Assert.Equal(1, game.Starts);
    }

    [Fact]
    public async Task FailedRequestDoesNotRelaunchOnRetryAndNextClickCanRecover()
    {
        var game = new FakeGame();
        var sessions = new GameSessionService(game, game);
        var request = sessions.Start(Id());
        game.Finish.SetException(new GameConfigurationException("游戏文件不存在。"));
        var failed = await WaitAsync(sessions, request.RequestId);
        Assert.Equal(GameSessionState.Failed, failed.State);
        Assert.Equal("游戏文件不存在。", failed.Error);
        sessions.Start(request.RequestId);
        Assert.Equal(1, game.Starts);
        game.Finish = new(TaskCreationOptions.RunContinuationsAsynchronously);
        game.Pid = 456;
        var retry = sessions.Start(Id());
        game.Finish.SetResult(new(GameLaunchStatus.AlreadyRunning, 456));
        Assert.Equal(GameSessionState.Running, (await WaitAsync(sessions, retry.RequestId)).State);
        Assert.Equal(2, game.Starts);
    }

    [Fact]
    public async Task InvalidRequestAndUnknownStatusNeverLaunch()
    {
        var game = new FakeGame();
        var sessions = new GameSessionService(game, game);
        Assert.Throws<ArgumentException>(() => sessions.Start("arbitrary command"));
        Assert.Null(await sessions.GetAsync(Id()));
        Assert.Equal(0, game.Starts);
    }

    [Fact]
    public async Task RunningGameCanBeReusedWithMissingSavedConfiguration()
    {
        var game = new ExistingProcess();
        var launcher = new GameLaunchService(new UnavailableStore(), new(), game);
        Assert.Equal(new(GameLaunchStatus.AlreadyRunning, 777), await launcher.LaunchAsync());
    }

    private static async Task<GameSessionSnapshot> WaitAsync(GameSessionService sessions, string id)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var result = (await sessions.GetAsync(id, timeout.Token))!;
            if (result.State != GameSessionState.Starting) return result;
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class FakeGame : IGameLaunchService, IGameStatusService
    {
        public int Starts;
        public int? Pid;
        public TaskCompletionSource<GameLaunchResult> Finish = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<GameLaunchResult> LaunchAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Starts); Entered.TrySetResult();
            return Finish.Task.WaitAsync(cancellationToken);
        }
        public Task<GameRuntimeSnapshot> GetStatusAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Pid.HasValue ? new GameRuntimeSnapshot(GameRuntimeState.Running, Pid) : new(GameRuntimeState.NotRunning));
    }

    private sealed class ExistingProcess : IGameProcessLauncher
    {
        public int? FindRunningGame() => 777;
        public int Start(GameStartRequest request) => throw new InvalidOperationException("Must reuse existing process");
    }
    private sealed class UnavailableStore : IGameSettingsStore
    {
        public Task<GameLaunchSettings> LoadAsync(CancellationToken cancellationToken = default) => throw new IOException("NAS unavailable");
        public Task SaveAsync(GameLaunchSettings settings, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
