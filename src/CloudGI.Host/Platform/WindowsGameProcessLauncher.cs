using CloudGI.Host.Core.Games;
using System.ComponentModel;
using System.Diagnostics;

namespace CloudGI.Host.Platform;

public sealed class WindowsGameProcessLauncher : IGameProcessLauncher, IGameProcessController
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

    public GameProcessIdentity Capture(int processId)
    {
        using var process = Process.GetProcessById(processId);
        using var current = Process.GetCurrentProcess();
        if (process.HasExited || process.SessionId != current.SessionId ||
            !GameLaunchValidator.ExecutableNames.Any(name => string.Equals(Path.GetFileNameWithoutExtension(name), process.ProcessName, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("游戏进程身份发生变化，请重新连接。");
        return new(process.Id, process.StartTime.ToUniversalTime().Ticks, process.SessionId);
    }

    public Task StopAsync(GameProcessIdentity identity, CancellationToken token = default) => Task.Run(async () =>
    {
        token.ThrowIfCancellationRequested();
        Process process;
        try { process = Process.GetProcessById(identity.ProcessId); }
        catch (ArgumentException) { return; } // Already exited.
        using (process)
        {
            // Hold a handle while checking identity, so a reused PID cannot target a replacement process.
            _ = process.SafeHandle;
            if (process.HasExited || process.StartTime.ToUniversalTime().Ticks != identity.StartTimeUtcTicks ||
                process.SessionId != identity.SessionId) return;
            if (process.CloseMainWindow())
            {
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); return; }
                catch (TimeoutException) { }
            }
            if (!process.HasExited) process.Kill(entireProcessTree: false);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
    }, token);

    public int Start(GameStartRequest request)
    {
        if (request.TouchscreenMode) return WindowsTouchscreenLauncher.Start(request);
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
