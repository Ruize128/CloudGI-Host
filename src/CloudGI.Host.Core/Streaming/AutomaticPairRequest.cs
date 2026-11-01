using System.Net;
using System.Text;

namespace CloudGI.Host.Core.Streaming;

public sealed record AutomaticPairRequest(string ClientId, string Pin, string DeviceName)
{
    public string PairingName => "CloudGI-" + ClientId;

    public void Validate()
    {
        if (ClientId is null || ClientId.Length != 32 || ClientId.Any(c => !Uri.IsHexDigit(c)))
            throw new ArgumentException("Invalid CloudGI client identity.");
        if (Pin is null || Pin.Length != 4 || Pin.Any(c => c is < '0' or > '9'))
            throw new ArgumentException("Invalid pairing PIN.");
        if (string.IsNullOrWhiteSpace(DeviceName) || Encoding.UTF8.GetByteCount(DeviceName) > 128 || DeviceName.Any(char.IsControl))
            throw new ArgumentException("Invalid device name.");
    }

    public bool Matches(PendingPairing pending, IPAddress remoteAddress) =>
        pending.Name == PairingName && IPAddress.TryParse(pending.Address, out var address) &&
        address.MapToIPv6().Equals(remoteAddress.MapToIPv6());
}
