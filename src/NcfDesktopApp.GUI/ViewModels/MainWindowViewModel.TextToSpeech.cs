/*----------------------------------------------------------------
    Copyright (C) 2026 Senparc

    文件名：MainWindowViewModel.TextToSpeech.cs
    文件功能描述：本地 TTS 配置、AdminChat 逐条朗读与共享音频可视化状态

    创建标识：Senparc - 20260803
----------------------------------------------------------------*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NcfDesktopApp.GUI.Models;
using NcfDesktopApp.GUI.Services;

namespace NcfDesktopApp.GUI.ViewModels;

public partial class MainWindowViewModel
{
    private readonly ILocalTextToSpeechService _ttsService = LocalTextToSpeechService.Shared;
    private CancellationTokenSource? _ttsPlaybackCts;
    private CancellationTokenSource? _ttsModelDownloadCts;
    private int? _speakingMessageId;
    private bool _audioServicesInitialized;
    private readonly object _streamingTtsLock = new();
    private StreamingTtsSessionState? _streamingTtsSession;
    private int _streamingTtsHandledSessionId;

    [ObservableProperty]
    private TtsModelOption? _selectedTtsModel;

    [ObservableProperty]
    private string _ttsCustomModelPath = string.Empty;

    [ObservableProperty]
    private TtsVoiceOption? _selectedTtsVoice = TtsModelCatalog.Voices[0];

    [ObservableProperty]
    private double _ttsSpeed = 1.0;

    [ObservableProperty]
    private bool _ttsAutoRead;

    [ObservableProperty]
    private string _ttsModelStatusText = "尚未选择朗读模型。";

    [ObservableProperty]
    private string _ttsModelPathText = "—";

    [ObservableProperty]
    private string _ttsDownloadProgressText = string.Empty;

    [ObservableProperty]
    private string _ttsPlaybackStatusText = "可在每条 AI 回复右侧点击“朗读”；所有生成和播放均在本机完成。";

    [ObservableProperty]
    private bool _isTtsModelReady;

    [ObservableProperty]
    private bool _isTtsModelBusy;

    [ObservableProperty]
    private bool _isTtsPlaying;

    [ObservableProperty]
    private AudioVisualizationMode _audioVisualizationMode;

    [ObservableProperty]
    private double _audioVisualizationLevel;

    [ObservableProperty]
    private double[] _audioVisualizationBands = new double[12];

    public IReadOnlyList<TtsModelOption> TtsModelOptions => TtsModelCatalog.Options;

    public IReadOnlyList<TtsVoiceOption> TtsVoiceOptions => TtsModelCatalog.Voices;

    public string TtsModelStatusColor => IsTtsModelReady ? "#16A34A" : "#D97706";

    public bool IsAudioVisualizationActive => AudioVisualizationMode != AudioVisualizationMode.None;

    public string AdminChatAudioStatusText => IsTtsPlaying ? TtsPlaybackStatusText : VoiceInputStatusText;

    partial void OnSelectedTtsModelChanged(TtsModelOption? value)
    {
        StopTtsPlayback();
        RefreshTtsModelReadiness();
        NotifyTtsCommandsChanged();
        if (!_suppressDesktopSettingsSave)
        {
            SaveDesktopSettings();
        }
    }

    partial void OnTtsCustomModelPathChanged(string value)
    {
        RefreshTtsModelReadiness();
        NotifyTtsCommandsChanged();
        if (!_suppressDesktopSettingsSave)
        {
            SaveDesktopSettings();
        }
    }

    partial void OnSelectedTtsVoiceChanged(TtsVoiceOption? value)
    {
        if (!_suppressDesktopSettingsSave)
        {
            SaveDesktopSettings();
        }
    }

    partial void OnTtsSpeedChanged(double value)
    {
        var normalized = Math.Clamp(value, 0.5, 2.0);
        if (Math.Abs(normalized - value) > 0.001)
        {
            TtsSpeed = normalized;
            return;
        }

        if (!_suppressDesktopSettingsSave)
        {
            SaveDesktopSettings();
        }
    }

    partial void OnTtsAutoReadChanged(bool value)
    {
        if (!_suppressDesktopSettingsSave)
        {
            SaveDesktopSettings();
        }
    }

    partial void OnIsTtsModelReadyChanged(bool value)
    {
        OnPropertyChanged(nameof(TtsModelStatusColor));
        NotifyTtsCommandsChanged();
    }

    partial void OnIsTtsModelBusyChanged(bool value) => NotifyTtsCommandsChanged();

    partial void OnIsTtsPlayingChanged(bool value)
    {
        OnPropertyChanged(nameof(AdminChatAudioStatusText));
        NotifyTtsCommandsChanged();
        DownloadWakeWordModelCommand.NotifyCanExecuteChanged();
        ScheduleWakeWordListeningRefresh();
    }

    partial void OnTtsPlaybackStatusTextChanged(string value) =>
        OnPropertyChanged(nameof(AdminChatAudioStatusText));

    partial void OnVoiceInputStatusTextChanged(string value) =>
        OnPropertyChanged(nameof(AdminChatAudioStatusText));

    partial void OnAudioVisualizationModeChanged(AudioVisualizationMode value) =>
        OnPropertyChanged(nameof(IsAudioVisualizationActive));

    private void InitializeAudioServices()
    {
        if (_audioServicesInitialized)
        {
            return;
        }

        _audioServicesInitialized = true;
        InitializeWakeWordServiceForWorkspace();
        _voiceInputService.VisualizationFrameAvailable += OnVoiceVisualizationFrame;
        _ttsService.VisualizationFrameAvailable += OnTtsVisualizationFrame;
    }

    [RelayCommand(CanExecute = nameof(CanDownloadTtsModel))]
    private async Task DownloadTtsModel()
    {
        var option = SelectedTtsModel;
        if (option is not { CanDownload: true })
        {
            TtsModelStatusText = "请先选择可下载的内置朗读模型。";
            return;
        }

        var readiness = TtsModelCatalog.Evaluate(option, TtsCustomModelPath);
        if (readiness.IsReady)
        {
            ApplyTtsModelReadiness(readiness);
            return;
        }

        _ttsModelDownloadCts?.Cancel();
        _ttsModelDownloadCts?.Dispose();
        _ttsModelDownloadCts = new CancellationTokenSource();
        IsTtsModelBusy = true;
        TtsDownloadProgressText = $"正在下载 {option.DisplayName}…";
        long lastReportedBytes = 0;
        try
        {
            await _ttsService.DownloadModelAsync(
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
                        TtsDownloadProgressText =
                            $"已下载 {VoiceModelCatalog.FormatBytes(bytes)} / {option.ApproximateSizeText}（约 {percent:F0}%）";
                    });
                },
                _ttsModelDownloadCts.Token);
            RefreshTtsModelReadiness();
            TtsDownloadProgressText = "下载和解压完成，可离线朗读。";
            AddLog($"✅ 本地朗读模型已就绪: {TtsModelPathText}");
        }
        catch (OperationCanceledException)
        {
            TtsModelStatusText = "朗读模型下载已取消，可稍后继续。";
            TtsDownloadProgressText = string.Empty;
        }
        catch (Exception ex)
        {
            TtsModelStatusText = $"朗读模型下载失败：{ex.Message}";
            TtsDownloadProgressText = string.Empty;
            AddLog($"❌ 朗读模型下载失败: {ex.Message}");
        }
        finally
        {
            IsTtsModelBusy = false;
            _ttsModelDownloadCts?.Dispose();
            _ttsModelDownloadCts = null;
        }
    }

    private bool CanDownloadTtsModel() =>
        SelectedTtsModel is { CanDownload: true } &&
        !IsTtsModelReady &&
        !IsTtsModelBusy &&
        !IsTtsPlaying;

    [RelayCommand(CanExecute = nameof(CanSelectCustomTtsModel))]
    private async Task SelectCustomTtsModel()
    {
        try
        {
            var storageProvider = GetActiveStorageProvider();
            if (storageProvider is not { CanOpen: true })
            {
                TtsModelStatusText = "当前平台无法打开模型目录选择器。";
                return;
            }

            var folders = await storageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "选择已解压的 sherpa-onnx Kokoro 模型目录",
                AllowMultiple = false
            });
            var path = folders.FirstOrDefault()?.TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            _suppressDesktopSettingsSave = true;
            TtsCustomModelPath = path;
            SelectedTtsModel = TtsModelCatalog.FindById(TtsModelCatalog.CustomModelId);
            _suppressDesktopSettingsSave = false;
            RefreshTtsModelReadiness();
            SaveDesktopSettings();
        }
        catch (Exception ex)
        {
            TtsModelStatusText = $"选择朗读模型失败：{ex.Message}";
        }
    }

    private bool CanSelectCustomTtsModel() => !IsTtsModelBusy && !IsTtsPlaying;

    [RelayCommand]
    private void OpenTtsModelDirectory()
    {
        Directory.CreateDirectory(TtsModelCatalog.ModelsDirectory);
        OpenBrowser(TtsModelCatalog.ModelsDirectory);
    }

    [RelayCommand]
    private void CancelTtsOperation()
    {
        _ttsModelDownloadCts?.Cancel();
        StopTtsPlayback();
        TtsPlaybackStatusText = "朗读已停止。";
    }

    [RelayCommand]
    private async Task PreviewTtsVoice()
    {
        await PlayTtsTextAsync(
            -2,
            "你好，我是 NCF 桌面助手。这段语音完全由本地模型生成。 Hello, this voice is generated offline.");
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task ToggleAdminChatSpeech(AdminChatMessage? message)
    {
        if (message == null || !message.IsAgent)
        {
            return;
        }

        if (IsTtsPlaying && _speakingMessageId == message.Id)
        {
            StopTtsPlayback();
            return;
        }

        await PlayTtsTextAsync(message.Id, message.Content);
    }

    internal Task TryAutoReadAssistantMessageAsync(AdminChatMessage message)
    {
        if (!TtsAutoRead || !message.IsAgent)
        {
            return Task.CompletedTask;
        }

        return PlayTtsTextAsync(message.Id, message.Content);
    }

    internal bool BeginStreamingAutoRead(int sessionId)
    {
        _streamingTtsHandledSessionId = 0;
        if (!TtsAutoRead || sessionId <= 0 || IsVoiceRecording || IsVoiceTranscribing)
        {
            return false;
        }

        var readiness = TtsModelCatalog.Evaluate(SelectedTtsModel, TtsCustomModelPath);
        ApplyTtsModelReadiness(readiness);
        if (!readiness.IsReady || readiness.Files == null)
        {
            return false;
        }

        StopTtsPlayback();
        var cts = new CancellationTokenSource();
        var channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
        var state = new StreamingTtsSessionState(
            sessionId,
            new StreamingTtsTextBuffer(),
            channel,
            readiness.Files,
            SelectedTtsVoice?.SpeakerId ?? 45,
            (float)TtsSpeed,
            cts);
        lock (_streamingTtsLock)
        {
            _streamingTtsSession = state;
            _streamingTtsHandledSessionId = sessionId;
        }

        _ttsPlaybackCts = cts;
        return true;
    }

    internal void AppendStreamingAutoReadChunk(int sessionId, string chunk)
    {
        StreamingTtsSessionState? stateToStart = null;
        lock (_streamingTtsLock)
        {
            var state = _streamingTtsSession;
            if (state == null || state.SessionId != sessionId || state.Cancellation.IsCancellationRequested)
            {
                return;
            }

            QueueStreamingSpeechChunks(state, state.TextBuffer.Append(chunk));
            if (state.HasQueuedSpeech && !state.PlaybackScheduled)
            {
                state.PlaybackScheduled = true;
                stateToStart = state;
            }
        }

        if (stateToStart != null)
        {
            Dispatcher.UIThread.Post(() => _ = RunStreamingTtsPlaybackAsync(stateToStart));
        }
    }

    internal bool CompleteStreamingAutoRead(AdminChatMessage message)
    {
        StreamingTtsSessionState? stateToStart = null;
        StreamingTtsSessionState? stateToDispose = null;
        bool handled;
        lock (_streamingTtsLock)
        {
            var state = _streamingTtsSession;
            if (state == null || state.SessionId != message.SessionId)
            {
                handled = _streamingTtsHandledSessionId == message.SessionId;
                if (handled)
                {
                    _streamingTtsHandledSessionId = 0;
                }

                return handled;
            }

            handled = true;
            state.FinalMessageId = message.Id;
            QueueStreamingSpeechChunks(state, state.TextBuffer.Complete(message.Content));
            state.Channel.Writer.TryComplete();
            if (state.HasQueuedSpeech && !state.PlaybackScheduled)
            {
                state.PlaybackScheduled = true;
                stateToStart = state;
            }
            else if (!state.HasQueuedSpeech)
            {
                _streamingTtsSession = null;
                stateToDispose = state;
            }

            _streamingTtsHandledSessionId = 0;
        }

        if (stateToDispose != null)
        {
            if (ReferenceEquals(_ttsPlaybackCts, stateToDispose.Cancellation))
            {
                _ttsPlaybackCts = null;
            }

            stateToDispose.Cancellation.Dispose();
        }

        if (stateToStart != null)
        {
            Dispatcher.UIThread.Post(() => _ = RunStreamingTtsPlaybackAsync(stateToStart));
        }
        else if (IsTtsPlaying)
        {
            _speakingMessageId = message.Id;
        }

        return handled;
    }

    private static void QueueStreamingSpeechChunks(
        StreamingTtsSessionState state,
        IReadOnlyList<string> chunks)
    {
        foreach (var speechChunk in chunks)
        {
            if (state.Channel.Writer.TryWrite(speechChunk))
            {
                state.HasQueuedSpeech = true;
            }
        }
    }

    private async Task RunStreamingTtsPlaybackAsync(StreamingTtsSessionState state)
    {
        lock (_streamingTtsLock)
        {
            if (!ReferenceEquals(_streamingTtsSession, state) || state.Cancellation.IsCancellationRequested)
            {
                state.Cancellation.Dispose();
                return;
            }

        }

        try
        {
            await StopWakeWordListeningForOperationAsync().ConfigureAwait(true);
            state.Cancellation.Token.ThrowIfCancellationRequested();
            _speakingMessageId = state.FinalMessageId;
            IsTtsPlaying = true;
            TtsPlaybackStatusText = "正在边接收回答边本机朗读…";
            Robot.SetSpeechState("正在流式朗读", "AI 回复正在生成，并通过本地 Kokoro 模型连续朗读");

            await _ttsService.PlayStreamingAsync(
                state.Files,
                state.Channel.Reader,
                state.SpeakerId,
                state.Speed,
                state.Cancellation.Token);
            if (!state.Cancellation.IsCancellationRequested && ReferenceEquals(_ttsPlaybackCts, state.Cancellation))
            {
                TtsPlaybackStatusText = "朗读完成。";
                Robot.SetSpeechState("朗读完成", "AI 回复已在本机播放完毕");
            }
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(_ttsPlaybackCts, state.Cancellation))
            {
                TtsPlaybackStatusText = "朗读已停止。";
            }
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(_ttsPlaybackCts, state.Cancellation))
            {
                TtsPlaybackStatusText = $"朗读失败：{ex.Message}";
                Robot.SetSpeechState("朗读失败", ex.Message, isError: true);
                AddLog($"❌ 本地流式朗读失败: {ex.Message}");
            }
        }
        finally
        {
            lock (_streamingTtsLock)
            {
                if (ReferenceEquals(_streamingTtsSession, state))
                {
                    _streamingTtsSession = null;
                }
            }

            if (ReferenceEquals(_ttsPlaybackCts, state.Cancellation))
            {
                _ttsPlaybackCts = null;
                _speakingMessageId = null;
                IsTtsPlaying = false;
            }

            state.Cancellation.Dispose();
            ScheduleWakeWordListeningRefresh();
        }
    }

    private async Task PlayTtsTextAsync(int messageId, string text)
    {
        var readiness = TtsModelCatalog.Evaluate(SelectedTtsModel, TtsCustomModelPath);
        ApplyTtsModelReadiness(readiness);
        if (!readiness.IsReady || readiness.Files == null)
        {
            TtsPlaybackStatusText = readiness.Message;
            ShowWorkspaceSettingsRequested?.Invoke();
            return;
        }

        if (IsVoiceRecording || IsVoiceTranscribing)
        {
            TtsPlaybackStatusText = "请先结束语音输入或转写，再开始朗读。";
            return;
        }

        await StopWakeWordListeningForOperationAsync().ConfigureAwait(true);
        StopTtsPlayback();
        var cts = new CancellationTokenSource();
        _ttsPlaybackCts = cts;
        _speakingMessageId = messageId;
        IsTtsPlaying = true;
        TtsPlaybackStatusText = "正在本机生成并播放语音…";
        Robot.SetSpeechState("正在朗读", "AI 回复正在通过本地 Kokoro 模型朗读");
        try
        {
            await _ttsService.PlayAsync(
                readiness.Files,
                text,
                SelectedTtsVoice?.SpeakerId ?? 45,
                (float)TtsSpeed,
                cts.Token);
            if (!cts.IsCancellationRequested && ReferenceEquals(_ttsPlaybackCts, cts))
            {
                TtsPlaybackStatusText = "朗读完成。";
                Robot.SetSpeechState("朗读完成", "AI 回复已在本机播放完毕");
            }
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(_ttsPlaybackCts, cts))
            {
                TtsPlaybackStatusText = "朗读已停止。";
            }
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(_ttsPlaybackCts, cts))
            {
                TtsPlaybackStatusText = $"朗读失败：{ex.Message}";
                Robot.SetSpeechState("朗读失败", ex.Message, isError: true);
                AddLog($"❌ 本地朗读失败: {ex.Message}");
            }
        }
        finally
        {
            if (ReferenceEquals(_ttsPlaybackCts, cts))
            {
                _ttsPlaybackCts = null;
                _speakingMessageId = null;
                IsTtsPlaying = false;
            }
            cts.Dispose();
            ScheduleWakeWordListeningRefresh();
        }
    }

    private void StopTtsPlayback()
    {
        CancelStreamingTtsSession();
        _ttsPlaybackCts?.Cancel();
        _ttsService.Stop();
        _speakingMessageId = null;
        IsTtsPlaying = false;
        if (!IsVoiceRecording)
        {
            ApplyAudioVisualization(AudioVisualizationMode.None, AudioVisualizationFrame.Silent);
        }
    }

    internal void CancelActiveStreamingAutoRead()
    {
        bool hasActiveStream;
        lock (_streamingTtsLock)
        {
            hasActiveStream = _streamingTtsSession != null;
            _streamingTtsHandledSessionId = 0;
        }

        if (hasActiveStream)
        {
            StopTtsPlayback();
        }
    }

    private void CancelStreamingTtsSession()
    {
        StreamingTtsSessionState? state;
        lock (_streamingTtsLock)
        {
            state = _streamingTtsSession;
            _streamingTtsSession = null;
        }

        if (state == null)
        {
            return;
        }

        state.Channel.Writer.TryComplete();
        state.Cancellation.Cancel();
        if (ReferenceEquals(_ttsPlaybackCts, state.Cancellation))
        {
            _ttsPlaybackCts = null;
        }

        if (!state.PlaybackScheduled)
        {
            state.Cancellation.Dispose();
        }
    }

    internal void RefreshTtsModelReadiness() =>
        ApplyTtsModelReadiness(TtsModelCatalog.Evaluate(SelectedTtsModel, TtsCustomModelPath));

    private void ApplyTtsModelReadiness(TtsModelReadiness readiness)
    {
        IsTtsModelReady = readiness.IsReady;
        TtsModelPathText = string.IsNullOrWhiteSpace(readiness.ModelDirectory)
            ? "—"
            : readiness.ModelDirectory;
        TtsModelStatusText = readiness.Message;
        OnPropertyChanged(nameof(TtsModelStatusColor));
        NotifyTtsCommandsChanged();
    }

    private void NotifyTtsCommandsChanged()
    {
        DownloadTtsModelCommand.NotifyCanExecuteChanged();
        SelectCustomTtsModelCommand.NotifyCanExecuteChanged();
    }

    private void OnVoiceVisualizationFrame(AudioVisualizationFrame frame)
    {
        var mode = IsVoiceRecording
            ? AudioVisualizationMode.Listening
            : AudioVisualizationMode.None;
        ApplyAudioVisualization(mode, frame);
    }

    private void OnTtsVisualizationFrame(AudioVisualizationFrame frame)
    {
        var mode = IsTtsPlaying
            ? AudioVisualizationMode.Speaking
            : AudioVisualizationMode.None;
        ApplyAudioVisualization(mode, frame);
    }

    private void ApplyAudioVisualization(AudioVisualizationMode mode, AudioVisualizationFrame frame)
    {
        Dispatcher.UIThread.Post(() =>
        {
            AudioVisualizationMode = mode;
            AudioVisualizationLevel = Math.Clamp(frame.Level, 0, 1);
            AudioVisualizationBands = frame.Bands.Length == 0 ? new double[12] : frame.Bands.ToArray();
            Robot.SetAudioVisualization(mode, frame);
        });
    }

    private void DisposeAudioServicesForWorkspace()
    {
        if (!_audioServicesInitialized)
        {
            return;
        }

        if (IsTtsPlaying)
        {
            StopTtsPlayback();
        }
        _ttsModelDownloadCts?.Cancel();
        _voiceInputService.VisualizationFrameAvailable -= OnVoiceVisualizationFrame;
        _ttsService.VisualizationFrameAvailable -= OnTtsVisualizationFrame;
        _audioServicesInitialized = false;
    }

    private sealed class StreamingTtsSessionState(
        int sessionId,
        StreamingTtsTextBuffer textBuffer,
        Channel<string> channel,
        TtsModelFiles files,
        int speakerId,
        float speed,
        CancellationTokenSource cancellation)
    {
        public int SessionId { get; } = sessionId;

        public StreamingTtsTextBuffer TextBuffer { get; } = textBuffer;

        public Channel<string> Channel { get; } = channel;

        public TtsModelFiles Files { get; } = files;

        public int SpeakerId { get; } = speakerId;

        public float Speed { get; } = speed;

        public CancellationTokenSource Cancellation { get; } = cancellation;

        public bool HasQueuedSpeech { get; set; }

        public bool PlaybackScheduled { get; set; }

        public int? FinalMessageId { get; set; }
    }
}
