using CloudGI.Host.Core.Configuration;
using CloudGI.Host.Core.Games;
using CloudGI.Host.Platform;
using CloudGI.Host.Views;
using Microsoft.UI.Xaml;

namespace CloudGI.Host;

public partial class App : Application
{
    private Window? _window;
    private Mutex? _primaryInstance;

    public App() => InitializeComponent();

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, EntryPoint = "MessageBoxW")]
    private static extern int ShowMessage(nint owner, string message, string title, uint flags);

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (Environment.GetCommandLineArgs().Contains("--configure-streaming-firewall"))
        {
            try { WindowsSunshineFirewall.Configure(); Environment.Exit(0); }
            catch { Environment.Exit(1); }
            return;
        }
        var configurationPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CloudGI", "Host", "settings.json");
        var commandLine = Environment.GetCommandLineArgs();
        if (commandLine.Length > 1 && commandLine[1] == "--child-session-agent")
        {
            try
            {
                var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
                await ChildSessionAgent.RunAsync(commandLine.Skip(2).ToArray(), configurationPath, context =>
                {
                    var shown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    if (!dispatcher.TryEnqueue(() =>
                    {
                        try
                        {
                            _window = new MainWindow(context.Settings, new GameLaunchValidator(), context.Games,
                                context.Games, context.Sunshine, false, context.UseHardwareEncoding, isChildHost: true);
                            _window.Closed += (_, _) => context.Stop();
                            _window.Activate();
                            shown.SetResult();
                        }
                        catch (Exception ex) { shown.SetException(ex); }
                    })) shown.SetException(new InvalidOperationException("无法打开子 Host 窗口。"));
                    return shown.Task;
                });
            }
            catch (Exception ex) when (ex is EndOfStreamException or OperationCanceledException) { }
            catch (Exception ex)
            {
                var directory = Path.Combine(Path.GetDirectoryName(configurationPath)!, "Annex");
                Directory.CreateDirectory(directory);
                await File.WriteAllTextAsync(Path.Combine(directory, "agent-error.txt"), ex.ToString());
            }
            Exit();
            return;
        }
        // A normal launch inside the child must never consume the parent's saved run mode.
        // The managed child is started with --child-session-agent and owns its visible UI.
        if (WindowsHostSession.IsManagedChildSession)
        {
            ShowMessage(0, "此后台桌面中的 CloudGI Host 由主 Host 自动启动和管理。请使用已打开的子 Host；启动失败时请在主 Host 中重试。",
                "CloudGI Host · 后台桌面", 0x40);
            Exit();
            return;
        }
        // The agent/firewall helpers above bypass this foreground-window singleton.
        var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
        _primaryInstance = new Mutex(false, @"Local\CloudGI.Host.Foreground." + sid, out var created);
        if (!created)
        {
            ShowMessage(0, "CloudGI Host 已在运行。切换运行模式时，请先关闭原来的 Host，再重新启动。", "CloudGI Host", 0x40);
            _primaryInstance.Dispose();
            Exit();
            return;
        }
        var store = new JsonGameSettingsStore(configurationPath);
        var validator = new GameLaunchValidator();
        GameLaunchSettings settings;
        string? settingsError = null;
        try { settings = await store.LoadAsync(); }
        catch (Exception ex)
        {
            // Keep the settings UI available for recovery; never silently capture the main desktop.
            settings = new GameLaunchSettings();
            settingsError = "无法读取运行模式，请先在设置中修复配置：" + ex.Message;
        }
        CloudGI.Host.Core.Streaming.ISunshineService sunshine;
        IGameLaunchService launcher;
        IGameStatusService status;
        if (settings.UseChildSession)
        {
            var session = new AnnexSessionService(Path.Combine(Path.GetDirectoryName(configurationPath)!, "Annex"), settings.UseHardwareEncoding);
            sunshine = session; launcher = session; status = session;
        }
        else
        {
            var processes = new WindowsGameProcessLauncher();
            var games = new GameLaunchService(store, validator, processes);
            launcher = games; status = games;
            sunshine = new WindowsSunshineService(Path.Combine(Path.GetDirectoryName(configurationPath)!, "Sunshine"), games, games, processes, useHardwareEncoding: settings.UseHardwareEncoding);
        }
        _window = new MainWindow(store, validator, launcher, status, sunshine, settings.UseChildSession, settings.UseHardwareEncoding, settingsError);
        _window.Closed += (_, _) => _primaryInstance?.Dispose();
        _window.Activate();
    }
}
