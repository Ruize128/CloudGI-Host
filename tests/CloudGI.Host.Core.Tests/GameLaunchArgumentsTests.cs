using CloudGI.Host.Core.Configuration;
using CloudGI.Host.Core.Games;
using Xunit;

namespace CloudGI.Host.Core.Tests;

public sealed class GameLaunchArgumentsTests
{
    [Fact]
    public void DefaultsRequestBorderlessFullscreenWithoutObsoletePlatformFlags()
    {
        Assert.Equal("-screen-fullscreen 1 -window-mode borderless",
            GameLaunchArguments.Build(new()));
    }

    [Theory]
    [InlineData("-screen-fullscreen 0 -window-mode exclusive -is_cloud 0 -platform_type CLOUD_THIRD_PARTY_PC use_mobile_platform")]
    [InlineData("-screen-fullscreen=0 -window-mode=\"exclusive\" -is_cloud=0 -platform_type=CLOUD_THIRD_PARTY_PC")]
    public void ManagedOptionsReplaceConflictsWithoutDuplicates(string arguments)
    {
        var settings = new GameLaunchSettings { LaunchArguments = arguments };
        Assert.Equal(GameLaunchArguments.Build(new()), GameLaunchArguments.Build(settings));
        Assert.Equal(arguments, settings.LaunchArguments); // Never rewrite user configuration.
    }

    [Fact]
    public void OtherArgumentsAndQuotedValuesArePreserved()
    {
        const string custom = "-screen-width 1280 -example \"keep -screen-fullscreen 0 here\" -path \"C:\\游戏 folder\\file\"";
        var actual = GameLaunchArguments.Build(new() { LaunchArguments = custom });
        Assert.StartsWith(custom + " -screen-fullscreen 1", actual);
    }

    [Fact]
    public void MissingManagedValueDoesNotConsumeNextFlag()
    {
        var actual = GameLaunchArguments.Build(new() { LaunchArguments = "-screen-fullscreen -screen-width 1280" });
        Assert.StartsWith("-screen-width 1280 -screen-fullscreen 1", actual);
    }

    [Fact]
    public void OptionsCanBeDisabledWithoutLeavingOldMobileArguments()
    {
        var actual = GameLaunchArguments.Build(new()
        {
            Fullscreen = false, TouchscreenMode = false,
            LaunchArguments = "-screen-width 1280 use_mobile_platform -is_cloud 1 -platform_type CLOUD_THIRD_PARTY_MOBILE -screen-fullscreen 1"
        });
        Assert.Equal("-screen-width 1280 -screen-fullscreen 0", actual);
    }

    [Fact]
    public async Task OldConfigurationGetsDefaultsAndNewOptionsRoundTrip()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            const string json = "{\"schemaVersion\":1,\"gameExecutablePath\":\"\",\"launchArguments\":\"-screen-fullscreen 0\"}";
            await File.WriteAllTextAsync(path, json);
            var store = new JsonGameSettingsStore(path);
            var settings = await store.LoadAsync();
            Assert.True(settings.Fullscreen); Assert.True(settings.TouchscreenMode);
            Assert.Equal(json, await File.ReadAllTextAsync(path));
            await store.SaveAsync(settings with { Fullscreen = false, TouchscreenMode = false });
            var restored = await store.LoadAsync();
            Assert.False(restored.Fullscreen); Assert.False(restored.TouchscreenMode);
            Assert.Equal("-screen-fullscreen 0", restored.LaunchArguments);
        }
        finally { File.Delete(path); }
    }
}
