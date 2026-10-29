using CloudGI.Host.Core.Configuration;
using CloudGI.Host.Core.Games;
using CloudGI.Host.ViewModels;
using Microsoft.UI.Windowing;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;

namespace CloudGI.Host.Views;

public sealed partial class MainWindow : Window
{
    private readonly HomePage _homePage;
    private readonly SettingsPage _settingsPage;
    private bool _initialized;

    public MainWindow(IGameSettingsStore settingsStore, GameLaunchValidator validator, IGameLaunchService launchService)
    {
        InitializeComponent();
        _homePage = new HomePage(new MainViewModel(settingsStore, validator, launchService));
        _settingsPage = new SettingsPage(new SettingsViewModel(settingsStore, validator), AppWindow.Id);
        _homePage.SettingsRequested += (_, _) => Navigation.SelectedItem = Navigation.SettingsItem;
        ContentFrame.Content = _homePage;
        Navigation.SelectedItem = Navigation.MenuItems[0];

        // AppWindow uses physical pixels; size the initial window in logical pixels.
        var scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        if (scale <= 0) scale = 1;
        var width = (int)Math.Round(1000 * scale);
        var height = (int)Math.Round(760 * scale);
        var display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        if (display is not null)
        {
            var area = display.WorkArea;
            width = Math.Min(width, area.Width);
            height = Math.Min(height, area.Height);
            AppWindow.Resize(new SizeInt32(width, height));
            AppWindow.Move(new PointInt32(
                area.X + Math.Max(0, (area.Width - width) / 2),
                area.Y + Math.Max(0, (area.Height - height) / 2)));
        }
        else AppWindow.Resize(new SizeInt32(width, height));
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint window);

    private async void RootGrid_Loaded(object sender, RoutedEventArgs e)
    {
        if (_initialized) return;
        _initialized = true;
        await _settingsPage.ViewModel.LoadAsync();
        await _homePage.ViewModel.RefreshAsync();
    }

    private async void Navigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_homePage is null || _settingsPage is null) return;
        ContentFrame.Content = args.IsSettingsSelected ? _settingsPage : _homePage;
        if (!args.IsSettingsSelected) await _homePage.ViewModel.RefreshAsync();
    }
}
