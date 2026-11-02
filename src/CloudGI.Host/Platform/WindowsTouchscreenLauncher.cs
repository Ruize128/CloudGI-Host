using CloudGI.Host.Core.Games;
using System.Diagnostics;
using System.Text.Json;

namespace CloudGI.Host.Platform;

/// <summary>Runs the bundled x64 helper for both ARM64 and x64 Host; never loads x64 code into ARM64 Host.</summary>
internal static class WindowsTouchscreenLauncher
{
    public static int Start(GameStartRequest request)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "MobileUI");
        var executable = Path.Combine(directory, "CloudGI.MobileUI.exe");
        if (!File.Exists(executable) || !File.Exists(Path.Combine(directory, "CloudGI.Touchscreen.dll")))
            throw new GameLaunchException("手机 UI 启动组件缺失，请重新构建 Host，或在首页关闭手机 UI 后普通启动。");
        var info = new ProcessStartInfo(executable)
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        // ArgumentList keeps the raw game arguments as a single helper argument without invoking a shell.
        info.ArgumentList.Add("--game");
        info.ArgumentList.Add(request.ExecutablePath);
        info.ArgumentList.Add("--arguments");
        info.ArgumentList.Add(request.Arguments);
        using var process = Process.Start(info) ?? throw new GameLaunchException("Windows 未能创建手机 UI 启动组件。");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(110_000))
        {
            // Before a successful handoff the helper's job owns only its newly created game.
            try { process.Kill(entireProcessTree: false); }
            catch (InvalidOperationException) { }
            process.WaitForExit(5_000);
            throw new GameLaunchException("手机 UI 初始化超时，本次新建游戏已取消。可关闭首页开关后普通启动。");
        }
        var text = output.GetAwaiter().GetResult();
        _ = error.GetAwaiter().GetResult(); // Native diagnostics contain numeric stage codes only.
        if (process.ExitCode != 0)
            throw new GameLaunchException(process.ExitCode switch
            {
                3 => "手机 UI 组件需要 x64 原神程序，请检查游戏文件。",
                4 => "手机 UI 模块文件缺失，请重新构建 Host。",
                6 => "Windows 未能创建游戏进程，请检查游戏路径和访问权限。",
                8 => "手机 UI 模块未能加载，本次新建游戏已取消。",
                9 => "当前游戏版本未匹配到触屏切换函数，本次新建游戏已取消。可关闭首页开关后普通启动。",
                10 => "手机 UI 初始化失败，本次新建游戏已取消。",
                14 => "手机 UI 函数扫描超时，本次新建游戏已取消。",
                15 => "手机 UI 初始化线程未能就绪，本次新建游戏已取消。",
                16 => "手机 UI 挂钩安装未能就绪，本次新建游戏已取消。",
                >= 20 and <= 32 => $"手机 UI 挂钩创建失败（内部代码 {process.ExitCode - 20}），本次新建游戏已取消。",
                >= 40 and <= 52 => $"手机 UI 挂钩启用失败（内部代码 {process.ExitCode - 40}），本次新建游戏已取消。",
                >= 60 and <= 72 => $"手机 UI 挂钩初始化失败（内部代码 {process.ExitCode - 60}），本次新建游戏已取消。",
                _ => $"手机 UI 启动失败（阶段 {process.ExitCode}），本次新建游戏已取消。"
            });
        try
        {
            using var result = JsonDocument.Parse(text);
            var pid = result.RootElement.GetProperty("processId").GetInt32();
            if (pid <= 0 || !result.RootElement.GetProperty("touchscreenPrepared").GetBoolean())
                throw new InvalidDataException();
            // Hook readiness is confirmed; actual UI switching occurs later in the game callback.
            // This is the game PID, not the short-lived helper's PID. Session/lease ownership remains Host-owned.
            return pid;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or InvalidDataException)
        {
            throw new GameLaunchException("手机 UI 启动组件返回了无效结果，请检查组件版本。");
        }
    }
}
