using CloudGI.Host.Core.Games;
using System.ComponentModel;
using System.Diagnostics;

namespace CloudGI.Host.Platform;

public sealed class WindowsGameProcessLauncher : IGameProcessLauncher
{
    public int? FindRunningGame()
    {
        using var current = Process.GetCurrentProcess();
        Win32Exception? detectionError = null;
        foreach (var name in GameLaunchValidator.ExecutableNames)
        {
            var processes = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(name));
            try
            {
                foreach (var process in processes)
                {
                    try
                    {
                        if (process.SessionId == current.SessionId && !process.HasExited)
                            return process.Id;
                    }
                    catch (InvalidOperationException) { } // Exited during enumeration; retry next poll.
                    catch (Win32Exception ex) { detectionError = ex; }
                }
            }
            finally
            {
                // Dispose every wrapper, including those after an early match.
                foreach (var process in processes) process.Dispose();
            }
        }
        if (detectionError is not null) throw detectionError;
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
