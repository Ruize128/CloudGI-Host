using CloudGI.Host.Core.Configuration;
using CloudGI.Host.Core.Games;
using Microsoft.UI.Xaml.Controls;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;

namespace CloudGI.Host.ViewModels;

public sealed class MainViewModel(
    IGameSettingsStore settingsStore,
    GameLaunchValidator validator,
    IGameLaunchService launchService) : StatusViewModel
{
    private string _gamePath = "尚未设置游戏路径";
    private string _arguments = "未设置（使用游戏默认值）";
    private bool _hasConfiguration;

    public string Version => Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.1.0";
    public string Platform => $"{RuntimeInformation.OSDescription} · {RuntimeInformation.ProcessArchitecture}";
    public string Runtime => RuntimeInformation.FrameworkDescription;
    public string GamePath => _gamePath;
    public string Arguments => _arguments;
    public bool CanLaunch => _hasConfiguration && !IsBusy;

    public async Task RefreshAsync()
    {
        if (IsBusy) return;
        _hasConfiguration = false;
        IsBusy = true;
        try
        {
            var saved = await settingsStore.LoadAsync();
            _hasConfiguration = false;
            _gamePath = string.IsNullOrWhiteSpace(saved.GameExecutablePath) ? "尚未设置游戏路径" : saved.GameExecutablePath;
            _arguments = string.IsNullOrWhiteSpace(saved.LaunchArguments) ? "未设置（使用游戏默认值）" : saved.LaunchArguments;
            if (string.IsNullOrWhiteSpace(saved.GameExecutablePath))
                SetStatus("请先前往设置，选择游戏文件并保存。");
            else
            {
                validator.Normalize(saved);
                _hasConfiguration = true;
                SetStatus("");
            }
        }
        catch (Exception ex) { ReportError(ex.Message); }
        finally
        {
            NotifyPropertyChanged(nameof(GamePath));
            NotifyPropertyChanged(nameof(Arguments));
            IsBusy = false;
        }
    }

    public async Task LaunchAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        SetStatus("正在请求 Windows 启动游戏…如弹出系统确认窗口，请在主机上处理。");
        try
        {
            var result = await launchService.LaunchAsync();
            SetStatus(result.Status == GameLaunchStatus.AlreadyRunning
                ? $"游戏已在运行（PID {result.ProcessId}），未重复启动。新参数将在下次启动时生效。"
                : $"已发送游戏启动请求（PID {result.ProcessId}）。登录操作请在游戏窗口中完成。",
                result.Status == GameLaunchStatus.AlreadyRunning ? InfoBarSeverity.Informational : InfoBarSeverity.Success);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            SetStatus("已取消 Windows 的启动授权，游戏未启动。", InfoBarSeverity.Warning);
        }
        catch (Exception ex) { ReportError("启动失败：" + ex.Message); }
        finally { IsBusy = false; }
    }

    protected override void OnBusyChanged() => NotifyPropertyChanged(nameof(CanLaunch));
}
