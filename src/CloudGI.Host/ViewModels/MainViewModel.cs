using CloudGI.Host.Core.Configuration;
using CloudGI.Host.Core.Games;
using Microsoft.UI.Xaml.Controls;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;

namespace CloudGI.Host.ViewModels;

public sealed class MainViewModel(
    IGameSettingsStore settingsStore,
    GameLaunchValidator validator,
    IGameLaunchService launchService,
    IGameStatusService statusService) : StatusViewModel
{
    private string _gamePath = "尚未设置游戏路径";
    private string _arguments = "未设置（使用游戏默认值）";
    private bool _hasConfiguration;
    private bool _isLaunching;
    private bool _isMonitoring;
    private bool _showingLaunchResult;
    private GameRuntimeSnapshot _gameState = new(GameRuntimeState.Unknown);
    private readonly SemaphoreSlim _statusRefreshGate = new(1, 1);

    public string Version => Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.1.0";
    public string Platform => $"{RuntimeInformation.OSDescription} · {RuntimeInformation.ProcessArchitecture}";
    public string Runtime => RuntimeInformation.FrameworkDescription;
    public string GamePath => _gamePath;
    public string Arguments => _arguments;
    public bool CanLaunch => _hasConfiguration && !IsBusy && _gameState.State == GameRuntimeState.NotRunning;
    public string LaunchButtonText => _gameState.State == GameRuntimeState.Running ? "游戏已运行" : "启动游戏";
    public string GameStateText => DisplayState switch
    {
        GameRuntimeState.NotRunning => "未运行",
        GameRuntimeState.Starting => "启动中",
        GameRuntimeState.Running => "运行中",
        _ => _gameState.Error is null ? "检查中" : "检测失败"
    };
    public string GameStateDetail => DisplayState switch
    {
        GameRuntimeState.Running => $"PID {_gameState.ProcessId} · 新参数将在下次启动时生效。",
        GameRuntimeState.Starting => "正在处理启动请求，请留意 Windows 的确认窗口。",
        GameRuntimeState.NotRunning => "尚未发现当前 Windows 会话中的游戏进程。",
        _ => _gameState.Error is null ? "正在检查游戏进程…" : "无法确认游戏状态：" + _gameState.Error
    };
    private GameRuntimeState DisplayState => _isLaunching && _gameState.State != GameRuntimeState.Running
        ? GameRuntimeState.Starting : _gameState.State;

    public async Task MonitorGameStatusAsync(CancellationToken cancellationToken)
    {
        if (_isMonitoring) return;
        _isMonitoring = true;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            do { await RefreshGameStatusAsync(cancellationToken); }
            while (await timer.WaitForNextTickAsync(cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally { _isMonitoring = false; }
    }

    private async Task RefreshGameStatusAsync(CancellationToken cancellationToken = default)
    {
        await _statusRefreshGate.WaitAsync(cancellationToken);
        try
        {
            var snapshot = await statusService.GetStatusAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            ApplyGameState(snapshot);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ApplyGameState(new(GameRuntimeState.Unknown, Error: ex.Message));
        }
        finally { _statusRefreshGate.Release(); }
    }

    private void ApplyGameState(GameRuntimeSnapshot snapshot)
    {
        if (_gameState == snapshot) return;
        if (_gameState.State == GameRuntimeState.Running && snapshot.State == GameRuntimeState.NotRunning
            && _showingLaunchResult)
        {
            SetStatus("");
            _showingLaunchResult = false;
        }
        _gameState = snapshot;
        NotifyGameStateChanged();
    }

    private void NotifyGameStateChanged()
    {
        NotifyPropertyChanged(nameof(GameStateText));
        NotifyPropertyChanged(nameof(GameStateDetail));
        NotifyPropertyChanged(nameof(LaunchButtonText));
        NotifyPropertyChanged(nameof(CanLaunch));
    }

    public async Task RefreshAsync()
    {
        if (IsBusy) return;
        _hasConfiguration = false;
        _showingLaunchResult = false;
        IsBusy = true;
        try
        {
            var saved = await settingsStore.LoadAsync();
            _hasConfiguration = false;
            _gamePath = string.IsNullOrWhiteSpace(saved.GameExecutablePath) ? "尚未设置游戏路径" : saved.GameExecutablePath;
            _arguments = string.IsNullOrWhiteSpace(saved.LaunchArguments) ? "未设置（使用游戏默认值）" : saved.LaunchArguments;
            if (string.IsNullOrWhiteSpace(saved.GameExecutablePath))
                SetStatus("请先前往设置，选择游戏文件并保存。");
            else
            {
                await Task.Run(() => validator.Normalize(saved));
                _hasConfiguration = true;
                SetStatus("");
            }
        }
        catch (Exception ex) { ReportError(ex.Message); }
        finally
        {
            NotifyPropertyChanged(nameof(GamePath));
            NotifyPropertyChanged(nameof(Arguments));
            IsBusy = false;
        }
    }

    public async Task LaunchAsync()
    {
        if (!CanLaunch) return;
        IsBusy = true;
        _isLaunching = true;
        _showingLaunchResult = false;
        NotifyGameStateChanged();
        SetStatus("正在请求 Windows 启动游戏…如弹出系统确认窗口，请在主机上处理。");
        try
        {
            var result = await launchService.LaunchAsync();
            _showingLaunchResult = true;
            SetStatus(result.Status == GameLaunchStatus.AlreadyRunning
                ? $"游戏已在运行（PID {result.ProcessId}），未重复启动。新参数将在下次启动时生效。"
                : $"已发送游戏启动请求（PID {result.ProcessId}）。登录操作请在游戏窗口中完成。",
                result.Status == GameLaunchStatus.AlreadyRunning ? InfoBarSeverity.Informational : InfoBarSeverity.Success);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            SetStatus("已取消 Windows 的启动授权，游戏未启动。", InfoBarSeverity.Warning);
        }
        catch (Exception ex) { ReportError("启动失败：" + ex.Message); }
        finally
        {
            await RefreshGameStatusAsync();
            _isLaunching = false;
            IsBusy = false;
            NotifyGameStateChanged();
        }
    }

    protected override void OnBusyChanged() => NotifyPropertyChanged(nameof(CanLaunch));
}
