using CloudGI.Host.Core.Configuration;
using CloudGI.Host.Core.Games;
using Xunit;

namespace CloudGI.Host.Core.Tests;

public sealed class GameStatusTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "CloudGI status " + Guid.NewGuid());

    private GameLaunchSettings CreateGame()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "YuanShen.exe");
        File.WriteAllText(path, "fixture; never executed");
        return new() { GameExecutablePath = path };
    }

    [Fact]
    public async Task ManualGameIsDiscoveredWithoutLoadingOrValidatingConfiguration()
    {
        var store = new MemoryStore(new());
        var process = new TestProcess { Pid = 123 };
        IGameStatusService service = new GameLaunchService(store, new(), process);
        Assert.Equal(new(GameRuntimeState.Running, 123), await service.GetStatusAsync());
        Assert.Equal(0, store.LoadCount);
        Assert.Equal(0, process.StartCount);
    }

    [Fact]
    public async Task ExitAndRestartAreDetectedUsingTheNewPid()
    {
        var process = new TestProcess { Pid = 123 };
        var service = new GameLaunchService(new MemoryStore(new()), new(), process);
        Assert.Equal(123, (await service.GetStatusAsync()).ProcessId);
        process.Pid = null;
        Assert.Equal(new(GameRuntimeState.NotRunning), await service.GetStatusAsync());
        process.Pid = 456;
        Assert.Equal(new(GameRuntimeState.Running, 456), await service.GetStatusAsync());
    }

    [Fact]
    public async Task StartingCanBeQueriedWhileWindowsStartIsPending()
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var process = new TestProcess
        {
            BeforeStart = () =>
            {
                entered.SetResult();
                if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
            }
        };
        var service = new GameLaunchService(new MemoryStore(CreateGame()), new(), process);
        var launch = service.LaunchAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(new(GameRuntimeState.Starting),
                await service.GetStatusAsync().WaitAsync(TimeSpan.FromSeconds(5)));
            // A live process takes precedence over a pending start operation.
            process.Pid = 789;
            Assert.Equal(new(GameRuntimeState.Running, 789), await service.GetStatusAsync());
        }
        finally { release.Set(); await launch; }
        Assert.Equal(GameRuntimeState.Running, (await service.GetStatusAsync()).State);
    }

    [Fact]
    public async Task FailedLaunchClearsStartingAndAllowsRetry()
    {
        var process = new TestProcess { BeforeStart = () => throw new IOException("start failed") };
        var service = new GameLaunchService(new MemoryStore(CreateGame()), new(), process);
        await Assert.ThrowsAsync<IOException>(() => service.LaunchAsync());
        Assert.Equal(GameRuntimeState.NotRunning, (await service.GetStatusAsync()).State);
        process.BeforeStart = null;
        Assert.Equal(GameLaunchStatus.Started, (await service.LaunchAsync()).Status);
        Assert.Equal(GameRuntimeState.Running, (await service.GetStatusAsync()).State);
    }

    [Fact]
    public async Task DetectionFailureIsUnknownAndRecoversOnNextQuery()
    {
        var process = new TestProcess { ScanError = new UnauthorizedAccessException("access denied") };
        var service = new GameLaunchService(new MemoryStore(new()), new(), process);
        var snapshot = await service.GetStatusAsync();
        Assert.Equal(GameRuntimeState.Unknown, snapshot.State);
        Assert.Null(snapshot.ProcessId);
        Assert.Equal("access denied", snapshot.Error);
        process.ScanError = null;
        Assert.Equal(new(GameRuntimeState.NotRunning), await service.GetStatusAsync());
    }

    [Fact]
    public async Task DetectionFailurePreventsDuplicateLaunch()
    {
        var process = new TestProcess { ScanError = new UnauthorizedAccessException("access denied") };
        var service = new GameLaunchService(new MemoryStore(CreateGame()), new(), process);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.LaunchAsync());
        Assert.Equal(0, process.StartCount);
    }

    [Fact]
    public async Task CancelledStatusQueryDoesNotScanProcesses()
    {
        var process = new TestProcess();
        var service = new GameLaunchService(new MemoryStore(new()), new(), process);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.GetStatusAsync(new CancellationToken(true)));
        Assert.Equal(0, process.ScanCount);
    }

    private sealed class MemoryStore(GameLaunchSettings settings) : IGameSettingsStore
    {
        public int LoadCount { get; private set; }
        public Task<GameLaunchSettings> LoadAsync(CancellationToken cancellationToken = default)
        {
            LoadCount++;
            return Task.FromResult(settings);
        }
        public Task SaveAsync(GameLaunchSettings value, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class TestProcess : IGameProcessLauncher
    {
        private int _pid;
        public int? Pid
        {
            get { var pid = Volatile.Read(ref _pid); return pid == 0 ? null : pid; }
            set => Volatile.Write(ref _pid, value ?? 0);
        }
        public int ScanCount;
        public int StartCount;
        public Exception? ScanError;
        public Action? BeforeStart;
        public int? FindRunningGame()
        {
            Interlocked.Increment(ref ScanCount);
            if (ScanError is not null) throw ScanError;
            return Pid;
        }
        public int Start(GameStartRequest request)
        {
            BeforeStart?.Invoke();
            Interlocked.Increment(ref StartCount);
            Pid = 789;
            return 789;
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
