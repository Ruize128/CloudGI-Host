using System.Text.RegularExpressions;

namespace CloudGI.Host.Core.Streaming;

/// <summary>Reads the actual H.264 selection reported by the pinned Sunshine build, not attempted encoders.</summary>
public static class SunshineEncoding
{
    public static string? ParseSelectedEncoder(string log)
    {
        var matches = Regex.Matches(log, @"Found H\.264 encoder: [^\r\n]* \[([a-z0-9_-]+)\]", RegexOptions.CultureInvariant);
        return matches.Count == 0 ? null : matches[^1].Groups[1].Value;
    }

    public static bool MatchesSelection(string encoder, bool useHardwareEncoding) => useHardwareEncoding
        ? encoder is "nvenc" or "quicksync" or "amdvce" or "mediafoundation"
        : encoder == "software";

    public static string Describe(string? encoder) => encoder switch
    {
        "software" => "软件编码（H.264 / libx264）",
        "nvenc" => "硬件编码（NVIDIA NVENC）",
        "quicksync" => "硬件编码（Intel Quick Sync）",
        "amdvce" => "硬件编码（AMD AMF）",
        "mediafoundation" => "硬件编码（Media Foundation）",
        _ => "尚未就绪"
    };
}
