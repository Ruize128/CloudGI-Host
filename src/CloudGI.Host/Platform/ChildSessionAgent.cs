using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using CloudGI.Host.Core.Configuration;
using CloudGI.Host.Core.Games;
using CloudGI.Host.Core.Streaming;
using Microsoft.Win32.SafeHandles;

namespace CloudGI.Host.Platform;

internal sealed record ChildHostContext(IGameSettingsStore Settings, GameLaunchService Games,
    ISunshineService Sunshine, bool UseHardwareEncoding, Action Stop);

internal static class ChildSessionAgent
{
    public static async Task RunAsync(string[] arguments, string configurationPath, Func<ChildHostContext, Task> showWindow)
    {
        if (arguments.Length != 4 || !arguments[0].StartsWith("CloudGI.Annex.", StringComparison.Ordinal))
            throw new ArgumentException("子会话代理参数无效。");
        using var parent = Process.GetProcessById(int.Parse(arguments[1]));
        using var current = Process.GetCurrentProcess();
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator) ||
            current.SessionId == 0 || current.SessionId == parent.SessionId || current.SessionId == WTSGetActiveConsoleSessionId() ||
            parent.HasExited || parent.StartTime.ToUniversalTime().Ticks != long.Parse(arguments[2]) ||
            !string.Equals(parent.MainModule?.FileName, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("代理必须在专属子会话中，以同一 Host 管理员账户启动。");
        using var childInstance = WindowsHostSession.ClaimChildSession();
        using var lifetime = new CancellationTokenSource();
        var watcher = WatchParentAsync(parent, lifetime);
        using var pipe = new NamedPipeClientStream(".", arguments[0], PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.ConnectAsync(30000, lifetime.Token).ConfigureAwait(false);
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var serverPid) || serverPid != parent.Id)
                throw new IOException("本机控制管道不属于指定的 Host。");
            // Delete only the exact temporary launch task once its action is running.
            if (!arguments[3].StartsWith("CloudGI-Annex-", StringComparison.Ordinal) ||
                !Guid.TryParseExact(arguments[3]["CloudGI-Annex-".Length..], "N", out _))
                throw new ArgumentException("临时任务标识无效。");
            try
            {
                dynamic scheduler = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
                scheduler.Connect(); scheduler.GetFolder(@"\").DeleteTask(arguments[3], 0);
            }
            catch (COMException) { /* Viewer cleanup retries deletion. */ }
            var store = new ChildHostSettingsStore(new JsonGameSettingsStore(configurationPath));
            var processes = new WindowsGameProcessLauncher();
            var games = new GameLaunchService(store, new GameLaunchValidator(), processes);
            var directory = Path.Combine(Path.GetDirectoryName(configurationPath)!, "Annex", "Sunshine");
            if (!File.Exists(Path.Combine(directory, "instance.json")))
                await new SunshineConfiguration(directory).SaveAsync(new("CloudGI - " + Environment.MachineName + " [Annex]"), lifetime.Token).ConfigureAwait(false);
            // The foreground Host sends its startup choice over the authenticated local pipe.
            // Later UI changes remain pending until Host restarts, even if the child reconnects.
            var initial = await ChildSessionProtocol.ReadAsync<ChildSessionRequest>(pipe, lifetime.Token).ConfigureAwait(false);
            if (initial.Command == "shutdown")
            {
                await ChildSessionProtocol.WriteAsync(pipe, new ChildSessionReply(true,
                    new(SunshineState.Stopped, "", 48989)), lifetime.Token).ConfigureAwait(false);
                return;
            }
            if (initial.Command != "start" || initial.Value is not ("hardware" or "software"))
                throw new InvalidDataException("子 Host 缺少有效的启动编码选项。");
            ChildSessionRequest? first = initial;
            using var sunshine = new WindowsSunshineService(directory, games, games, processes,
                childSession: true, useHardwareEncoding: initial.Value == "hardware");
            // This is the child Host itself: show its UI over the same game/Sunshine services.
            // Its run mode is fixed locally; never load UseChildSession to select another backend.
            await showWindow(new(store, games, sunshine, initial.Value == "hardware", () => lifetime.Cancel())).ConfigureAwait(false);
            while (!lifetime.IsCancellationRequested)
            {
                var request = first ?? await ChildSessionProtocol.ReadAsync<ChildSessionRequest>(pipe, lifetime.Token).ConfigureAwait(false);
                first = null;
                ChildSessionReply reply;
                var shutdown = request.Command == "shutdown";
                try
                {
                    GameLaunchResult? launch = null;
                    PendingPairing[]? pending = null;
                    GameRuntimeSnapshot? game = null;
                    switch (request.Command)
                    {
                        case "start": await sunshine.StartAsync(lifetime.Token).ConfigureAwait(false); break;
                        case "status": game = await games.GetStatusAsync(lifetime.Token).ConfigureAwait(false); break;
                        case "launch":
                            if (sunshine.Snapshot.State != SunshineState.Ready) throw new IOException("后台串流尚未就绪。");
                            launch = await games.LaunchAsync(lifetime.Token).ConfigureAwait(false); break;
                        case "rename": await sunshine.RenameAsync(request.Value ?? "", lifetime.Token).ConfigureAwait(false); break;
                        case "pending": pending = (await sunshine.GetPendingPairingsAsync(lifetime.Token).ConfigureAwait(false)).ToArray(); break;
                        case "pair": await sunshine.PairAsync(request.Value ?? "", request.Pin ?? "", request.Name ?? "", lifetime.Token).ConfigureAwait(false); break;
                        case "shutdown": sunshine.Dispose(); break;
                        default: throw new InvalidDataException("不支持的本机子会话命令。");
                    }
                    reply = new(sunshine.Snapshot.State != SunshineState.Failed, sunshine.Snapshot, game, launch, pending, sunshine.Snapshot.Error);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                { reply = new(false, sunshine.Snapshot, Error: ex.Message); }
                await ChildSessionProtocol.WriteAsync(pipe, reply, lifetime.Token).ConfigureAwait(false);
                if (shutdown) break;
            }
        }
        finally { lifetime.Cancel(); await watcher.ConfigureAwait(false); }
    }

    private static async Task WatchParentAsync(Process parent, CancellationTokenSource lifetime)
    {
        try { await parent.WaitForExitAsync(lifetime.Token).ConfigureAwait(false); lifetime.Cancel(); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("kernel32.dll")] private static extern uint WTSGetActiveConsoleSessionId();
}
