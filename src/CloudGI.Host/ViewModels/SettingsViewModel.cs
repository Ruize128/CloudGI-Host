using CloudGI.Host.Core.Configuration;
using CloudGI.Host.Core.Games;
using Microsoft.UI.Xaml.Controls;

namespace CloudGI.Host.ViewModels;

public sealed class SettingsViewModel(IGameSettingsStore settingsStore, GameLaunchValidator validator) : StatusViewModel
{
    private string _gamePath = "";
    private string _launchArguments = "";

    public string GamePath
    {
        get => _gamePath;
        set => SetProperty(ref _gamePath, value);
    }

    public string LaunchArguments
    {
        get => _launchArguments;
        set => SetProperty(ref _launchArguments, value);
    }

    public async Task LoadAsync()
    {
        IsBusy = true;
        try
        {
            var settings = await settingsStore.LoadAsync();
            GamePath = settings.GameExecutablePath;
            LaunchArguments = settings.LaunchArguments;
        }
        catch (Exception ex) { ReportError(ex.Message); }
        finally { IsBusy = false; }
    }

    public async Task SaveAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            var settings = validator.Normalize(new GameLaunchSettings
            {
                GameExecutablePath = GamePath,
                LaunchArguments = LaunchArguments
            });
            await settingsStore.SaveAsync(settings);
            GamePath = settings.GameExecutablePath;
            LaunchArguments = settings.LaunchArguments;
            SetStatus("已保存。下次启动游戏时将使用这些设置。", InfoBarSeverity.Success);
        }
        catch (Exception ex) { ReportError("保存失败：" + ex.Message); }
        finally { IsBusy = false; }
    }
}
