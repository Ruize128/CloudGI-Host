using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace CloudGI.Host.Core.Streaming;

/// <summary>Authorizes game control with an enabled Sunshine device's existing private key.</summary>
public sealed class PairedClientRequestVerifier(string statePath)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, long> _nonces = new(StringComparer.Ordinal);

    public async Task<bool> VerifyAsync(string method, string path, string device, string timestamp,
        string nonce, string signature, CancellationToken token = default)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (device.Length != 64 || device.Any(c => !Uri.IsHexDigit(c)) ||
            nonce.Length != 32 || !Guid.TryParseExact(nonce, "N", out _) || signature.Length > 1024 ||
            !long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out var time) ||
            time < now - 60 || time > now + 60) return false;
        try
        {
            // Re-read on every request so removing/disabling a paired device takes effect immediately.
            await using var stream = new FileStream(statePath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
            if (stream.Length > 4 * 1024 * 1024) return false;
            using var state = await JsonDocument.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false);
            var devices = state.RootElement.GetProperty("root").GetProperty("named_devices");
            if (devices.ValueKind != JsonValueKind.Array) return false;
            X509Certificate2? matched = null;
            try
            {
                foreach (var entry in devices.EnumerateArray())
                {
                    using var certificate = X509Certificate2.CreateFromPem(entry.GetProperty("cert").GetString()!);
                    if (!string.Equals(Convert.ToHexString(SHA256.HashData(certificate.RawData)), device, StringComparison.OrdinalIgnoreCase)) continue;
                    if (matched is not null || entry.TryGetProperty("enabled", out var enabled) &&
                        enabled.ToString() is not ("true" or "True" or "1")) return false;
                    matched = X509CertificateLoader.LoadCertificate(certificate.RawData);
                }
                if (matched is null) return false;
                using var rsa = matched.GetRSAPublicKey();
                var payload = Encoding.UTF8.GetBytes($"CLOUDGI-GAME-1\n{method}\n{path}\n{timestamp}\n{nonce}");
                if (rsa is null || !rsa.VerifyData(payload, Convert.FromBase64String(signature), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)) return false;
                lock (_gate)
                {
                    foreach (var key in _nonces.Where(p => p.Value < now - 60).Select(p => p.Key).ToArray()) _nonces.Remove(key);
                    if (_nonces.Count >= 4096) return false;
                    return _nonces.TryAdd(device.ToUpperInvariant() + nonce, time);
                }
            }
            finally { matched?.Dispose(); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or
            CryptographicException or ArgumentException or FormatException or InvalidOperationException or KeyNotFoundException)
        { return false; }
    }
}
