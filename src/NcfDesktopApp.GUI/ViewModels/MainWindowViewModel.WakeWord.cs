/*----------------------------------------------------------------
    Copyright (C) 2026 Senparc

    文件名：MainWindowViewModel.WakeWord.cs
    文件功能描述：固定唤醒词设置、模型下载与录音生命周期联动

    创建标识：Senparc - 20260803

    修改标识：Senparc - 20260804
    修改描述：v0.6.0 管理固定唤醒词设置与监听生命周期

    修改标识：Senparc - 20260807
    修改描述：移除性能排查期间遗留的全局停用门闩，恢复固定唤醒词监听

    修改标识：Senparc - 20260812
    修改描述：v0.10.0 完善桌面端唤醒词会话激活与中英文提示

    修改标识：Senparc - 20260826
    修改描述：v0.11.0 管理多条自定义唤醒词及设置页应用

----------------------------------------------------------------*/

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NcfDesktopApp.GUI.Models;
using NcfDesktopApp.GUI.Services;

namespace NcfDesktopApp.GUI.ViewModels;

public partial class MainWindowViewModel
{
    private static readonly TimeSpan WakeFeedbackTailProtection = TimeSpan.FromMilliseconds(100);

    private readonly ILocalWakeWordService _wakeWordService = LocalWakeWordService.Shared;
    private readonly SemaphoreSlim _wakeWordLifecycleGate = new(1, 1);
    private CancellationTokenSource? _wakeWordModelDownloadCts;
    private bool _wakeWordHandlingDetection;
    private bool _voiceInputStarting;
    private bool _workspaceAudioDisposed;
    // macOS 的 TCC 麦克风授权回调与 MiniAudio 初始化存在平台时序差异。
    // 已保存的“开启”状态不能在登录瞬间再次触发原生权限流程；本次运行中由
    // 用户实际切换一次开关后才允许自动监听。Windows/Linux 保持原有行为。
    private bool _wakeWordExplicitlyToggledThisSession;

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

    [ObservableProperty]
    private string _wakeWordConfigurationStatusText =
        "可添加多个唤醒词；修改后点击“应用唤醒词配置”才会重建本机关键词模型。";

    public ObservableCollection<WakeWordConfiguration> WakeWordConfigurations { get; } = new();

    public ObservableCollection<WakeWordChatSessionOption> WakeWordChatSessionOptions { get; } = new();

    public string WakePhraseText
    {
        get
        {
            var defaultWord = WakeWordConfigurations.FirstOrDefault(configuration => configuration.IsDefault) ??
                              WakeWordConfigurations.FirstOrDefault();
            if (defaultWord == null)
            {
                return $"“{WakeWordModelCatalog.WakePhraseDisplay}”（读作“{WakeWordModelCatalog.WakePhrasePronunciation}”）";
            }

            return $"“{defaultWord.DisplayLabel}”";
        }
    }

    public string WakePhraseLabel => LocalizationService.T("Settings.WakePhrase", WakePhraseText);

    public string WakeWordEnableLabel => LocalizationService.T("Settings.WakeWordEnable", WakePhraseText);

    public string WakeWordStatusColor => IsWakeWordListening
        ? "#16A34A"
        : WakeWordEnabled && !IsWakeWordModelReady
            ? "#DC3545"
            : "#D97706";

    /// <summary>
    /// 仅在 macOS 恢复了已保存的开启状态、但还未在本次运行中安全激活时显示。
    /// </summary>
    public bool IsWakeWordSessionActivationRequired =>
        !_workspaceAudioDisposed &&
        WakeWordStartupPolicy.RequiresCurrentSessionActivation(
            OperatingSystem.IsMacOS(),
            WakeWordEnabled,
            _wakeWordExplicitlyToggledThisSession);

    partial void OnWakeWordEnabledChanged(bool value)
    {
        if (!_suppressDesktopSettingsSave)
        {
            _wakeWordExplicitlyToggledThisSession = true;
        }

        if (!value)
        {
            _wakeWordModelDownloadCts?.Cancel();
        }

        if (!_suppressDesktopSettingsSave)
        {
            SaveDesktopSettings();
        }

        OnPropertyChanged(nameof(WakeWordStatusColor));
        OnPropertyChanged(nameof(IsWakeWordSessionActivationRequired));
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

    [RelayCommand]
    private void AddWakeWord()
    {
        var configuration = new WakeWordConfiguration
        {
            Phrase = "新唤醒词",
            Pinyin = string.Empty,
            IsEnabled = true,
            IsDefault = WakeWordConfigurations.Count == 0
        };
        WakeWordConfigurations.Add(configuration);
        ObserveWakeWordConfiguration(configuration);
        PopulateAutomaticPinyin(configuration);
        NotifyWakePhraseChanged();
        WakeWordConfigurationStatusText = "已添加唤醒词，请填写短语并应用配置。";
    }

    [RelayCommand]
    private void RemoveWakeWord(WakeWordConfiguration? configuration)
    {
        if (configuration == null || WakeWordConfigurations.Count <= 1)
        {
            WakeWordConfigurationStatusText = "至少保留一个唤醒词。";
            return;
        }

        var wasDefault = configuration.IsDefault;
        UnobserveWakeWordConfiguration(configuration);
        WakeWordConfigurations.Remove(configuration);
        if (wasDefault && WakeWordConfigurations.Count > 0)
        {
            WakeWordConfigurations[0].IsDefault = true;
        }

        NotifyWakePhraseChanged();
        WakeWordConfigurationStatusText = "已移除唤醒词，请应用配置。";
    }

    internal void RegenerateWakeWordPinyin(WakeWordConfiguration? configuration)
    {
        if (configuration == null)
        {
            return;
        }

        if (!WakeWordModelCatalog.TryGetSuggestedPinyin(
                configuration.Phrase,
                out var suggestedPinyin,
                out var error))
        {
            configuration.IsWakeDetectionValid = false;
            configuration.WakeDetectionValidationMessage = error;
            WakeWordConfigurationStatusText = $"无法从“{configuration.DisplayLabel}”自动生成拼音：{error}";
            return;
        }

        configuration.Pinyin = suggestedPinyin;
        configuration.IsWakeDetectionValid = true;
        configuration.WakeDetectionValidationMessage = "已按中文短语自动生成拼音，请应用配置。";
        WakeWordConfigurationStatusText = $"已更新“{configuration.DisplayLabel}”的拼音，请应用配置。";
    }

    [RelayCommand]
    private void SetDefaultWakeWord(WakeWordConfiguration? configuration)
    {
        if (configuration == null)
        {
            return;
        }

        foreach (var item in WakeWordConfigurations)
        {
            item.IsDefault = ReferenceEquals(item, configuration);
        }

        NotifyWakePhraseChanged();
        WakeWordConfigurationStatusText = $"默认唤醒词已设为“{configuration.DisplayLabel}”，请应用配置。";
    }

    [RelayCommand]
    private async Task ApplyWakeWordConfiguration()
    {
        NormalizeWakeWordConfigurations();
        await StopWakeWordListeningForOperationAsync().ConfigureAwait(true);
        RefreshWakeWordModelReadiness();
        var readiness = WakeWordModelCatalog.Evaluate();
        if (readiness.IsReady)
        {
            var configuredModel = WakeWordModelCatalog.BuildConfiguredFiles(
                readiness.ModelDirectory,
                WakeWordConfigurations);
            ApplyWakeWordValidations(configuredModel);
            WakeWordConfigurationStatusText = configuredModel.Message;
        }
        else
        {
            WakeWordConfigurationStatusText = readiness.Message;
        }

        SaveDesktopSettings();
        ScheduleWakeWordListeningRefresh();
    }

    private void NormalizeWakeWordConfigurations()
    {
        if (WakeWordConfigurations.Count == 0)
        {
            var defaultConfiguration = WakeWordModelCatalog.CreateDefaultConfiguration();
            WakeWordConfigurations.Add(defaultConfiguration);
            ObserveWakeWordConfiguration(defaultConfiguration);
        }

        var defaultWord = WakeWordConfigurations.FirstOrDefault(configuration => configuration.IsDefault) ??
                          WakeWordConfigurations.First();
        foreach (var item in WakeWordConfigurations)
        {
            item.IsDefault = ReferenceEquals(item, defaultWord);
            item.Phrase = item.Phrase.Trim();
            item.Pinyin = item.Pinyin?.Trim() ?? string.Empty;
            item.TargetSessionId = Math.Max(0, item.TargetSessionId);
            PopulateAutomaticPinyin(item);
        }
    }

    internal void LoadWakeWordConfigurations(IEnumerable<WakeWordConfiguration>? configurations)
    {
        foreach (var existing in WakeWordConfigurations)
        {
            UnobserveWakeWordConfiguration(existing);
        }

        WakeWordConfigurations.Clear();
        foreach (var configuration in configurations ?? Array.Empty<WakeWordConfiguration>())
        {
            if (configuration == null)
            {
                continue;
            }

            WakeWordConfigurations.Add(configuration);
            ObserveWakeWordConfiguration(configuration);
            PopulateAutomaticPinyin(configuration);
        }

        if (WakeWordConfigurations.Count == 0)
        {
            var defaultConfiguration = WakeWordModelCatalog.CreateDefaultConfiguration();
            WakeWordConfigurations.Add(defaultConfiguration);
            ObserveWakeWordConfiguration(defaultConfiguration);
        }

        NormalizeWakeWordConfigurations();
        RefreshWakeWordChatSessionOptions();
        NotifyWakePhraseChanged();
    }

    internal void RefreshWakeWordChatSessionOptions()
    {
        WakeWordChatSessionOptions.Clear();
        WakeWordChatSessionOptions.Add(new WakeWordChatSessionOption(
            0,
            LocalizationService.T("Settings.WakeCurrentChat")));
        foreach (var session in AdminChatSessions)
        {
            WakeWordChatSessionOptions.Add(new WakeWordChatSessionOption(session.Id, session.DisplayName));
        }

        foreach (var configuration in WakeWordConfigurations)
        {
            var selected = WakeWordChatSessionOptions.FirstOrDefault(
                option => option.Id == configuration.TargetSessionId);
            if (selected != null)
            {
                configuration.SelectedTargetSession = selected;
            }
            else if (configuration.TargetSessionId <= 0)
            {
                configuration.SelectedTargetSession = WakeWordChatSessionOptions[0];
            }
        }
    }

    private void ObserveWakeWordConfiguration(WakeWordConfiguration configuration)
    {
        configuration.PropertyChanged -= OnWakeWordConfigurationPropertyChanged;
        configuration.PropertyChanged += OnWakeWordConfigurationPropertyChanged;
    }

    private void UnobserveWakeWordConfiguration(WakeWordConfiguration configuration)
    {
        configuration.PropertyChanged -= OnWakeWordConfigurationPropertyChanged;
    }

    private void OnWakeWordConfigurationPropertyChanged(
        object? sender,
        System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(WakeWordConfiguration.Phrase) or
            nameof(WakeWordConfiguration.IsDefault))
        {
            if (sender is WakeWordConfiguration configuration &&
                e.PropertyName == nameof(WakeWordConfiguration.Phrase))
            {
                PopulateAutomaticPinyin(configuration);
                if (!string.IsNullOrWhiteSpace(configuration.Pinyin))
                {
                    configuration.IsWakeDetectionValid = true;
                    configuration.WakeDetectionValidationMessage =
                        "已根据中文短语自动更新拼音，请应用配置。";
                    WakeWordConfigurationStatusText =
                        "已根据中文短语自动更新拼音；请应用唤醒词配置。";
                }
            }

            NotifyWakePhraseChanged();
        }
    }

    private static void PopulateAutomaticPinyin(WakeWordConfiguration configuration)
    {
        if (!WakeWordModelCatalog.TryGetSuggestedPinyin(
                configuration.Phrase,
                out var suggestedPinyin,
                out _) ||
            (!string.IsNullOrWhiteSpace(configuration.Pinyin) &&
             WakeWordModelCatalog.HasToneInformation(configuration.Pinyin)))
        {
            return;
        }

        configuration.Pinyin = suggestedPinyin;
    }

    private void ApplyWakeWordValidations(WakeWordConfiguredModel configuredModel)
    {
        var validations = configuredModel.Validations
            .GroupBy(validation => validation.ConfigurationId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Last(),
                StringComparer.OrdinalIgnoreCase);
        foreach (var configuration in WakeWordConfigurations)
        {
            if (!validations.TryGetValue(configuration.Id, out var validation))
            {
                configuration.IsWakeDetectionValid = configuredModel.IsReady;
                configuration.WakeDetectionValidationMessage = configuredModel.Message;
                continue;
            }

            configuration.IsWakeDetectionValid = validation.IsValid;
            configuration.WakeDetectionValidationMessage = validation.Message;
            if (validation.IsValid &&
                !string.IsNullOrWhiteSpace(validation.EffectivePinyin) &&
                (string.IsNullOrWhiteSpace(configuration.Pinyin) ||
                 !WakeWordModelCatalog.HasToneInformation(configuration.Pinyin)))
            {
                configuration.Pinyin = validation.EffectivePinyin;
            }
        }
    }

    private void NotifyWakePhraseChanged()
    {
        OnPropertyChanged(nameof(WakePhraseText));
        OnPropertyChanged(nameof(WakePhraseLabel));
        OnPropertyChanged(nameof(WakeWordEnableLabel));
    }

    private WakeWordConfiguration? FindWakeWordConfiguration(string keywordId)
    {
        return WakeWordConfigurations.FirstOrDefault(configuration =>
            configuration.IsEnabled &&
            string.Equals(
                WakeWordModelCatalog.GetKeywordAlias(configuration),
                keywordId,
                StringComparison.OrdinalIgnoreCase));
    }

    private int PrepareWakeWordChatTarget(WakeWordConfiguration? configuration)
    {
        var targetSessionId = configuration?.TargetSessionId ?? 0;
        if (targetSessionId > 0)
        {
            var targetSession = AdminChatSessions.FirstOrDefault(session => session.Id == targetSessionId);
            if (targetSession != null)
            {
                SelectedAdminChatSession = targetSession;
                return targetSession.Id;
            }

            WakeWordConfigurationStatusText =
                $"唤醒词“{configuration?.DisplayLabel}”关联的 Chat 已不存在，将使用当前选中的 Chat。";
        }

        return SelectedAdminChatSession?.Id ?? 0;
    }

    /// <summary>
    /// 供 macOS 主界面的一键入口使用。它先确保没有残留的监听，再把已保存的
    /// 开启状态安全地应用到本次运行；效果等同于用户手动关闭后再开启，但不会把
    /// 设置文件短暂写成关闭状态。
    /// </summary>
    [RelayCommand]
    private async Task ActivateWakeWordForCurrentSession()
    {
        if (!IsWakeWordSessionActivationRequired)
        {
            return;
        }

        await StopWakeWordListeningForOperationAsync().ConfigureAwait(true);
        if (_workspaceAudioDisposed)
        {
            return;
        }

        _wakeWordExplicitlyToggledThisSession = true;
        OnPropertyChanged(nameof(IsWakeWordSessionActivationRequired));
        WakeWordStatusText = "已重新开启本次运行的固定唤醒词；正在检查监听条件…";
        AddLog("🎙️ 已通过主界面重新开启本次运行的固定唤醒词监听。");
        ScheduleWakeWordListeningRefresh();
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

        if (IsWakeWordSessionActivationRequired)
        {
            IsWakeWordListening = false;
            WakeWordStatusText =
                "macOS 已保留固定唤醒词设置；为避免登录时触发麦克风授权导致主窗口退出，请点击主界面右上角“开启唤醒”。";
            return;
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            StartWakeWordListeningRefresh();
        }
        else
        {
            Dispatcher.UIThread.Post(StartWakeWordListeningRefresh);
        }
    }

    /// <summary>
    /// 启动唤醒监听刷新但不把任何生命周期/原生音频异常泄漏到 UI dispatcher。
    /// 设备权限变化时，SoundFlow 可能在创建 capture device 或释放设备期间抛出
    /// ObjectDisposedException；这类故障只应停用唤醒监听，不能结束 AdminChat。
    /// </summary>
    private void StartWakeWordListeningRefresh()
    {
        _ = RefreshWakeWordListeningSafelyAsync();
    }

    private async Task RefreshWakeWordListeningSafelyAsync()
    {
        try
        {
            await RefreshWakeWordListeningAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // 工作台关闭或音频操作切换时的正常取消。
        }
        catch (Exception ex)
        {
            IsWakeWordListening = false;
            WakeWordStatusText = $"唤醒监听已安全停止：{ex.Message}";
            AddLog($"⚠️ 唤醒监听已安全停止: {ex.Message}");
            CrashDiagnosticService.ReportHandledException("唤醒监听生命周期", ex);
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
                WakeWordEnabled,
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

            var configuredModel = WakeWordModelCatalog.BuildConfiguredFiles(
                readiness.ModelDirectory,
                WakeWordConfigurations);
            ApplyWakeWordValidations(configuredModel);
            WakeWordConfigurationStatusText = configuredModel.Message;
            if (!configuredModel.IsReady || configuredModel.Files == null)
            {
                WakeWordStatusText = configuredModel.Message;
                return;
            }

            await _wakeWordService.StartListeningAsync(
                _voiceInputOwner,
                configuredModel.Files,
                CancellationToken.None).ConfigureAwait(true);
            IsWakeWordListening = true;
            WakeWordStatusText =
                $"正在本机等待 {configuredModel.Files.KeywordDefinitions?.Count ?? 0} 个唤醒词；" +
                "未唤醒音频只在内存中流过，不保存、不上传。";
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

        if (IsVoiceInputBusy || _voiceInputStarting || _wakeWordHandlingDetection)
        {
            return "语音录制或转写期间，唤醒监听已暂时暂停。";
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
        OnPropertyChanged(nameof(IsWakeWordSessionActivationRequired));
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

        Dispatcher.UIThread.Post(() => _ = HandleWakeWordDetectedSafelyAsync(detected));
    }

    private async Task HandleWakeWordDetectedSafelyAsync(WakeWordDetectedEvent detected)
    {
        try
        {
            await HandleWakeWordDetectedAsync(detected).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _wakeWordHandlingDetection = false;
            IsWakeWordListening = false;
            WakeWordStatusText = $"唤醒操作已安全停止：{ex.Message}";
            AddLog($"⚠️ 唤醒操作未完成: {ex.Message}");
            CrashDiagnosticService.ReportHandledException("处理唤醒词", ex);
        }
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
            var configuration = FindWakeWordConfiguration(detected.KeywordId);
            var interruptedTtsPlayback = IsTtsPlaying;
            var targetSessionId = PrepareWakeWordChatTarget(configuration);
            WakeWordStatusText = $"已检测到“{detected.Phrase}”，正在切换到语音录制…";
            Robot.SetVoiceInputState("已唤醒", "正在开始本地语音录制");
            if (interruptedTtsPlayback)
            {
                StopTtsPlayback();
                TtsPlaybackStatusText = "检测到唤醒词，已停止朗读并准备录音。";
                AddLog("🎙️ 朗读期间检测到唤醒词，已停止本地朗读并切换到录音。");
            }
            await StopWakeWordListeningForOperationAsync().ConfigureAwait(true);
            var feedbackPlayed = await PlayWakeWordFeedbackAsync(
                    "listening",
                    _cancellationTokenSource?.Token ?? CancellationToken.None)
                .ConfigureAwait(true);
            if (feedbackPlayed)
            {
                await Task.Delay(
                        WakeFeedbackTailProtection,
                        _cancellationTokenSource?.Token ?? CancellationToken.None)
                    .ConfigureAwait(true);
            }

            await StartVoiceInputAsync(
                startedByWakeWord: true,
                allowWhileAdminChatBusy: true,
                wakeWordTargetSessionId: targetSessionId).ConfigureAwait(true);
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

        Dispatcher.UIThread.Post(() => _ = HandleWakeWordListeningFailureSafelyAsync(failure.Exception));
    }

    private async Task HandleWakeWordListeningFailureSafelyAsync(Exception exception)
    {
        try
        {
            await HandleWakeWordListeningFailureAsync(exception).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            IsWakeWordListening = false;
            WakeWordStatusText = $"唤醒监听已停止：{exception.Message}";
            AddLog($"❌ 唤醒监听失败: {exception.Message}");
            CrashDiagnosticService.ReportHandledException("处理唤醒监听失败", ex);
        }
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
