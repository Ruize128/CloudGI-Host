using CloudGI.Host.Core.Streaming;
using CloudGI.Host.Core.Configuration;
using CloudGI.Host.Platform;
using Microsoft.UI.Xaml.Controls;
using System.Collections.ObjectModel;

namespace CloudGI.Host.ViewModels;

public sealed class StreamingViewModel(ISunshineService service, IGameSettingsStore settingsStore,
    bool useChildSession, bool useHardwareEncoding, string? settingsError = null, bool isChildHost = false) : StatusViewModel
{
    private string _hostName = "";
    private readonly bool _activeHardwareEncoding = useHardwareEncoding;
    private bool _useHardwareEncoding = useHardwareEncoding;
    private bool _starting;
    private string? _encoder;
    public bool UseHardwareEncoding => _useHardwareEncoding;
    public bool CanChangeEncoding => IsNotBusy && !_starting;
    public string EncodingHint => _useHardwareEncoding != _activeHardwareEncoding
        ? "已保存。重启 Host 后将使用" + (_useHardwareEncoding ? "硬件编码。" : "软件编码。")
        : "主桌面和后台桌面共用此设置，修改后重启 Host 生效。";
    public string EncoderStateText => "实际编码器：" + SunshineEncoding.Describe(_encoder);

    public async Task SetUseHardwareEncodingAsync(bool enabled)
    {
        if (!CanChangeEncoding || enabled == _useHardwareEncoding) return;
        IsBusy = true;
        try
        {
            var saved = await settingsStore.UpdateAsync(latest => latest with { UseHardwareEncoding = enabled });
            _useHardwareEncoding = saved.UseHardwareEncoding;
            NotifyPropertyChanged(nameof(EncodingHint));
            SetStatus("编码方式已保存，重启 Host 后生效。", InfoBarSeverity.Success);
        }
        catch (Exception ex) { ReportError("保存编码方式失败：" + ex.Message); }
        finally { NotifyPropertyChanged(nameof(UseHardwareEncoding)); IsBusy = false; }
    }
    private readonly bool _activeUseChildSession = useChildSession && !isChildHost;
    private bool _useChildSession = useChildSession && !isChildHost;
    public bool UseChildSession => _useChildSession;
    public bool CanChangeRunMode => !isChildHost && IsNotBusy;
    public string RunModeText => isChildHost ? "当前运行：子 Host · 后台桌面内" :
        _activeUseChildSession ? "当前运行：独立后台桌面" : "当前运行：主桌面";
    public string RunModeHint => isChildHost
        ? "此 Host 直接运行游戏和 Sunshine，不创建子会话。运行模式请在主 Host 中修改。"
        : _useChildSession != _activeUseChildSession
        ? "已保存。重启 Host 后将切换到" + (_useChildSession ? "独立后台桌面。" : "主桌面模式。")
        : "修改自动保存，重启 Host 后生效。";

    public async Task SetUseChildSessionAsync(bool enabled)
    {
        if (!CanChangeRunMode || enabled == _useChildSession) return;
        IsBusy = true;
        try
        {
            // Do not validate game paths: the switch also works when a NAS is offline.
            var saved = await settingsStore.UpdateAsync(latest => latest with { UseChildSession = enabled });
            _useChildSession = saved.UseChildSession;
            NotifyPropertyChanged(nameof(RunModeHint));
            SetStatus("运行模式已保存，重启 Host 后生效。", InfoBarSeverity.Success);
        }
        catch (Exception ex) { ReportError("保存运行模式失败：" + ex.Message); }
        finally { NotifyPropertyChanged(nameof(UseChildSession)); IsBusy = false; }
    }
    public Microsoft.UI.Xaml.Visibility ChildSessionVisibility => service is AnnexSessionService
        ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    private double _childWidth = service is AnnexSessionService s1 ? s1.Display.Width : 1920;
    private double _childHeight = service is AnnexSessionService s2 ? s2.Display.Height : 1080;
    private double _childScale = service is AnnexSessionService s3 ? s3.Display.Scale : 100;
    public double ChildWidth { get => _childWidth; set => SetProperty(ref _childWidth, value); }
    public double ChildHeight { get => _childHeight; set => SetProperty(ref _childHeight, value); }
    public double ChildScale { get => _childScale; set => SetProperty(ref _childScale, value); }

    public async Task ApplyChildDisplayAsync()
    {
        if (IsBusy || service is not AnnexSessionService session) return;
        IsBusy = true;
        try
        {
            if (!double.IsFinite(ChildWidth) || !double.IsFinite(ChildHeight) || !double.IsFinite(ChildScale) ||
                ChildWidth != Math.Truncate(ChildWidth) || ChildHeight != Math.Truncate(ChildHeight) || ChildScale != Math.Truncate(ChildScale))
                throw new ArgumentException("分辨率和缩放必须为整数。");
            await session.ConfigureDisplayAsync(new(checked((int)ChildWidth), checked((int)ChildHeight), checked((int)ChildScale)));
            if (session.Snapshot.State == SunshineState.Failed) throw new IOException(session.Snapshot.Error);
            SetStatus("后台桌面已使用新的分辨率重新启动。", InfoBarSeverity.Success);
        }
        catch (Exception ex) { ReportError(ex.Message); }
        finally { RefreshState(); IsBusy = false; }
    }
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
    protected override void OnBusyChanged()
    {
        NotifyPropertyChanged(nameof(CanPair));
        NotifyPropertyChanged(nameof(CanChangeRunMode));
        NotifyPropertyChanged(nameof(CanChangeEncoding));
    }

    private void RefreshState()
    {
        var snapshot = service.Snapshot;
        _starting = snapshot.State == SunshineState.Starting;
        _encoder = snapshot.Encoder;
        NotifyPropertyChanged(nameof(CanChangeEncoding));
        NotifyPropertyChanged(nameof(EncoderStateText));
        Ready = snapshot.State == SunshineState.Ready;
        StateText = snapshot.State switch
        {
            SunshineState.Starting => "串流服务启动中",
            SunshineState.Ready => $"{snapshot.Name} · 可连接",
            SunshineState.Failed => "串流服务启动或连接失败",
            _ => "串流服务已停止"
        };
        Detail = snapshot.Error ?? $"端口 {snapshot.Port} · Sunshine {SunshineConfiguration.Version}"
            + (service is AnnexSessionService session ? " · " + session.SessionDetail : "");
        if (HostName.Length == 0 && snapshot.Name.Length > 0) HostName = snapshot.Name;
    }

    public async Task MonitorAsync(CancellationToken token)
    {
        try
        {
            if (settingsError is not null) { ReportError(settingsError); RefreshState(); return; }
            _firewallReady = await Task.Run(WindowsSunshineFirewall.IsConfigured, token);
            NotifyPropertyChanged(nameof(NetworkText));
            // The parent owns initial child startup over its authenticated pipe.
            // The child window only observes it; a second StartAsync here would race a failed start.
            var starting = isChildHost ? Task.CompletedTask : service.StartAsync(token);
            RefreshState();
            // Keep the startup status responsive while Sunshine probes the display and encoder.
            while (!starting.IsCompleted) { await Task.Delay(250, token); RefreshState(); }
            await starting;
            RefreshState();
            if (Ready && !_firewallReady && !isChildHost) await EnableNetworkAsync();
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
            do
            {
                if (service.Snapshot.State == SunshineState.Stopped && isChildHost)
                {
                    RefreshState();
                    continue;
                }
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
        try { await service.StartAsync(); RefreshState(); if (Ready && !_firewallReady) await EnableNetworkAsync(); }
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
