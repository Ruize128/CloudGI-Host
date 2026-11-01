using CloudGI.Host.Core.Streaming;
using CloudGI.Host.Platform;
using Microsoft.UI.Xaml.Controls;
using System.Collections.ObjectModel;

namespace CloudGI.Host.ViewModels;

public sealed class StreamingViewModel(ISunshineService service) : StatusViewModel
{
    private string _hostName = "";
    private string _stateText = "正在准备串流服务";
    private string _detail = "";
    private string _pin = "";
    private bool _ready;
    private bool _firewallReady;
    private PendingPairing? _selectedPairing;
    public string HostName { get => _hostName; set => SetProperty(ref _hostName, value); }
    public string StateText { get => _stateText; private set => SetProperty(ref _stateText, value); }
    public string Detail { get => _detail; private set => SetProperty(ref _detail, value); }
    public string Pin { get => _pin; set => SetProperty(ref _pin, value); }
    public bool Ready { get => _ready; private set { if (SetProperty(ref _ready, value)) NotifyPropertyChanged(nameof(CanPair)); } }
    public bool CanPair => Ready && IsNotBusy && SelectedPairing is not null;
    public string NetworkText => _firewallReady ? "已允许本地子网连接" : "首次使用请允许局域网连接（Windows 会请求授权）。";
    public ObservableCollection<PendingPairing> PendingPairings { get; } = [];
    public PendingPairing? SelectedPairing
    {
        get => _selectedPairing;
        set { if (SetProperty(ref _selectedPairing, value)) NotifyPropertyChanged(nameof(CanPair)); }
    }
    protected override void OnBusyChanged() => NotifyPropertyChanged(nameof(CanPair));

    private void RefreshState()
    {
        var snapshot = service.Snapshot;
        Ready = snapshot.State == SunshineState.Ready;
        StateText = snapshot.State switch
        {
            SunshineState.Starting => "串流服务启动中",
            SunshineState.Ready => $"{snapshot.Name} · 可连接",
            SunshineState.Failed => "串流服务启动或连接失败",
            _ => "串流服务已停止"
        };
        Detail = snapshot.Error ?? $"端口 {snapshot.Port} · Sunshine {SunshineConfiguration.Version}";
        if (HostName.Length == 0 && snapshot.Name.Length > 0) HostName = snapshot.Name;
    }

    public async Task MonitorAsync(CancellationToken token)
    {
        try
        {
            _firewallReady = await Task.Run(WindowsSunshineFirewall.IsConfigured, token);
            NotifyPropertyChanged(nameof(NetworkText));
            var starting = service.StartAsync(token);
            RefreshState();
            // Keep the startup status responsive while Sunshine probes the display and encoder.
            while (!starting.IsCompleted) { await Task.Delay(250, token); RefreshState(); }
            await starting;
            RefreshState();
            if (Ready && !_firewallReady) await EnableNetworkAsync();
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
            do
            {
                var requests = await service.GetPendingPairingsAsync(token);
                RefreshState();
                if (!IsBusy && !PendingPairings.SequenceEqual(requests))
                {
                    var selectedId = SelectedPairing?.Id;
                    PendingPairings.Clear();
                    foreach (var request in requests) PendingPairings.Add(request);
                    SelectedPairing = PendingPairings.FirstOrDefault(p => p.Id == selectedId) ?? PendingPairings.FirstOrDefault();
                }
            } while (await timer.WaitForNextTickAsync(token));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { ReportError("串流状态检查失败：" + ex.Message); RefreshState(); }
    }

    public async Task RestartAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try { await service.StartAsync(); RefreshState(); }
        catch (Exception ex) { ReportError(ex.Message); }
        finally { IsBusy = false; }
    }

    public async Task SaveNameAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            await service.RenameAsync(HostName);
            HostName = service.Snapshot.Name;
            SetStatus("主机名称已保存，串流服务已重新启动。", InfoBarSeverity.Success);
        }
        catch (Exception ex) { ReportError(ex.Message); }
        finally { RefreshState(); IsBusy = false; }
    }

    public async Task PairAsync()
    {
        if (!CanPair || SelectedPairing is not { } request) return;
        IsBusy = true;
        try
        {
            await service.PairAsync(request.Id, Pin, request.Name);
            Pin = "";
            SetStatus("配对成功。现在可以在 Moonlight 中选择 Desktop 连接。", InfoBarSeverity.Success);
        }
        catch (Exception ex) { Pin = ""; ReportError("配对失败：" + ex.Message); }
        finally { IsBusy = false; }
    }

    public async Task EnableNetworkAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            await WindowsSunshineFirewall.EnableAsync();
            _firewallReady = true;
            NotifyPropertyChanged(nameof(NetworkText));
            SetStatus("已允许局域网连接。请在手机 Moonlight 中选择此主机。", InfoBarSeverity.Success);
        }
        catch (Exception ex) { ReportError("局域网设置未完成：" + ex.Message); }
        finally { IsBusy = false; }
    }
}
