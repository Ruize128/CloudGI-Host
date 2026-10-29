using CloudGI.Host.Core.Configuration;

namespace CloudGI.Host.Core.Games;

public sealed class GameConfigurationException(string message) : Exception(message);

public sealed class GameLaunchValidator
{
    public static IReadOnlyList<string> ExecutableNames { get; } =
        Array.AsReadOnly(new[] { "YuanShen.exe", "GenshinImpact.exe" });

    public GameLaunchSettings Normalize(GameLaunchSettings settings)
    {
        var input = settings.GameExecutablePath?.Trim() ?? "";
        if (input.StartsWith('"') && input.EndsWith('"') && input.Length >= 2)
            input = input[1..^1].Trim();
        if (string.IsNullOrWhiteSpace(input))
            throw new GameConfigurationException("请先选择游戏目录或游戏可执行文件。");
        if (!Path.IsPathFullyQualified(input))
            throw new GameConfigurationException("请选择游戏文件的完整路径。");

        string executable;
        try { executable = Path.GetFullPath(input); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new GameConfigurationException("游戏路径格式不正确。");
        }

        if (Directory.Exists(executable))
        {
            var candidates = ExecutableNames.Select(name => Path.Combine(executable, name))
                .Where(File.Exists).ToArray();
            if (candidates.Length == 0)
                throw new GameConfigurationException("此目录中未找到 YuanShen.exe 或 GenshinImpact.exe。");
            if (candidates.Length > 1)
                throw new GameConfigurationException("此目录有多个游戏文件，请直接选择要启动的可执行文件。");
            executable = candidates[0];
        }

        if (!ExecutableNames.Contains(Path.GetFileName(executable), StringComparer.OrdinalIgnoreCase))
            throw new GameConfigurationException("请选择 YuanShen.exe 或 GenshinImpact.exe，而不是官方启动器。");
        if (!File.Exists(executable))
            throw new GameConfigurationException("游戏文件不存在或当前无法访问，请检查路径和磁盘连接。");

        var arguments = settings.LaunchArguments?.Trim() ?? "";
        if (arguments.Length > 8192 || arguments.Any(char.IsControl))
            throw new GameConfigurationException("启动参数须为不超过 8192 个字符的单行文本。");

        return settings with { GameExecutablePath = executable, LaunchArguments = arguments };
    }
}
