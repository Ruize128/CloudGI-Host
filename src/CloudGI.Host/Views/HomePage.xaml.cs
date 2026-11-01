using CloudGI.Host.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CloudGI.Host.Views;

public sealed partial class HomePage : Page
{
    public MainViewModel ViewModel { get; }
    public StreamingViewModel Streaming { get; }
    public event EventHandler? SettingsRequested;

    public HomePage(MainViewModel viewModel, StreamingViewModel streaming)
    {
        ViewModel = viewModel;
        Streaming = streaming;
        InitializeComponent();
    }

    private async void RestartStreaming_Click(object sender, RoutedEventArgs e) => await Streaming.RestartAsync();

    private async void LaunchGame_Click(object sender, RoutedEventArgs e) => await ViewModel.LaunchAsync();
    private void OpenSettings_Click(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke(this, EventArgs.Empty);
}
