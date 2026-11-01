using CloudGI.Host.Core.Configuration;
using CloudGI.Host.Core.Games;
using CloudGI.Host.Platform;
using CloudGI.Host.Views;
using Microsoft.UI.Xaml;

namespace CloudGI.Host;

public partial class App : Application
{
    private Window? _window;

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var configurationPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CloudGI", "Host", "settings.json");
        var store = new JsonGameSettingsStore(configurationPath);
        var validator = new GameLaunchValidator();
        // The same service instance can later be injected into Host's control API.
        var launcher = new GameLaunchService(store, validator, new WindowsGameProcessLauncher());
        _window = new MainWindow(store, validator, launcher, launcher);
        _window.Activate();
    }
}
