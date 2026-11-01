using CloudGI.Host.Core.Streaming;
using System.Net;
using System.Net.Sockets;
using Xunit;

namespace CloudGI.Host.Core.Tests;

public sealed class SunshineConfigurationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "CloudGI sunshine 测试 " + Guid.NewGuid());

    [Fact]
    public async Task ConfigurationAndPersistentIdentityAreIsolatedAndPreserved()
    {
        var config = new SunshineConfiguration(_root);
        Directory.CreateDirectory(_root);
        var state = config.FilePath("state.json");
        await File.WriteAllTextAsync(state, "existing pairing identity");
        var settings = new SunshineSettings("CloudGI 中文主机", SunshineConfiguration.FindAvailablePort(48989));
        await config.SaveAsync(settings);
        await config.WriteAsync(settings);
        Assert.Equal(settings, await config.LoadAsync());
        var text = await File.ReadAllTextAsync(config.FilePath("sunshine.conf"));
        Assert.StartsWith("sunshine_name = CloudGI 中文主机", text);
        Assert.Contains("file_state = " + state, text);
        Assert.Contains("origin_web_ui_allowed = pc", text);
        Assert.DoesNotContain("global_prep_cmd", text);
        Assert.DoesNotContain("resolution =", text);
        await config.WriteAsync(settings with { Name = "Renamed" });
        Assert.Equal("existing pairing identity", await File.ReadAllTextAsync(state));
        Assert.DoesNotContain("cmd", await File.ReadAllTextAsync(config.FilePath("apps.json")));
    }

    [Fact]
    public async Task FirstStartupPersistsIdentityBeforeSunshineRuns()
    {
        var config = new SunshineConfiguration(_root);
        var settings = await config.LoadAsync();
        await config.WriteAsync(settings);
        var identity = await File.ReadAllTextAsync(config.FilePath("state.json"));
        Assert.Contains("uniqueid", identity);
        await config.WriteAsync(settings);
        Assert.Equal(identity, await File.ReadAllTextAsync(config.FilePath("state.json")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("bad\nport = 47989")]
    [InlineData("bad#name")]
    [InlineData("bad=name")]
    public void NameCannotInjectConfiguration(string name) => Assert.Throws<ArgumentException>(() => SunshineConfiguration.ValidateName(name));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConflictInAnyDerivedPortSelectsAnotherCompleteGroup(bool udp)
    {
        var port = SunshineConfiguration.FindAvailablePort(48989);
        using var socket = new Socket(AddressFamily.InterNetwork, udp ? SocketType.Dgram : SocketType.Stream, udp ? ProtocolType.Udp : ProtocolType.Tcp);
        socket.ExclusiveAddressUse = true;
        socket.Bind(new IPEndPoint(IPAddress.Any, port + (udp ? 11 : 21)));
        Assert.False(SunshineConfiguration.CanBind(port));
        var selected = SunshineConfiguration.FindAvailablePort(port);
        Assert.NotEqual(port, selected);
        Assert.True(SunshineConfiguration.CanBind(selected));
    }

    [Fact]
    public async Task CorruptSettingsAreReportedAndPreserved()
    {
        Directory.CreateDirectory(_root);
        var config = new SunshineConfiguration(_root);
        await File.WriteAllTextAsync(config.FilePath("instance.json"), "broken");
        await Assert.ThrowsAsync<InvalidDataException>(() => config.LoadAsync());
        Assert.Equal("broken", await File.ReadAllTextAsync(config.FilePath("instance.json")));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
