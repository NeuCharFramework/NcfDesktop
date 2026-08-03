/*----------------------------------------------------------------
    Copyright (C) 2026 Senparc

    文件名：MainWindowViewModel.WakeWord.cs
    文件功能描述：固定唤醒词设置、模型下载与录音生命周期联动

    创建标识：Senparc - 20260803
----------------------------------------------------------------*/

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NcfDesktopApp.GUI.Services;

namespace NcfDesktopApp.GUI.ViewModels;

public partial class MainWindowViewModel
{
    // 性能问题排查阶段：暂停所有常驻唤醒监听，但保留用户原来的设置值，
    // 后续恢复功能时不会丢失用户选择。
    private static readonly bool WakeWordTemporarilyDisabled = true;

    private readonly ILocalWakeWordService _wakeWordService = LocalWakeWordService.Shared;
    private readonly SemaphoreSlim _wakeWordLifecycleGate = new(1, 1);
    private CancellationTokenSource? _wakeWordModelDownloadCts;
    private bool _wakeWordHandlingDetection;
    private bool _voiceInputStarting;
    private bool _workspaceAudioDisposed;

    [ObservableProperty]
    private bool _wakeWordEnabled;

    [ObservableProperty]
    private bool _isWakeWordModelReady;

    [ObservableProperty]
    private bool _isWakeWordModelBusy;

    [ObservableProperty]
    private bool _isWakeWordListening;

    [ObservableProperty]
    private string _wakeWordStatusText = "固定唤醒词默认关闭，不会常驻使用麦克风。";

    [ObservableProperty]
    private string _wakeWordDownloadProgressText = string.Empty;

    [ObservableProperty]
    private double _wakeWordDownloadProgressValue;

    [ObservableProperty]
    private bool _isWakeWordDownloadProgressIndeterminate = true;

    public string WakePhraseText =>
        $"“{WakeWordModelCatalog.WakePhraseDisplay}”（读作“{WakeWordModelCatalog.WakePhrasePronunciation}”）";

    public bool IsWakeWordControlEnabled => !WakeWordTemporarilyDisabled;

    public string WakeWordStatusColor => IsWakeWordListening
        ? "#16A34A"
        : WakeWordEnabled && !IsWakeWordModelReady
            ? "#DC3545"
            : "#D97706";

    partial void OnWakeWordEnabledChanged(bool value)
    {
        if (!value)
        {
            _wakeWordModelDownloadCts?.Cancel();
        }

        if (!_suppressDesktopSettingsSave)
        {
            SaveDesktopSettings();
        }

        OnPropertyChanged(nameof(WakeWordStatusColor));
        DownloadWakeWordModelCommand.NotifyCanExecuteChanged();
        ScheduleWakeWordListeningRefresh();
    }

    partial void OnIsWakeWordModelReadyChanged(bool value)
    {
        OnPropertyChanged(nameof(WakeWordStatusColor));
        DownloadWakeWordModelCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsWakeWordModelBusyChanged(bool value) =>
        DownloadWakeWordModelCommand.NotifyCanExecuteChanged();

    partial void OnIsWakeWordListeningChanged(bool value) =>
        OnPropertyChanged(nameof(WakeWordStatusColor));

    [RelayCommand(CanExecute = nameof(CanDownloadWakeWordModel))]
    private async Task DownloadWakeWordModel()
    {
        await StopWakeWordListeningForOperationAsync().ConfigureAwait(true);
        _wakeWordModelDownloadCts?.Cancel();
        _wakeWordModelDownloadCts?.Dispose();
        _wakeWordModelDownloadCts = new CancellationTokenSource();
        IsWakeWordModelBusy = true;
        WakeWordDownloadProgressValue = 0;
        IsWakeWordDownloadProgressIndeterminate = true;
        WakeWordDownloadProgressText = "正在下载约 31 MiB 的 INT8 唤醒模型…";
        WakeWordStatusText = "下载完成前不会启动麦克风监听。";
        long lastReportedBytes = 0;
        string? lastReportedSource = null;
        try
        {
            await _wakeWordService.DownloadModelAsync(
                download =>
                {
                    var sourceChanged = !string.Equals(
                        lastReportedSource,
                        download.SourceName,
                        StringComparison.Ordinal);
                    var expectedBytes = download.TotalBytes is > 0
                        ? download.TotalBytes.Value
                        : WakeWordModelCatalog.ApproximateDownloadBytes;
                    if (download.Stage == WakeWordDownloadStage.Downloading &&
                        !sourceChanged &&
                        download.DownloadedBytes - lastReportedBytes < 512 * 1024 &&
                        download.DownloadedBytes < expectedBytes)
                    {
                        return;
                    }

                    lastReportedSource = download.SourceName;
                    lastReportedBytes = download.DownloadedBytes;
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (download.Stage == WakeWordDownloadStage.SourceFailed)
                        {
                            var detail = string.IsNullOrWhiteSpace(download.Detail)
                                ? "未知错误"
                                : download.Detail;
                            WakeWordDownloadProgressText = $"{download.SourceName}失败：{detail}；正在尝试备用源…";
                            AddLog($"⚠️ 唤醒模型下载源失败（{download.SourceName}）：{detail}");
                            return;
                        }

                        var hasKnownTotal = download.TotalBytes is > 0;
                        var percent = Math.Clamp(
                            download.DownloadedBytes * 100d / expectedBytes,
                            0,
                            100);
                        WakeWordDownloadProgressValue = percent;
                        IsWakeWordDownloadProgressIndeterminate = !hasKnownTotal;
                        WakeWordDownloadProgressText = download.Stage switch
                        {
                            WakeWordDownloadStage.Connecting => $"正在连接 {download.SourceName}…",
                            WakeWordDownloadStage.Validating =>
                                $"{download.SourceName} 下载完成，正在校验文件完整性…",
                            _ => $"{download.SourceName}：已下载 " +
                                 $"{VoiceModelCatalog.FormatBytes(download.DownloadedBytes)} / " +
                                 $"{VoiceModelCatalog.FormatBytes(expectedBytes)}（{percent:F0}%）"
                        };
                    });
                },
                _wakeWordModelDownloadCts.Token).ConfigureAwait(true);

            RefreshWakeWordModelReadiness();
            WakeWordDownloadProgressValue = 100;
            IsWakeWordDownloadProgressIndeterminate = false;
            WakeWordDownloadProgressText = "模型下载和校验完成。";
            AddLog($"✅ 固定唤醒词模型已就绪: {WakeWordModelCatalog.ModelDirectory}");
        }
        catch (OperationCanceledException)
        {
            WakeWordStatusText = WakeWordEnabled
                ? "唤醒模型下载已取消，可稍后重试。"
                : "固定唤醒词已关闭，模型下载已取消。";
            WakeWordDownloadProgressText = string.Empty;
        }
        catch (Exception ex)
        {
            WakeWordStatusText = $"唤醒模型下载失败：{ex.Message}";
            WakeWordDownloadProgressText = string.Empty;
            AddLog($"❌ 唤醒模型下载失败: {ex.Message}");
        }
        finally
        {
            IsWakeWordModelBusy = false;
            _wakeWordModelDownloadCts?.Dispose();
            _wakeWordModelDownloadCts = null;
            ScheduleWakeWordListeningRefresh();
        }
    }

    private bool CanDownloadWakeWordModel() =>
        !IsWakeWordModelReady &&
        !IsWakeWordModelBusy &&
        !IsVoiceInputBusy &&
        !IsTtsPlaying;

    [RelayCommand]
    private void CancelWakeWordModelDownload()
    {
        _wakeWordModelDownloadCts?.Cancel();
    }

    [RelayCommand]
    private void OpenWakeWordModelDirectory()
    {
        Directory.CreateDirectory(WakeWordModelCatalog.ModelsDirectory);
        OpenBrowser(WakeWordModelCatalog.ModelsDirectory);
    }

    internal void RefreshWakeWordModelReadiness()
    {
        var readiness = WakeWordModelCatalog.Evaluate();
        IsWakeWordModelReady = readiness.IsReady;
        if (WakeWordEnabled || !readiness.IsReady)
        {
            WakeWordStatusText = readiness.Message;
        }

        OnPropertyChanged(nameof(WakeWordStatusColor));
        DownloadWakeWordModelCommand.NotifyCanExecuteChanged();
    }

    internal void ScheduleWakeWordListeningRefresh()
    {
        if (_workspaceAudioDisposed)
        {
            return;
        }

        if (WakeWordTemporarilyDisabled)
        {
            IsWakeWordListening = false;
            WakeWordStatusText = "性能排查中：固定唤醒监听已临时停用，不会占用麦克风或运行后台识别。";
            return;
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            _ = RefreshWakeWordListeningAsync();
        }
        else
        {
            Dispatcher.UIThread.Post(() => _ = RefreshWakeWordListeningAsync());
        }
    }

    internal async Task StopWakeWordListeningForOperationAsync()
    {
        await _wakeWordLifecycleGate.WaitAsync().ConfigureAwait(true);
        try
        {
            if (_wakeWordService.ListeningOwner == _voiceInputOwner)
            {
                await _wakeWordService.StopListeningAsync(_voiceInputOwner).ConfigureAwait(true);
            }

            IsWakeWordListening = false;
        }
        finally
        {
            _wakeWordLifecycleGate.Release();
        }
    }

    private async Task RefreshWakeWordListeningAsync()
    {
        await _wakeWordLifecycleGate.WaitAsync().ConfigureAwait(true);
        try
        {
            var shouldListen = WakeWordListeningPolicy.ShouldListen(
                !WakeWordTemporarilyDisabled && WakeWordEnabled,
                IsWakeWordModelReady,
                IsVoiceModelReady,
                IsAdminChatActive,
                IsAdminChatBusy,
                IsVoiceInputBusy || _voiceInputStarting || _wakeWordHandlingDetection,
                IsTtsPlaying,
                _workspaceAudioDisposed);

            if (!shouldListen)
            {
                if (_wakeWordService.ListeningOwner == _voiceInputOwner)
                {
                    await _wakeWordService.StopListeningAsync(_voiceInputOwner).ConfigureAwait(true);
                }

                IsWakeWordListening = false;
                WakeWordStatusText = GetWakeWordIdleStatus();
                return;
            }

            var readiness = WakeWordModelCatalog.Evaluate();
            if (!readiness.IsReady || readiness.Files == null)
            {
                IsWakeWordModelReady = false;
                WakeWordStatusText = readiness.Message;
                return;
            }

            await _wakeWordService.StartListeningAsync(
                _voiceInputOwner,
                readiness.Files,
                CancellationToken.None).ConfigureAwait(true);
            IsWakeWordListening = true;
            WakeWordStatusText =
                $"正在本机等待 {WakePhraseText}；未唤醒音频只在内存中流过，不保存、不上传。";
        }
        catch (Exception ex)
        {
            IsWakeWordListening = false;
            WakeWordStatusText = $"无法启动唤醒监听：{ex.Message}";
            AddLog($"⚠️ 唤醒监听未启动: {ex.Message}");
        }
        finally
        {
            _wakeWordLifecycleGate.Release();
        }
    }

    private string GetWakeWordIdleStatus()
    {
        if (WakeWordTemporarilyDisabled)
        {
            return "性能排查中：固定唤醒监听已临时停用，不会占用麦克风或运行后台识别。";
        }

        if (!WakeWordEnabled)
        {
            return "固定唤醒词已关闭，麦克风不会为唤醒功能常驻。";
        }

        if (IsWakeWordModelBusy)
        {
            return "正在准备唤醒模型，暂未使用麦克风。";
        }

        if (!IsWakeWordModelReady)
        {
            return WakeWordModelCatalog.Evaluate().Message;
        }

        if (!IsVoiceModelReady)
        {
            return "等待 Whisper 语音模型就绪；唤醒监听尚未启动。";
        }

        if (!IsAdminChatActive)
        {
            return "等待 NCF、DesktopBridge 和 AdminChat 登录就绪；唤醒监听尚未启动。";
        }

        if (IsAdminChatBusy)
        {
            return "Agent 正在处理消息，唤醒监听已暂时暂停。";
        }

        if (IsVoiceInputBusy || _voiceInputStarting || _wakeWordHandlingDetection)
        {
            return "语音录制或转写期间，唤醒监听已暂时暂停。";
        }

        if (IsTtsPlaying)
        {
            return "本机正在朗读，为避免扬声器误触发，唤醒监听已暂时暂停。";
        }

        return "唤醒监听已暂停。";
    }

    private void InitializeWakeWordServiceForWorkspace()
    {
        _wakeWordService.KeywordDetected += OnWakeWordDetected;
        _wakeWordService.ListeningFailed += OnWakeWordListeningFailed;
    }

    private async Task DisposeWakeWordServiceForWorkspaceAsync()
    {
        _workspaceAudioDisposed = true;
        _wakeWordModelDownloadCts?.Cancel();
        await StopWakeWordListeningForOperationAsync().ConfigureAwait(true);
        _wakeWordService.KeywordDetected -= OnWakeWordDetected;
        _wakeWordService.ListeningFailed -= OnWakeWordListeningFailed;
    }

    private void OnWakeWordDetected(WakeWordDetectedEvent detected)
    {
        if (detected.Owner != _voiceInputOwner || _workspaceAudioDisposed)
        {
            return;
        }

        Dispatcher.UIThread.Post(() => _ = HandleWakeWordDetectedAsync(detected));
    }

    private async Task HandleWakeWordDetectedAsync(WakeWordDetectedEvent detected)
    {
        if (_wakeWordHandlingDetection || !WakeWordEnabled || _workspaceAudioDisposed)
        {
            return;
        }

        _wakeWordHandlingDetection = true;
        try
        {
            WakeWordStatusText = $"已检测到“{detected.Phrase}”，正在切换到语音录制…";
            Robot.SetVoiceInputState("已唤醒", "正在开始本地语音录制");
            await StopWakeWordListeningForOperationAsync().ConfigureAwait(true);
            await StartVoiceInputAsync(startedByWakeWord: true).ConfigureAwait(true);
        }
        finally
        {
            _wakeWordHandlingDetection = false;
            if (!IsVoiceRecording)
            {
                ScheduleWakeWordListeningRefresh();
            }
        }
    }

    private void OnWakeWordListeningFailed(WakeWordServiceFailure failure)
    {
        if (failure.Owner != _voiceInputOwner || _workspaceAudioDisposed)
        {
            return;
        }

        Dispatcher.UIThread.Post(() => _ = HandleWakeWordListeningFailureAsync(failure.Exception));
    }

    private async Task HandleWakeWordListeningFailureAsync(Exception exception)
    {
        try
        {
            await StopWakeWordListeningForOperationAsync().ConfigureAwait(true);
        }
        catch
        {
            // 原始监听错误更有诊断价值。
        }

        IsWakeWordListening = false;
        WakeWordStatusText = $"唤醒监听已停止：{exception.Message}";
        AddLog($"❌ 唤醒监听失败: {exception.Message}");
    }
}
