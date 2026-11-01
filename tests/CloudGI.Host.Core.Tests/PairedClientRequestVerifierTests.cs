using CloudGI.Host.Core.Streaming;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Xunit;

namespace CloudGI.Host.Core.Tests;

public sealed class PairedClientRequestVerifierTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
    private readonly RSA _key = RSA.Create(2048);
    private readonly X509Certificate2 _certificate;
    public PairedClientRequestVerifierTests() => _certificate = new CertificateRequest("CN=Test device", _key,
        HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));

    private Task SaveAsync(bool enabled) => File.WriteAllTextAsync(_path, JsonSerializer.Serialize(new
    {
        root = new { named_devices = new[] { new { cert = _certificate.ExportCertificatePem(), enabled = enabled ? "true" : "false" } } }
    }));

    private Task<bool> VerifyAsync(PairedClientRequestVerifier verifier, string nonce, string? signedPath = null, long? timestamp = null)
    {
        const string path = "/cloudgi/game/start/01234567890123456789012345678901";
        var time = (timestamp ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds()).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var payload = Encoding.UTF8.GetBytes($"CLOUDGI-GAME-1\nPOST\n{signedPath ?? path}\n{time}\n{nonce}");
        var signature = Convert.ToBase64String(_key.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        return verifier.VerifyAsync("POST", path, Convert.ToHexString(SHA256.HashData(_certificate.RawData)), time, nonce, signature);
    }

    [Fact]
    public async Task PairedDeviceCanSignOnceAndRevocationAppliesImmediately()
    {
        await SaveAsync(true);
        var verifier = new PairedClientRequestVerifier(_path);
        var nonce = Guid.NewGuid().ToString("N");
        Assert.True(await VerifyAsync(verifier, nonce));
        Assert.False(await VerifyAsync(verifier, nonce));
        await SaveAsync(false);
        Assert.False(await VerifyAsync(verifier, Guid.NewGuid().ToString("N")));
    }

    [Fact]
    public async Task ChangedRouteAndStaleSignatureAreRejected()
    {
        await SaveAsync(true);
        var verifier = new PairedClientRequestVerifier(_path);
        Assert.False(await VerifyAsync(verifier, Guid.NewGuid().ToString("N"), signedPath: "/cloudgi/game/status/other"));
        Assert.False(await VerifyAsync(verifier, Guid.NewGuid().ToString("N"), timestamp: DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 120));
    }

    [Fact]
    public async Task MissingOrCorruptPairingStateFailsClosed()
    {
        var verifier = new PairedClientRequestVerifier(_path);
        Assert.False(await VerifyAsync(verifier, Guid.NewGuid().ToString("N")));
        await File.WriteAllTextAsync(_path, "{}");
        Assert.False(await VerifyAsync(verifier, Guid.NewGuid().ToString("N")));
    }

    public void Dispose() { File.Delete(_path); _certificate.Dispose(); _key.Dispose(); }
}
