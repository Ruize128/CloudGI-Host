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
        if (Environment.GetCommandLineArgs().Contains("--configure-streaming-firewall"))
        {
            try { WindowsSunshineFirewall.Configure(); Environment.Exit(0); }
            catch { Environment.Exit(1); }
            return;
        }
        var configurationPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CloudGI", "Host", "settings.json");
        var store = new JsonGameSettingsStore(configurationPath);
        var validator = new GameLaunchValidator();
        // UI and remote requests share launch serialization and process detection.
        var processes = new WindowsGameProcessLauncher();
        var launcher = new GameLaunchService(store, validator, processes);
        var sunshine = new WindowsSunshineService(Path.Combine(Path.GetDirectoryName(configurationPath)!, "Sunshine"), launcher, launcher, processes);
        _window = new MainWindow(store, validator, launcher, launcher, sunshine);
        _window.Activate();
    }
}
