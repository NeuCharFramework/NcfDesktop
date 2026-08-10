/*----------------------------------------------------------------
    Copyright (C) 2026 Senparc

    文件名：MainWindowViewModel.VoiceInput.cs
    文件功能描述：本地语音模型配置、录音、转写及 AdminChat 输入联动

    创建标识：Senparc - 20260801

    修改标识：Senparc - 20260804
    修改描述：v0.6.0 增加语音识别完成后的可选自动发送

----------------------------------------------------------------*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NcfDesktopApp.GUI.Models;
using NcfDesktopApp.GUI.Services;

namespace NcfDesktopApp.GUI.ViewModels;

public partial class MainWindowViewModel
{
    private readonly ILocalVoiceInputService _voiceInputService = LocalVoiceInputService.Shared;
    private readonly Guid _voiceInputOwner = Guid.NewGuid();
    private CancellationTokenSource? _voiceRecognitionCts;
    private CancellationTokenSource? _voiceModelDownloadCts;
    private bool _voiceInputStartedByWakeWord;
    private int _voiceAutoStopInProgress;
    private string? _pendingWakeWordTranscript;

    [ObservableProperty]
    private VoiceModelOption? _selectedVoiceModel;

    [ObservableProperty]
    private string _voiceCustomModelPath = string.Empty;

    [ObservableProperty]
    private string _voiceLanguage = "auto";

    [ObservableProperty]
    private bool _sttAutoSend;

    [ObservableProperty]
    private string _voiceModelStatusText = "尚未选择语音模型。";

    [ObservableProperty]
    private string _voiceModelPathText = "—";

    [ObservableProperty]
    private string _voiceModelDownloadProgressText = string.Empty;

    [ObservableProperty]
    private bool _isVoiceModelBusy;

    [ObservableProperty]
    private bool _isVoiceModelReady;

    [ObservableProperty]
    private bool _isVoiceRecording;

    [ObservableProperty]
    private bool _isVoiceTranscribing;

    [ObservableProperty]
    private string _voiceInputStatusText =
        "语音将在本机转写；手动识别结果会先进入输入框。唤醒词启动的语音会在检测到讲话结束后自动发送。";

    public IReadOnlyList<VoiceModelOption> VoiceModelOptions => VoiceModelCatalog.Options;

    public IReadOnlyList<string> VoiceLanguageOptions { get; } = new[] { "auto", "zh", "en" };

    public string VoiceInputButtonText => IsVoiceRecording ? "停止" : IsVoiceTranscribing ? "识别中" : "语音";

    public string VoiceInputButtonIcon => IsVoiceRecording ? "■" : IsVoiceTranscribing ? "…" : "🎙";

    public string VoiceModelStatusColor => IsVoiceModelReady ? "#16A34A" : "#D97706";

    public bool IsVoiceInputBusy => IsVoiceRecording || IsVoiceTranscribing;

    public bool IsVoiceCancelVisible => IsVoiceRecording || IsVoiceTranscribing || IsVoiceModelBusy;

    partial void OnSelectedVoiceModelChanged(VoiceModelOption? value)
    {
        RefreshVoiceModelReadiness();
        NotifyVoiceCommandsChanged();
        if (!_suppressDesktopSettingsSave)
        {
            SaveDesktopSettings();
        }
    }

    partial void OnVoiceCustomModelPathChanged(string value)
    {
        RefreshVoiceModelReadiness();
        NotifyVoiceCommandsChanged();
        if (!_suppressDesktopSettingsSave)
        {
            SaveDesktopSettings();
        }
    }

    partial void OnVoiceLanguageChanged(string value)
    {
        var normalized = NormalizeVoiceLanguage(value);
        if (!string.Equals(normalized, value, StringComparison.Ordinal))
        {
            VoiceLanguage = normalized;
            return;
        }

        if (!_suppressDesktopSettingsSave)
        {
            SaveDesktopSettings();
        }
    }

    partial void OnSttAutoSendChanged(bool value)
    {
        if (!IsVoiceInputBusy)
        {
            VoiceInputStatusText = value
                ? "手动语音将在本机转写；识别完成后会自动发送。"
                : "语音将在本机转写；手动识别结果会先进入输入框。唤醒词启动的语音会在检测到讲话结束后自动发送。";
        }

        if (!_suppressDesktopSettingsSave)
        {
            SaveDesktopSettings();
        }
    }

    partial void OnIsVoiceModelBusyChanged(bool value) => NotifyVoiceStateChanged();

    partial void OnIsVoiceModelReadyChanged(bool value)
    {
        OnPropertyChanged(nameof(VoiceModelStatusColor));
        NotifyVoiceCommandsChanged();
        ScheduleWakeWordListeningRefresh();
    }

    partial void OnIsVoiceRecordingChanged(bool value)
    {
        NotifyVoiceStateChanged();
        ScheduleWakeWordListeningRefresh();
    }

    partial void OnIsVoiceTranscribingChanged(bool value)
    {
        NotifyVoiceStateChanged();
        ScheduleWakeWordListeningRefresh();
    }

    [RelayCommand(CanExecute = nameof(CanDownloadVoiceModel))]
    private async Task DownloadVoiceModel()
    {
        var option = SelectedVoiceModel;
        if (option == null || !option.CanDownload)
        {
            VoiceModelStatusText = "请先选择可下载的内置模型。";
            return;
        }

        var current = VoiceModelCatalog.Evaluate(option, VoiceCustomModelPath);
        if (current.IsReady)
        {
            RefreshVoiceModelReadiness();
            return;
        }

        _voiceModelDownloadCts?.Cancel();
        _voiceModelDownloadCts?.Dispose();
        _voiceModelDownloadCts = new CancellationTokenSource();
        IsVoiceModelBusy = true;
        VoiceModelDownloadProgressText = $"正在下载 {option.DisplayName}…";
        VoiceModelStatusText = "模型下载完成前不能开始语音输入。";
        long lastReportedBytes = 0;
        try
        {
            await _voiceInputService.DownloadModelAsync(
                option,
                bytes =>
                {
                    if (bytes - lastReportedBytes < 2 * 1024 * 1024 && bytes < option.ApproximateBytes)
                    {
                        return;
                    }

                    lastReportedBytes = bytes;
                    Dispatcher.UIThread.Post(() =>
                    {
                        var percent = option.ApproximateBytes <= 0
                            ? 0
                            : Math.Clamp(bytes * 100d / option.ApproximateBytes, 0, 100);
                        VoiceModelDownloadProgressText =
                            $"已下载 {VoiceModelCatalog.FormatBytes(bytes)} / {option.ApproximateSizeText}（约 {percent:F0}%）";
                    });
                },
                _voiceModelDownloadCts.Token);

            RefreshVoiceModelReadiness();
            VoiceModelDownloadProgressText = "模型下载完成，可离线使用语音输入。";
            AddLog($"✅ 本地语音模型已下载: {VoiceModelPathText}");
        }
        catch (OperationCanceledException)
        {
            VoiceModelStatusText = "模型下载已取消，可稍后重新下载。";
            VoiceModelDownloadProgressText = string.Empty;
        }
        catch (Exception ex)
        {
            VoiceModelStatusText = $"模型下载失败：{ex.Message}";
            VoiceModelDownloadProgressText = string.Empty;
            AddLog($"❌ 语音模型下载失败: {ex.Message}");
        }
        finally
        {
            IsVoiceModelBusy = false;
            _voiceModelDownloadCts?.Dispose();
            _voiceModelDownloadCts = null;
            NotifyVoiceCommandsChanged();
        }
    }

    private bool CanDownloadVoiceModel()
    {
        return SelectedVoiceModel is { CanDownload: true } &&
               !IsVoiceModelReady &&
               !IsVoiceModelBusy &&
               !IsVoiceInputBusy;
    }

    [RelayCommand(CanExecute = nameof(CanSelectCustomVoiceModel))]
    private async Task SelectCustomVoiceModel()
    {
        try
        {
            var storageProvider = GetActiveStorageProvider();
            if (storageProvider is not { CanOpen: true })
            {
                VoiceModelStatusText = "当前平台无法打开模型文件选择器。";
                return;
            }

            var files = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择 Whisper GGML 模型",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Whisper GGML 模型")
                    {
                        Patterns = new[] { "*.bin" },
                        MimeTypes = new[] { "application/octet-stream" }
                    }
                }
            });
            var path = files.FirstOrDefault()?.TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            _suppressDesktopSettingsSave = true;
            VoiceCustomModelPath = path;
            SelectedVoiceModel = VoiceModelCatalog.FindById(VoiceModelCatalog.CustomModelId);
            _suppressDesktopSettingsSave = false;
            RefreshVoiceModelReadiness();
            SaveDesktopSettings();
        }
        catch (Exception ex)
        {
            VoiceModelStatusText = $"选择模型失败：{ex.Message}";
        }
    }

    private bool CanSelectCustomVoiceModel() => !IsVoiceModelBusy && !IsVoiceInputBusy;

    [RelayCommand]
    private void OpenVoiceModelDirectory()
    {
        Directory.CreateDirectory(VoiceModelCatalog.ModelsDirectory);
        OpenBrowser(VoiceModelCatalog.ModelsDirectory);
    }

    [RelayCommand]
    private async Task ToggleVoiceInput()
    {
        if (IsVoiceTranscribing || IsVoiceModelBusy)
        {
            VoiceInputStatusText = "当前语音操作尚未完成，请稍候。";
            return;
        }

        if (IsVoiceRecording)
        {
            await StopVoiceInputAndTranscribeAsync(autoSendAfterTranscription: false);
            return;
        }

        await StartVoiceInputAsync(startedByWakeWord: false);
    }

    private async Task StartVoiceInputAsync(
        bool startedByWakeWord,
        bool allowWhileAdminChatBusy = false)
    {

        var readiness = VoiceModelCatalog.Evaluate(SelectedVoiceModel, VoiceCustomModelPath);
        ApplyVoiceModelReadiness(readiness);
        if (!readiness.IsReady)
        {
            VoiceInputStatusText = readiness.Message;
            ShowWorkspaceSettingsRequested?.Invoke();
            Robot.SetVoiceInputState("需要配置", readiness.Message, isError: true);
            return;
        }

        if (!IsAdminChatActive)
        {
            VoiceInputStatusText = "请先启动 NCF、连接 DesktopBridge 并登录 AdminChat。";
            Robot.SetVoiceInputState("等待登录", VoiceInputStatusText, isError: true);
            return;
        }

        if (IsAdminChatBusy && !allowWhileAdminChatBusy)
        {
            VoiceInputStatusText = "AdminChat 正在处理上一条消息，请稍后再开始录音。";
            return;
        }

        _voiceInputStarting = true;
        _voiceInputStartedByWakeWord = startedByWakeWord;
        try
        {
            await StopWakeWordListeningForOperationAsync().ConfigureAwait(true);
            StopTtsPlayback();

            _voiceRecognitionCts?.Cancel();
            _voiceRecognitionCts?.Dispose();
            _voiceRecognitionCts = new CancellationTokenSource();
            try
            {
                await _voiceInputService.StartRecordingAsync(
                    _voiceInputOwner,
                    readiness.ModelPath,
                    automaticallyStopAfterSpeech: startedByWakeWord,
                    cancellationToken: _voiceRecognitionCts.Token);
                IsVoiceRecording = true;
                VoiceInputStatusText = startedByWakeWord
                    ? allowWhileAdminChatBusy && IsAdminChatBusy
                        ? $"已由“{WakeWordModelCatalog.WakePhraseDisplay}”唤醒并开始录音；当前回复结束后会自动发送本次语音。"
                        : $"已由“{WakeWordModelCatalog.WakePhraseDisplay}”唤醒并开始录音；检测到讲话结束后会在本机识别并自动发送。"
                    : "正在录音；再次点击“停止”后将在本机识别。";
                Robot.SetVoiceInputState(
                    "正在录音",
                    startedByWakeWord
                        ? allowWhileAdminChatBusy && IsAdminChatBusy
                            ? "唤醒成功；说完后会自动转写，并在当前回复结束后发送"
                            : "唤醒成功；说完后会自动停止、转写并发送"
                        : "再次点击语音按钮即可停止并转写");
            }
            catch (OperationCanceledException)
            {
                VoiceInputStatusText = "语音输入已取消。";
            }
            catch (Exception ex)
            {
                VoiceInputStatusText = $"无法开始录音：{ex.Message}";
                Robot.SetVoiceInputState("录音失败", ex.Message, isError: true);
                AddLog($"❌ 无法开始语音输入: {ex.Message}");
            }
        }
        finally
        {
            _voiceInputStarting = false;
            if (!IsVoiceRecording)
            {
                _voiceInputStartedByWakeWord = false;
                _voiceRecognitionCts?.Dispose();
                _voiceRecognitionCts = null;
                ScheduleWakeWordListeningRefresh();
            }
        }
    }

    [RelayCommand]
    private async Task CancelVoiceInput()
    {
        _voiceModelDownloadCts?.Cancel();
        _voiceRecognitionCts?.Cancel();
        try
        {
            if (IsVoiceRecording)
            {
                await _voiceInputService.CancelRecordingAsync(_voiceInputOwner);
            }
        }
        catch (Exception ex)
        {
            AddLog($"⚠️ 取消语音输入时清理失败: {ex.Message}");
        }
        finally
        {
            IsVoiceRecording = false;
            _voiceInputStartedByWakeWord = false;
            Interlocked.Exchange(ref _voiceAutoStopInProgress, 0);
            VoiceInputStatusText = "语音输入已取消。";
            Robot.SetVoiceInputState("已取消", "语音内容未发送");
            ScheduleWakeWordListeningRefresh();
        }
    }

    internal async Task CancelVoiceInputForShutdownAsync()
    {
        await DisposeWakeWordServiceForWorkspaceAsync();
        _voiceModelDownloadCts?.Cancel();
        _voiceRecognitionCts?.Cancel();
        try
        {
            if (_voiceInputService.RecordingOwner == _voiceInputOwner)
            {
                await _voiceInputService.CancelRecordingAsync(_voiceInputOwner);
            }
        }
        finally
        {
            DisposeAudioServicesForWorkspace();
        }
    }

    internal void RefreshVoiceModelReadiness()
    {
        ApplyVoiceModelReadiness(VoiceModelCatalog.Evaluate(SelectedVoiceModel, VoiceCustomModelPath));
    }

    private async Task StopVoiceInputAndTranscribeAsync(bool autoSendAfterTranscription)
    {
        var shouldAutoSend = autoSendAfterTranscription || SttAutoSend;
        IsVoiceRecording = false;
        IsVoiceTranscribing = true;
        VoiceInputStatusText = "正在使用本地模型识别，不会上传音频…";
        Robot.SetVoiceInputState("正在识别", "本地 Whisper 模型正在转写语音");
        try
        {
            var transcript = await _voiceInputService.StopAndTranscribeAsync(
                _voiceInputOwner,
                VoiceLanguage,
                _voiceRecognitionCts?.Token ?? CancellationToken.None);
            AddLog("✅ 本地语音识别完成，原始音频未上传");

            // STT 已完成；自动发送可能包含较长的流式回复，不能让界面在此期间继续显示“识别中”。
            IsVoiceTranscribing = false;

            if (shouldAutoSend && IsAdminChatActive)
            {
                if (IsAdminChatBusy)
                {
                    QueueWakeWordTranscriptForAutoSend(transcript);
                    return;
                }

                // 复用 AdminChat 唯一发送入口，使鉴权、流式响应和失败恢复行为与手动发送完全一致。
                VoiceInputStatusText = "识别完成，正在自动发送…";
                Robot.SetVoiceInputState("正在发送", "语音文字正在发送到 AdminChat");
                bool sent;
                try
                {
                    // 自动语音只发送本次识别文本，绝不把用户输入框中已有的草稿一并发送。
                    sent = await SendAdminChatMessageCoreAsync(transcript);
                }
                catch (Exception ex)
                {
                    // 转写已经成功，发送阶段的意外错误不能被误报为“语音识别失败”。
                    AppendTranscriptToChatInput(transcript);
                    VoiceInputStatusText = "识别完成，但自动发送失败；文字已保留在输入框。";
                    Robot.SetVoiceInputState("等待发送", VoiceInputStatusText, isError: true);
                    AddLog($"❌ STT 自动发送失败: {ex.Message}");
                    return;
                }

                if (sent)
                {
                    VoiceInputStatusText = "识别完成，文字已自动发送。";
                    Robot.SetVoiceInputState("已自动发送", "AdminChat 已完成语音消息处理");
                }
                else
                {
                    AppendTranscriptToChatInput(transcript);
                    VoiceInputStatusText = "自动发送未完成，识别文字已保留在输入框。";
                    Robot.SetVoiceInputState("等待发送", VoiceInputStatusText, isError: true);
                }
            }
            else if (shouldAutoSend)
            {
                // 转写期间可能发生退出登录或连接断开；此时必须保留文字，不能静默丢弃。
                AppendTranscriptToChatInput(transcript);
                VoiceInputStatusText = "识别完成，但当前无法自动发送；文字已保留在输入框。";
                Robot.SetVoiceInputState("等待发送", VoiceInputStatusText, isError: true);
            }
            else
            {
                AppendTranscriptToChatInput(transcript);
                VoiceInputStatusText = "识别完成，文字已放入输入框；确认后再发送。";
                Robot.SetVoiceInputState("识别完成", "文字已写入 AdminChat 输入框，请确认后发送");
            }
        }
        catch (OperationCanceledException)
        {
            VoiceInputStatusText = "语音识别已取消，内容未发送。";
            Robot.SetVoiceInputState("已取消", "语音内容未发送");
        }
        catch (Exception ex)
        {
            VoiceInputStatusText = $"语音识别失败：{ex.Message}";
            Robot.SetVoiceInputState("识别失败", ex.Message, isError: true);
            AddLog($"❌ 本地语音识别失败: {ex.Message}");
        }
        finally
        {
            IsVoiceTranscribing = false;
            _voiceInputStartedByWakeWord = false;
            Interlocked.Exchange(ref _voiceAutoStopInProgress, 0);
            _voiceRecognitionCts?.Dispose();
            _voiceRecognitionCts = null;
            ScheduleWakeWordListeningRefresh();
        }
    }

    private void OnVoiceRecordingAutoStopRequested(VoiceRecordingAutoStopRequested request)
    {
        if (request.Owner != _voiceInputOwner || _workspaceAudioDisposed)
        {
            return;
        }

        Dispatcher.UIThread.Post(() => _ = HandleVoiceRecordingAutoStopSafelyAsync(request));
    }

    private void OnVoiceRecordingSpeechDetected(VoiceRecordingSpeechDetected detected)
    {
        if (detected.Owner != _voiceInputOwner || _workspaceAudioDisposed)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (!IsVoiceRecording || !_voiceInputStartedByWakeWord)
            {
                return;
            }

            VoiceInputStatusText = "已检测到有效讲话；停顿约 1 秒后会自动识别并发送。";
            Robot.SetVoiceInputState("正在录音", "已检测到讲话；停顿后将自动转写并发送");
            AddLog("🎙️ 唤醒后的有效讲话已检测到，正在等待说话结束。");
        });
    }

    private async Task HandleVoiceRecordingAutoStopSafelyAsync(VoiceRecordingAutoStopRequested request)
    {
        if (!IsVoiceRecording || !_voiceInputStartedByWakeWord ||
            Interlocked.Exchange(ref _voiceAutoStopInProgress, 1) != 0)
        {
            return;
        }

        try
        {
            if (request.Reason == VoiceRecordingAutoStopReason.SpeechNotDetected)
            {
                await _voiceInputService.CancelRecordingAsync(_voiceInputOwner).ConfigureAwait(true);
                IsVoiceRecording = false;
                _voiceInputStartedByWakeWord = false;
                _voiceRecognitionCts?.Dispose();
                _voiceRecognitionCts = null;
                VoiceInputStatusText = "唤醒后未检测到讲话，语音内容未发送。";
                Robot.SetVoiceInputState("未检测到讲话", "已恢复等待下一次唤醒");
                AddLog("ℹ️ 唤醒后 8 秒内未检测到持续人声，已取消本次语音输入。");
                return;
            }

            VoiceInputStatusText = "检测到讲话结束，正在使用本地模型识别…";
            Robot.SetVoiceInputState("讲话结束", "正在本机转写并自动发送");
            await StopVoiceInputAndTranscribeAsync(autoSendAfterTranscription: true).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // 工作台关闭或用户主动取消时不覆盖后续状态。
        }
        catch (Exception ex)
        {
            VoiceInputStatusText = $"自动结束语音输入失败：{ex.Message}";
            Robot.SetVoiceInputState("自动结束失败", ex.Message, isError: true);
            AddLog($"❌ 唤醒语音自动结束失败: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _voiceAutoStopInProgress, 0);
            if (!IsVoiceRecording && !IsVoiceTranscribing)
            {
                ScheduleWakeWordListeningRefresh();
            }
        }
    }

    private void AppendTranscriptToChatInput(string transcript)
    {
        ChatInput = string.IsNullOrWhiteSpace(ChatInput)
            ? transcript
            : $"{ChatInput.TrimEnd()}{Environment.NewLine}{transcript}";
    }

    private void QueueWakeWordTranscriptForAutoSend(string transcript)
    {
        _pendingWakeWordTranscript = string.IsNullOrWhiteSpace(_pendingWakeWordTranscript)
            ? transcript
            : $"{_pendingWakeWordTranscript.TrimEnd()}{Environment.NewLine}{transcript}";
        VoiceInputStatusText = "识别完成；当前回复结束后会自动发送本次语音文字。";
        Robot.SetVoiceInputState("等待发送", "当前回复完成后将自动发送本次语音文字");
        AddLog("ℹ️ 当前 AdminChat 仍在回复；已排队本次语音文字，完成后自动发送。");
    }

    private void SchedulePendingWakeWordTranscriptSend()
    {
        if (string.IsNullOrWhiteSpace(_pendingWakeWordTranscript) || _workspaceAudioDisposed)
        {
            return;
        }

        Dispatcher.UIThread.Post(() => _ = SendPendingWakeWordTranscriptSafelyAsync());
    }

    private async Task SendPendingWakeWordTranscriptSafelyAsync()
    {
        if (_workspaceAudioDisposed || IsAdminChatBusy || string.IsNullOrWhiteSpace(_pendingWakeWordTranscript))
        {
            return;
        }

        var transcript = _pendingWakeWordTranscript;
        _pendingWakeWordTranscript = null;
        if (!CanUseAdminChat())
        {
            AppendTranscriptToChatInput(transcript);
            VoiceInputStatusText = "当前无法自动发送；已将语音文字保留在输入框。";
            Robot.SetVoiceInputState("等待发送", VoiceInputStatusText, isError: true);
            return;
        }

        VoiceInputStatusText = "上一条回复已完成，正在自动发送语音文字…";
        Robot.SetVoiceInputState("正在发送", "语音文字正在发送到 AdminChat");
        try
        {
            if (await SendAdminChatMessageCoreAsync(transcript).ConfigureAwait(true))
            {
                VoiceInputStatusText = "识别完成，文字已自动发送。";
                Robot.SetVoiceInputState("已自动发送", "AdminChat 已完成语音消息处理");
                return;
            }
        }
        catch (Exception ex)
        {
            AddLog($"❌ 排队语音自动发送失败: {ex.Message}");
        }

        AppendTranscriptToChatInput(transcript);
        VoiceInputStatusText = "自动发送未完成，语音文字已保留在输入框。";
        Robot.SetVoiceInputState("等待发送", VoiceInputStatusText, isError: true);
    }

    private void ApplyVoiceModelReadiness(VoiceModelReadiness readiness)
    {
        IsVoiceModelReady = readiness.IsReady;
        VoiceModelPathText = string.IsNullOrWhiteSpace(readiness.ModelPath) ? "—" : readiness.ModelPath;
        VoiceModelStatusText = readiness.Message;
        OnPropertyChanged(nameof(VoiceModelStatusColor));
        NotifyVoiceCommandsChanged();
    }

    private void NotifyVoiceStateChanged()
    {
        OnPropertyChanged(nameof(VoiceInputButtonText));
        OnPropertyChanged(nameof(VoiceInputButtonIcon));
        OnPropertyChanged(nameof(IsVoiceInputBusy));
        OnPropertyChanged(nameof(IsVoiceCancelVisible));
        NotifyVoiceCommandsChanged();
        DownloadWakeWordModelCommand.NotifyCanExecuteChanged();
    }

    private void NotifyVoiceCommandsChanged()
    {
        DownloadVoiceModelCommand.NotifyCanExecuteChanged();
        SelectCustomVoiceModelCommand.NotifyCanExecuteChanged();
    }

    private static string NormalizeVoiceLanguage(string? language)
    {
        return language?.Trim().ToLowerInvariant() switch
        {
            "zh" => "zh",
            "en" => "en",
            _ => "auto"
        };
    }

    private static IStorageProvider? GetActiveStorageProvider()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            return null;
        }

        return desktop.Windows.FirstOrDefault(window => window.IsActive)?.StorageProvider ??
               desktop.MainWindow?.StorageProvider;
    }
}
