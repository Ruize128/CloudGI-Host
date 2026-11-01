using CloudGI.Host.Core.Games;
using Xunit;

namespace CloudGI.Host.Core.Tests;

public sealed class GameStreamLeaseTests
{
    [Fact]
    public async Task ExplicitDisconnectClosesCapturedGameOnceAndOldDisconnectCannotCloseNewGame()
    {
        var processes = new Controller();
        var lease = new GameStreamLease(processes);
        await lease.BeginAsync("first", 123);
        await lease.HeartbeatAsync("first");
        await lease.EndAsync("other");
        Assert.Empty(processes.Stopped);
        await lease.EndAsync("first");
        await lease.EndAsync("first");
        Assert.Equal(new GameProcessIdentity(123, 1000, 1), Assert.Single(processes.Stopped));
        await lease.BeginAsync("second", 456);
        await lease.EndAsync("first");
        Assert.Single(processes.Stopped);
        await lease.EndAsync("second");
        Assert.Equal(456, processes.Stopped.Last().ProcessId);
    }

    [Fact]
    public async Task ActiveHeartbeatPreservesGameAndKilledClientExpiresWithoutAnyHttpRequest()
    {
        var time = new Clock(); var processes = new Controller();
        var lease = new GameStreamLease(processes, time);
        await lease.BeginAsync("stream", 123);
        await lease.HeartbeatAsync("stream");
        time.Now = 7000;
        await lease.CheckTimeoutAsync(); Assert.Empty(processes.Stopped);
        await lease.HeartbeatAsync("stream");
        time.Now = 14000;
        await lease.CheckTimeoutAsync(); Assert.Empty(processes.Stopped);
        time.Now = 15000;
        await lease.CheckTimeoutAsync(); Assert.Single(processes.Stopped);
        Assert.False(await lease.HeartbeatAsync("stream"));
        await lease.CheckTimeoutAsync(); Assert.Single(processes.Stopped);
    }

    [Fact]
    public async Task FailedInitialStreamGetsLongerGraceAndThenCloses()
    {
        var time = new Clock(); var processes = new Controller();
        var lease = new GameStreamLease(processes, time);
        await lease.BeginAsync("stream", 123);
        time.Now = 44000;
        await lease.CheckTimeoutAsync(); Assert.Empty(processes.Stopped);
        time.Now = 45000;
        await lease.CheckTimeoutAsync(); Assert.Single(processes.Stopped);
    }

    [Fact]
    public async Task NoLeaseNeverClosesManuallyStartedGameAndSecondStreamCannotReplaceActiveLease()
    {
        var processes = new Controller(); var lease = new GameStreamLease(processes);
        await lease.CheckTimeoutAsync(); Assert.Empty(processes.Stopped);
        await lease.BeginAsync("first", 123);
        await Assert.ThrowsAsync<InvalidOperationException>(() => lease.BeginAsync("second", 456));
        await lease.EndAsync("first");
        Assert.Equal(123, Assert.Single(processes.Stopped).ProcessId);
    }

    [Fact]
    public async Task NewStreamIsBlockedDuringCloseAndCloseFailureIsVisible()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var processes = new Controller { Pending = release.Task };
        var lease = new GameStreamLease(processes);
        await lease.BeginAsync("first", 123);
        var ending = lease.EndAsync("first");
        Assert.True(lease.IsClosing);
        await Assert.ThrowsAsync<InvalidOperationException>(() => lease.BeginAsync("second", 456));
        release.SetException(new UnauthorizedAccessException());
        await ending;
        Assert.False(lease.IsClosing);
        Assert.NotNull(lease.LastError);
    }

    private sealed class Clock : TimeProvider
    {
        public long Now;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Now;
    }
    private sealed class Controller : IGameProcessController
    {
        public readonly List<GameProcessIdentity> Stopped = [];
        public Task? Pending;
        public GameProcessIdentity Capture(int processId) => new(processId, 1000, 1);
        public Task StopAsync(GameProcessIdentity identity, CancellationToken token = default)
        { Stopped.Add(identity); return Pending ?? Task.CompletedTask; }
    }
}
