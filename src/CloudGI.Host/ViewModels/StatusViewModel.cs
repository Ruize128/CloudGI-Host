using Microsoft.UI.Xaml.Controls;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace CloudGI.Host.ViewModels;

public abstract class StatusViewModel : INotifyPropertyChanged
{
    private bool _isBusy;
    private string _statusMessage = "";
    private InfoBarSeverity _statusSeverity = InfoBarSeverity.Informational;

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsBusy
    {
        get => _isBusy;
        protected set
        {
            if (SetProperty(ref _isBusy, value))
            {
                NotifyPropertyChanged(nameof(IsNotBusy));
                OnBusyChanged();
            }
        }
    }

    public bool IsNotBusy => !IsBusy;
    public string StatusMessage => _statusMessage;
    public InfoBarSeverity StatusSeverity => _statusSeverity;
    public bool IsStatusOpen => _statusMessage.Length > 0;

    public void ReportError(string message) => SetStatus(message, InfoBarSeverity.Error);

    protected void SetStatus(string message, InfoBarSeverity severity = InfoBarSeverity.Informational)
    {
        _statusMessage = message;
        _statusSeverity = severity;
        NotifyPropertyChanged(nameof(StatusMessage));
        NotifyPropertyChanged(nameof(StatusSeverity));
        NotifyPropertyChanged(nameof(IsStatusOpen));
    }

    protected virtual void OnBusyChanged() { }

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        NotifyPropertyChanged(name);
        return true;
    }

    protected void NotifyPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
