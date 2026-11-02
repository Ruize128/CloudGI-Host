using CloudGI.Host.Core.Configuration;
using CloudGI.Host.Core.Games;
using Microsoft.UI.Xaml.Controls;

namespace CloudGI.Host.ViewModels;

public sealed class SettingsViewModel(IGameSettingsStore settingsStore, GameLaunchValidator validator) : StatusViewModel
{
    private string _gamePath = "";
    private string _launchArguments = "";
    private bool _fullscreen = true;
    public bool Fullscreen { get => _fullscreen; set => SetProperty(ref _fullscreen, value); }

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
            Fullscreen = settings.Fullscreen;
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
            var draft = new GameLaunchSettings
            {
                GameExecutablePath = GamePath,
                LaunchArguments = LaunchArguments,
                Fullscreen = Fullscreen
            };
            var settings = await Task.Run(() => validator.Normalize(draft));
            await settingsStore.UpdateAsync(latest => latest with
            {
                GameExecutablePath = settings.GameExecutablePath,
                LaunchArguments = settings.LaunchArguments,
                Fullscreen = settings.Fullscreen
            });
            GamePath = settings.GameExecutablePath;
            LaunchArguments = settings.LaunchArguments;
            Fullscreen = settings.Fullscreen;
            SetStatus("已保存。下次启动游戏时将使用这些设置。", InfoBarSeverity.Success);
        }
        catch (Exception ex) { ReportError("保存失败：" + ex.Message); }
        finally { IsBusy = false; }
    }
}
