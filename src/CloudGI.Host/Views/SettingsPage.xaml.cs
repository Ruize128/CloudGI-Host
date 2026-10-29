using CloudGI.Host.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;

namespace CloudGI.Host.Views;

public sealed partial class SettingsPage : Page
{
    private readonly WindowId _ownerWindow;
    public SettingsViewModel ViewModel { get; }

    public SettingsPage(SettingsViewModel viewModel, WindowId ownerWindow)
    {
        ViewModel = viewModel;
        _ownerWindow = ownerWindow;
        InitializeComponent();
    }

    private async void BrowseFile_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker(_ownerWindow) { FileTypeFilter = { ".exe" } };
            var result = await picker.PickSingleFileAsync();
            if (result is not null) ViewModel.GamePath = result.Path;
        }
        catch (Exception ex) { ViewModel.ReportError("无法打开文件选择器：" + ex.Message); }
    }

    private async void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var result = await new FolderPicker(_ownerWindow).PickSingleFolderAsync();
            if (result is not null) ViewModel.GamePath = result.Path;
        }
        catch (Exception ex) { ViewModel.ReportError("无法打开目录选择器：" + ex.Message); }
    }

    private async void Save_Click(object sender, RoutedEventArgs e) => await ViewModel.SaveAsync();
}
