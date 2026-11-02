using System.Buffers.Binary;
using CloudGI.Host.Core.Streaming;
using CloudGI.Host.Core.Configuration;
using Xunit;

namespace CloudGI.Host.Core.Tests;

public sealed class ChildSessionTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ChildSettingsCannotChangeParentRunMode(bool parentUsesChildSession)
    {
        var directory = Path.Combine(Path.GetTempPath(), "CloudGI-ChildSettings-" + Guid.NewGuid().ToString("N"));
        try
        {
            var shared = new JsonGameSettingsStore(Path.Combine(directory, "settings.json"));
            await shared.SaveAsync(new() { UseChildSession = parentUsesChildSession, GameExecutablePath = "original.exe" });
            var child = new ChildHostSettingsStore(shared);
            Assert.False((await child.LoadAsync()).UseChildSession);
            Assert.Equal(parentUsesChildSession, (await shared.LoadAsync()).UseChildSession);

            // The child can save real game options, but cannot enable nesting or turn off
            // the parent's background mode, including callers that save a whole record.
            await child.SaveAsync((await child.LoadAsync()) with { GameExecutablePath = "child.exe", UseChildSession = !parentUsesChildSession });
            var updated = await child.UpdateAsync(latest => latest with { UseHardwareEncoding = true, UseChildSession = true });
            Assert.False(updated.UseChildSession);
            var saved = await shared.LoadAsync();
            Assert.Equal(parentUsesChildSession, saved.UseChildSession);
            Assert.Equal("child.exe", saved.GameExecutablePath);
            Assert.True(saved.UseHardwareEncoding);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task ProtocolRoundTripsAChineseFailureAndSnapshot()
    {
        using var stream = new MemoryStream();
        var message = new ChildSessionReply(false, new(SunshineState.Failed, "后台桌面", 48989), Error: "子会话已断开");
        await ChildSessionProtocol.WriteAsync(stream, message, CancellationToken.None);
        stream.Position = 0;
        Assert.Equal(message, await ChildSessionProtocol.ReadAsync<ChildSessionReply>(stream, CancellationToken.None));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(65537)]
    public async Task RejectsInvalidFrameLengthsBeforeAllocating(int length)
    {
        var header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, length);
        using var stream = new MemoryStream(header);
        await Assert.ThrowsAsync<InvalidDataException>(() => ChildSessionProtocol.ReadAsync<ChildSessionRequest>(stream, CancellationToken.None));
    }

    [Fact]
    public async Task RejectsTruncatedFrames()
    {
        var bytes = new byte[5]; BinaryPrimitives.WriteInt32LittleEndian(bytes, 128);
        using var stream = new MemoryStream(bytes);
        await Assert.ThrowsAsync<EndOfStreamException>(() => ChildSessionProtocol.ReadAsync<ChildSessionRequest>(stream, CancellationToken.None));
    }

    [Fact]
    public void AcceptsPhoneAspectRatioAndRejectsInvalidDimensions()
    {
        Assert.Equal(2712, new ChildDisplaySettings(2712, 1220, 125).Validate().Width);
        Assert.Throws<ArgumentException>(() => new ChildDisplaySettings(0, 1080).Validate());
        Assert.Throws<ArgumentException>(() => new ChildDisplaySettings(1920, 9000).Validate());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EncoderChoiceAppliesToBothExecutionModes(bool childSession)
    {
        var config = new SunshineConfiguration(Path.GetTempPath(), childSession);
        var settings = new SunshineSettings("CloudGI");
        Assert.Contains("encoder = software", config.Render(settings, useHardwareEncoding: false));
        Assert.DoesNotContain("encoder = software", config.Render(settings, useHardwareEncoding: true));
    }

    [Fact]
    public void HardwareModeRejectsSoftwareFallbackAndIgnoresProbeAttempts()
    {
        const string log = "Trying encoder [nvenc]\nEncoder [nvenc] failed\nFound H.264 encoder: libx264 [software]\n";
        var actual = SunshineEncoding.ParseSelectedEncoder(log);
        Assert.Equal("software", actual);
        Assert.False(SunshineEncoding.MatchesSelection(actual!, useHardwareEncoding: true));
        Assert.True(SunshineEncoding.MatchesSelection(actual!, useHardwareEncoding: false));
        Assert.Null(SunshineEncoding.ParseSelectedEncoder("Trying encoder [nvenc]"));
    }

    [Theory]
    [InlineData("nvenc")]
    [InlineData("quicksync")]
    [InlineData("amdvce")]
    [InlineData("mediafoundation")]
    public void RecognizesSuccessfulHardwareSelection(string backend)
    {
        var selected = SunshineEncoding.ParseSelectedEncoder($"Found H.264 encoder: test [{backend}]\r\n");
        Assert.Equal(backend, selected);
        Assert.True(SunshineEncoding.MatchesSelection(selected!, true));
        Assert.False(SunshineEncoding.MatchesSelection(selected!, false));
    }

    [Fact]
    public void WgcIsEnabledOnlyForChildSessionConfiguration()
    {
        var settings = new SunshineSettings("CloudGI");
        Assert.Contains("capture = wgc", new SunshineConfiguration(Path.GetTempPath(), childSession: true).Render(settings));
        Assert.DoesNotContain("capture = wgc", new SunshineConfiguration(Path.GetTempPath()).Render(settings));
    }
}
