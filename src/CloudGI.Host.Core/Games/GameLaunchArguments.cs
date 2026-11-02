using CloudGI.Host.Core.Configuration;
using System.Text;

namespace CloudGI.Host.Core.Games;

/// <summary>Preserves custom argument tokens while applying Host-owned display/input options once.</summary>
public static class GameLaunchArguments
{
    public static string Build(GameLaunchSettings settings)
    {
        var tokens = Tokenize(settings.LaunchArguments);
        var remaining = new List<string>();
        for (var i = 0; i < tokens.Count; i++)
        {
            var name = tokens[i].Value.Split('=', 2)[0];
            var takesValue = name.Equals("-screen-fullscreen", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("-window-mode", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("-is_cloud", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("-platform_type", StringComparison.OrdinalIgnoreCase);
            if (takesValue)
            {
                if (!tokens[i].Value.Contains('=') && i + 1 < tokens.Count && !tokens[i + 1].Value.StartsWith('-')) i++;
                continue;
            }
            if (name.Equals("use_mobile_platform", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("-use_mobile_platform", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("-popupwindow", StringComparison.OrdinalIgnoreCase)) continue;
            remaining.Add(tokens[i].Raw);
        }
        remaining.Add(settings.Fullscreen ? "-screen-fullscreen 1 -window-mode borderless" : "-screen-fullscreen 0");
        // Mobile UI is applied by the Host-owned native launcher, not obsolete cloud-platform arguments.
        return string.Join(" ", remaining);
    }

    private static List<(string Raw, string Value)> Tokenize(string text)
    {
        var tokens = new List<(string, string)>();
        for (var i = 0; i < text.Length;)
        {
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
            if (i == text.Length) break;
            var start = i;
            var quoted = false;
            var value = new StringBuilder();
            while (i < text.Length && (quoted || !char.IsWhiteSpace(text[i])))
            {
                var slashes = 0;
                while (i < text.Length && text[i] == '\\') { slashes++; i++; }
                if (i < text.Length && text[i] == '"')
                {
                    value.Append('\\', slashes / 2);
                    if (slashes % 2 != 0) value.Append('"');
                    else if (quoted && i + 1 < text.Length && text[i + 1] == '"') { value.Append('"'); i++; }
                    else quoted = !quoted;
                    i++;
                }
                else
                {
                    value.Append('\\', slashes);
                    if (i < text.Length && (quoted || !char.IsWhiteSpace(text[i]))) value.Append(text[i++]);
                }
            }
            tokens.Add((text[start..i], value.ToString()));
        }
        return tokens;
    }
}
