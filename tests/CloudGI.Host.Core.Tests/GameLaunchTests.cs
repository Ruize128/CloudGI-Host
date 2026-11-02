using CloudGI.Host.Core.Configuration;
using CloudGI.Host.Core.Games;
using Xunit;

namespace CloudGI.Host.Core.Tests;

public sealed class GameLaunchTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "CloudGI tests 游戏 " + Guid.NewGuid());

    public GameLaunchTests() => Directory.CreateDirectory(_directory);

    private GameLaunchSettings CreateGame(string arguments = "")
    {
        var executable = Path.Combine(_directory, "YuanShen.exe");
        File.WriteAllText(executable, "test fixture; never executed");
        return new GameLaunchSettings { GameExecutablePath = executable, LaunchArguments = arguments };
    }

    [Fact]
    public async Task MissingConfigurationReturnsEmptySettings()
    {
        var store = new JsonGameSettingsStore(Path.Combine(_directory, "settings.json"));
        Assert.Equal(new GameLaunchSettings(), await store.LoadAsync());
    }

    [Fact]
    public async Task SettingsRoundTripPreservesUnicodeAndQuotedArguments()
    {
        var store = new JsonGameSettingsStore(Path.Combine(_directory, "config", "settings.json"));
        var settings = CreateGame("-screen-width 1280 -example \"value with spaces\"");
        await store.SaveAsync(settings);
        Assert.Equal(settings, await store.LoadAsync());
        await store.SaveAsync(settings with { LaunchArguments = "" });
        Assert.Empty((await store.LoadAsync()).LaunchArguments);
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(store.FilePath)!));
    }

    [Fact]
    public async Task CorruptConfigurationIsReportedAndNotOverwritten()
    {
        var path = Path.Combine(_directory, "settings.json");
        await File.WriteAllTextAsync(path, "{ broken json");
        await Assert.ThrowsAsync<InvalidDataException>(() => new JsonGameSettingsStore(path).LoadAsync());
        Assert.Equal("{ broken json", await File.ReadAllTextAsync(path));
    }

    [Theory]
    [InlineData("{\"schemaVersion\":99}")]
    [InlineData("{\"gameExecutablePath\":null}")]
    public async Task UnsupportedConfigurationIsReported(string json)
    {
        var path = Path.Combine(_directory, "settings.json");
        await File.WriteAllTextAsync(path, json);
        await Assert.ThrowsAsync<InvalidDataException>(() => new JsonGameSettingsStore(path).LoadAsync());
    }

    [Fact]
    public void DirectoryAndQuotedExecutableResolveToSameGame()
    {
        var settings = CreateGame();
        var validator = new GameLaunchValidator();
        Assert.Equal(settings, validator.Normalize(settings with { GameExecutablePath = _directory }));
        Assert.Equal(settings, validator.Normalize(settings with { GameExecutablePath = "\"" + settings.GameExecutablePath + "\"" }));
    }

    [Fact]
    public void AmbiguousDirectoryRequiresExplicitExecutable()
    {
        CreateGame();
        File.WriteAllText(Path.Combine(_directory, "GenshinImpact.exe"), "fixture");
        Assert.Throws<GameConfigurationException>(() => new GameLaunchValidator().Normalize(
            new GameLaunchSettings { GameExecutablePath = _directory }));
    }

    [Theory]
    [InlineData("")]
    [InlineData("YuanShen.exe")]
    public void EmptyOrRelativePathIsRejected(string path) =>
        Assert.Throws<GameConfigurationException>(() => new GameLaunchValidator().Normalize(
            new GameLaunchSettings { GameExecutablePath = path }));

    [Fact]
    public void LauncherAndMissingGameAreRejected()
    {
        var path = Path.Combine(_directory, "launcher.exe");
        File.WriteAllText(path, "fixture");
        var validator = new GameLaunchValidator();
        Assert.Throws<GameConfigurationException>(() => validator.Normalize(new() { GameExecutablePath = path }));
        Assert.Throws<GameConfigurationException>(() => validator.Normalize(new()
        {
            GameExecutablePath = Path.Combine(_directory, "YuanShen.exe")
        }));
    }

    [Theory]
    [InlineData("-width 1280\n-height 720")]
    [InlineData("bad\0argument")]
    public void ControlCharactersInArgumentsAreRejected(string arguments) =>
        Assert.Throws<GameConfigurationException>(() => new GameLaunchValidator().Normalize(CreateGame(arguments)));

    [Fact]
    public async Task ServiceUsesSavedArgumentsAndGameWorkingDirectoryWithoutAWindow()
    {
        var settings = CreateGame("-screen-width 1280 -example \"quoted value\"");
        var store = new JsonGameSettingsStore(Path.Combine(_directory, "settings.json"));
        await store.SaveAsync(settings);
        var launcher = new FakeLauncher();
        var service = new GameLaunchService(store, new(), launcher);
        var result = await service.LaunchAsync();
        Assert.Equal(GameLaunchStatus.Started, result.Status);
        Assert.Equal(new GameStartRequest(settings.GameExecutablePath, settings.LaunchArguments + " -screen-fullscreen 1 -window-mode borderless", _directory, TouchscreenMode: true), launcher.LastRequest);
    }

    [Fact]
    public async Task DisabledMobileUiIsPassedToOrdinaryLauncherWithoutOldPlatformFlags()
    {
        var settings = CreateGame("use_mobile_platform -is_cloud 1 -platform_type CLOUD_THIRD_PARTY_MOBILE")
            with { TouchscreenMode = false };
        var store = new JsonGameSettingsStore(Path.Combine(_directory, "settings.json"));
        await store.SaveAsync(settings);
        var launcher = new FakeLauncher();
        await new GameLaunchService(store, new(), launcher).LaunchAsync();
        Assert.False(launcher.LastRequest!.TouchscreenMode);
        Assert.DoesNotContain("platform", launcher.LastRequest.Arguments);
        Assert.DoesNotContain("is_cloud", launcher.LastRequest.Arguments);
    }

    [Fact]
    public async Task AtomicOptionUpdatesPreserveOtherFieldsAndDoNotNeedAnAccessibleGame()
    {
        var store = new JsonGameSettingsStore(Path.Combine(_directory, "settings.json"));
        var settings = new GameLaunchSettings { GameExecutablePath = "offline NAS", LaunchArguments = "-example keep" };
        await store.SaveAsync(settings);
        await Task.WhenAll(
            store.UpdateAsync(latest => latest with { TouchscreenMode = false }),
            store.UpdateAsync(latest => latest with { Fullscreen = false }));
        var actual = await store.LoadAsync();
        Assert.Equal(settings with { TouchscreenMode = false, Fullscreen = false }, actual);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.UpdateAsync(_ => throw new InvalidOperationException()));
        Assert.Equal(actual, await store.LoadAsync());
    }

    [Fact]
    public async Task ConcurrentLaunchesCreateOnlyOneProcess()
    {
        var store = new JsonGameSettingsStore(Path.Combine(_directory, "settings.json"));
        await store.SaveAsync(CreateGame());
        var launcher = new FakeLauncher();
        var service = new GameLaunchService(store, new(), launcher);
        var results = await Task.WhenAll(service.LaunchAsync(), service.LaunchAsync());
        Assert.Equal(1, launcher.StartCount);
        Assert.Single(results, r => r.Status == GameLaunchStatus.Started);
        Assert.Single(results, r => r.Status == GameLaunchStatus.AlreadyRunning);
    }

    [Fact]
    public async Task InvalidSavedSettingsNeverCreateAProcess()
    {
        var launcher = new FakeLauncher();
        var service = new GameLaunchService(new JsonGameSettingsStore(Path.Combine(_directory, "settings.json")), new(), launcher);
        await Assert.ThrowsAsync<GameConfigurationException>(() => service.LaunchAsync());
        Assert.Equal(0, launcher.StartCount);
    }

    [Fact]
    public async Task CancellationBeforeLaunchNeverCreatesAProcess()
    {
        var launcher = new FakeLauncher();
        var service = new GameLaunchService(new JsonGameSettingsStore(Path.Combine(_directory, "settings.json")), new(), launcher);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.LaunchAsync(new CancellationToken(true)));
        Assert.Equal(0, launcher.StartCount);
    }

    private sealed class FakeLauncher : IGameProcessLauncher
    {
        public int StartCount { get; private set; }
        public GameStartRequest? LastRequest { get; private set; }
        public int? FindRunningGame() => StartCount == 0 ? null : 12345;
        public int Start(GameStartRequest request)
        {
            LastRequest = request;
            StartCount++;
            return 12345;
        }
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
