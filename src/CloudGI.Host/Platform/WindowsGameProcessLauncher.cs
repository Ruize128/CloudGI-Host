using CloudGI.Host.Core.Games;
using System.ComponentModel;
using System.Diagnostics;

namespace CloudGI.Host.Platform;

public sealed class WindowsGameProcessLauncher : IGameProcessLauncher
{
    public int? FindRunningGame(string executablePath)
    {
        using var current = Process.GetCurrentProcess();
        foreach (var name in GameLaunchValidator.ExecutableNames)
        {
            foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(name)))
            {
                using (process)
                {
                    try
                    {
                        if (!process.HasExited && process.SessionId == current.SessionId)
                            return process.Id;
                    }
                    catch (InvalidOperationException) { }
                    catch (Win32Exception) { }
                }
            }
        }
        return null;
    }

    public int Start(GameStartRequest request)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = request.ExecutablePath,
            Arguments = request.Arguments,
            WorkingDirectory = request.WorkingDirectory,
            UseShellExecute = true
        }) ?? throw new InvalidOperationException("Windows 未能创建游戏进程。");
        return process.Id;
    }
}
