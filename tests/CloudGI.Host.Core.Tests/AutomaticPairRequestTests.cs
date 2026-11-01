using CloudGI.Host.Core.Streaming;
using System.Net;
using Xunit;
namespace CloudGI.Host.Core.Tests;
public sealed class AutomaticPairRequestTests
{
    private readonly AutomaticPairRequest _request = new(new string('a',32), "1234", "Android");
    [Fact]
    public void SessionMustMatchClientIdentityAndSourceAddress()
    {
        _request.Validate();
        Assert.True(_request.Matches(new("id", _request.PairingName, "192.168.1.2"), IPAddress.Parse("::ffff:192.168.1.2")));
        Assert.False(_request.Matches(new("id", "other device", "192.168.1.2"), IPAddress.Parse("192.168.1.2")));
        Assert.False(_request.Matches(new("id", _request.PairingName, "192.168.1.3"), IPAddress.Parse("192.168.1.2")));
    }
    [Fact]
    public void InvalidAutomaticRequestsAreRejected()
    {
        Assert.Throws<ArgumentException>(() => (_request with { ClientId = "bad" }).Validate());
        Assert.Throws<ArgumentException>(() => (_request with { Pin = "123a" }).Validate());
        Assert.Throws<ArgumentException>(() => (_request with { DeviceName = "bad\nname" }).Validate());
    }
}
