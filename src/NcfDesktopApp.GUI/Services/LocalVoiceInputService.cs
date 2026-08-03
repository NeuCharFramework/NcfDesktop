/*----------------------------------------------------------------
    Copyright (C) 2026 Senparc

    文件名：LocalVoiceInputService.cs
    文件功能描述：跨平台麦克风录音、Whisper 模型下载与本地转写

    创建标识：Senparc - 20260801
----------------------------------------------------------------*/

using System;
using System.Buffers;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NcfDesktopApp.GUI.Models;
using SoundFlow.Abstracts.Devices;
using SoundFlow.Backends.MiniAudio;
using SoundFlow.Components;
using SoundFlow.Enums;
using SoundFlow.Structs;
using Whisper.net;
using Whisper.net.Ggml;

namespace NcfDesktopApp.GUI.Services;

internal interface ILocalVoiceInputService
{
    event Action<AudioVisualizationFrame>? VisualizationFrameAvailable;

    Guid? RecordingOwner { get; }

    Task DownloadModelAsync(
        VoiceModelOption option,
        Action<long>? progress,
        CancellationToken cancellationToken);

    Task StartRecordingAsync(
        Guid owner,
        string modelPath,
        CancellationToken cancellationToken);

    Task<string> StopAndTranscribeAsync(
        Guid owner,
        string language,
        CancellationToken cancellationToken);

    Task CancelRecordingAsync(Guid owner);
}

internal sealed class LocalVoiceInputService : ILocalVoiceInputService, IDisposable
{
    private const int MaximumRecordingSeconds = 5 * 60;
    private const int MaximumRecordingSampleCount = VoiceAudioAnalysis.SampleRate * MaximumRecordingSeconds;
    private static readonly Lazy<LocalVoiceInputService> SharedInstance = new(() => new LocalVoiceInputService());
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly object _stateLock = new();
    private readonly object _sampleLock = new();

    private MiniAudioEngine? _audioEngine;
    private AudioCaptureDevice? _captureDevice;
    private Recorder? _recorder;
    private ArrayBufferWriter<float>? _recordingSamples;
    private Exception? _captureFailure;
    private bool _recordingLimitReached;
    private Guid? _recordingOwner;
    private string _recordingModelPath = string.Empty;
    private string _recordingDeviceName = string.Empty;
    private WhisperFactory? _loadedFactory;
    private string _loadedModelPath = string.Empty;
    private long _loadedModelLength;
    private DateTime _loadedModelWriteTimeUtc;
    private VoiceOperationStage _operationStage;
    private long _lastVisualizationTick;
    private bool _disposed;

    public static LocalVoiceInputService Shared => SharedInstance.Value;

    public event Action<AudioVisualizationFrame>? VisualizationFrameAvailable;

    public static void DisposeShared()
    {
        if (SharedInstance.IsValueCreated)
        {
            SharedInstance.Value.Dispose();
        }
    }

    public Guid? RecordingOwner
    {
        get
        {
            lock (_stateLock)
            {
                return _recordingOwner;
            }
        }
    }

    public async Task DownloadModelAsync(
        VoiceModelOption option,
        Action<long>? progress,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!option.CanDownload)
        {
            throw new InvalidOperationException("手动模型不能自动下载，请选择本地 GGML 文件。");
        }

        if (!await _operationGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("麦克风或语音模型正在被另一个工作台使用，请稍后重试。");
        }

        string? temporaryPath = null;
        try
        {
            Directory.CreateDirectory(VoiceModelCatalog.ModelsDirectory);
            var targetPath = VoiceModelCatalog.GetModelPath(option, null);
            temporaryPath = $"{targetPath}.download";
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }

            using var modelStream = await WhisperGgmlDownloader.Default
                .GetGgmlModelAsync(VoiceModelCatalog.GetGgmlType(option), cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            await using var output = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                1024 * 128,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            var buffer = new byte[1024 * 128];
            long downloaded = 0;
            int read;
            while ((read = await modelStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                downloaded += read;
                progress?.Invoke(downloaded);
            }

            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            var readiness = VoiceModelCatalog.EvaluateFile(option, temporaryPath);
            if (!readiness.IsReady)
            {
                throw new InvalidDataException(readiness.Message);
            }

            File.Move(temporaryPath, targetPath, overwrite: true);
        }
        finally
        {
            try
            {
                if (!string.IsNullOrEmpty(temporaryPath) && File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            finally
            {
                _operationGate.Release();
            }
        }
    }

    public async Task StartRecordingAsync(
        Guid owner,
        string modelPath,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath))
        {
            throw new FileNotFoundException("语音模型尚未准备好。", modelPath);
        }

        if (!await _operationGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("麦克风或语音模型正在被另一个工作台使用，请稍后重试。");
        }

        try
        {
            _audioEngine ??= new MiniAudioEngine();
            _audioEngine.UpdateAudioDevicesInfo();
            var device = _audioEngine.CaptureDevices.FirstOrDefault(item => item.IsDefault);
            if (string.IsNullOrWhiteSpace(device.Name))
            {
                device = _audioEngine.CaptureDevices.FirstOrDefault();
            }

            if (string.IsNullOrWhiteSpace(device.Name))
            {
                throw new InvalidOperationException("未检测到可用的麦克风，请检查系统录音设备和权限。");
            }

            var format = new AudioFormat
            {
                SampleRate = VoiceAudioAnalysis.SampleRate,
                Channels = 1,
                // SoundFlow 的录音回调统一提供 float；直接使用 F32 可避免把 float 内存按 S16 编码。
                Format = SampleFormat.F32,
                Layout = ChannelLayout.Mono
            };
            PrepareSampleBuffer();
            _captureDevice = _audioEngine.InitializeCaptureDevice(device, format);
            _recorder = new Recorder(_captureDevice, CaptureAudioSamples);

            _captureDevice.Start();
            var result = _recorder.StartRecording();
            if (!result.IsSuccess)
            {
                throw new InvalidOperationException(result.Error?.Message ?? "无法启动录音。");
            }

            lock (_stateLock)
            {
                _recordingOwner = owner;
                _recordingModelPath = modelPath;
                _recordingDeviceName = device.Name;
                _operationStage = VoiceOperationStage.Recording;
            }
        }
        catch
        {
            CleanupRecordingResources(discardAudio: true);
            _operationGate.Release();
            throw;
        }
    }

    public async Task<string> StopAndTranscribeAsync(
        Guid owner,
        string language,
        CancellationToken cancellationToken)
    {
        BeginTranscription(owner);
        CapturedAudio capturedAudio;
        string modelPath;
        string deviceName;
        try
        {
            var stopResult = await _recorder!.StopRecordingAsync().ConfigureAwait(false);
            if (!stopResult.IsSuccess)
            {
                throw new InvalidOperationException(stopResult.Error?.Message ?? "停止录音失败。");
            }

            _captureDevice?.Stop();
            capturedAudio = TakeCapturedAudio();
            lock (_stateLock)
            {
                modelPath = _recordingModelPath;
                deviceName = _recordingDeviceName;
            }
            CleanupRecordingResources(discardAudio: true);

            if (capturedAudio.Failure != null)
            {
                throw new InvalidOperationException(
                    $"麦克风采集失败：{capturedAudio.Failure.Message}",
                    capturedAudio.Failure);
            }

            if (capturedAudio.LimitReached)
            {
                throw new InvalidOperationException($"单次录音不能超过 {MaximumRecordingSeconds / 60} 分钟，请缩短后重试。");
            }

            var metrics = VoiceAudioAnalysis.Analyze(capturedAudio.Samples);
            if (metrics.SampleCount < VoiceAudioAnalysis.MinimumSampleCount)
            {
                throw new InvalidOperationException("录音时间过短，请至少录入 0.25 秒。");
            }

            if (!VoiceAudioAnalysis.HasUsableSignal(metrics))
            {
                throw new InvalidOperationException(
                    $"未检测到有效的麦克风声音（设备：{deviceName}，峰值：{FormatDbfs(metrics.PeakDbfs)} dBFS，" +
                    $"RMS：{FormatDbfs(metrics.RootMeanSquareDbfs)} dBFS）。请检查麦克风权限、默认输入设备和输入音量。");
            }

            var factory = GetOrLoadFactory(modelPath);
            using var processor = factory.CreateBuilder()
                .WithLanguage(NormalizeLanguage(language))
                .WithThreads(Math.Clamp(Environment.ProcessorCount / 2, 1, 4))
                .Build();
            var transcript = new StringBuilder();
            await foreach (var segment in processor.ProcessAsync(capturedAudio.Samples, cancellationToken))
            {
                transcript.Append(segment.Text);
            }

            var text = transcript.ToString().Trim();
            if (text.Length == 0)
            {
                throw new InvalidOperationException("未识别到有效语音，请靠近麦克风后重试。");
            }

            if (VoiceAudioAnalysis.IsOnlyNonSpeechAnnotation(text))
            {
                throw new InvalidOperationException(
                    "录音中没有识别到可用的人声内容，模型只返回了声音或静音标记。请检查麦克风设备后重试。");
            }

            return text;
        }
        finally
        {
            CleanupRecordingResources(discardAudio: true);
            ClearRecordingState();
            _operationGate.Release();
        }
    }

    public async Task CancelRecordingAsync(Guid owner)
    {
        lock (_stateLock)
        {
            if (_recordingOwner == null)
            {
                return;
            }

            if (_recordingOwner != owner)
            {
                throw new InvalidOperationException("麦克风正在被另一个工作台使用。");
            }

            // 转写阶段由其 CancellationToken 负责取消；不得在这里再次释放操作锁。
            if (_operationStage != VoiceOperationStage.Recording)
            {
                return;
            }

            _operationStage = VoiceOperationStage.Cancelling;
        }

        try
        {
            if (_recorder != null)
            {
                await _recorder.StopRecordingAsync().ConfigureAwait(false);
            }

            _captureDevice?.Stop();
        }
        finally
        {
            CleanupRecordingResources(discardAudio: true);
            ClearRecordingState();
            _operationGate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _recorder?.Dispose();
        }
        catch
        {
            // 应用退出时不得因音频后端清理失败阻止进程结束。
        }

        CleanupRecordingResources(discardAudio: true);
        _loadedFactory?.Dispose();
        _loadedFactory = null;
        _audioEngine?.Dispose();
        _audioEngine = null;
        _operationGate.Dispose();
    }

    private WhisperFactory GetOrLoadFactory(string modelPath)
    {
        var file = new FileInfo(modelPath);
        if (_loadedFactory != null &&
            string.Equals(_loadedModelPath, modelPath, StringComparison.OrdinalIgnoreCase) &&
            _loadedModelLength == file.Length &&
            _loadedModelWriteTimeUtc == file.LastWriteTimeUtc)
        {
            return _loadedFactory;
        }

        _loadedFactory?.Dispose();
        _loadedFactory = WhisperFactory.FromPath(modelPath);
        _loadedModelPath = modelPath;
        _loadedModelLength = file.Length;
        _loadedModelWriteTimeUtc = file.LastWriteTimeUtc;
        return _loadedFactory;
    }

    private static string NormalizeLanguage(string language)
    {
        return language?.Trim().ToLowerInvariant() switch
        {
            "zh" => "zh",
            "en" => "en",
            _ => "auto"
        };
    }

    private void BeginTranscription(Guid owner)
    {
        lock (_stateLock)
        {
            if (_recordingOwner == null ||
                _operationStage != VoiceOperationStage.Recording ||
                _recorder == null)
            {
                throw new InvalidOperationException("当前没有正在进行的录音。");
            }

            if (_recordingOwner != owner)
            {
                throw new InvalidOperationException("麦克风正在被另一个工作台使用。");
            }

            _operationStage = VoiceOperationStage.Transcribing;
        }
    }

    private void CleanupRecordingResources(bool discardAudio)
    {
        try
        {
            _recorder?.Dispose();
        }
        catch
        {
            // 后续清理仍需继续。
        }
        _recorder = null;

        try
        {
            _captureDevice?.Dispose();
        }
        catch
        {
            // 后续清理仍需继续。
        }
        _captureDevice = null;

        if (discardAudio)
        {
            ClearCapturedAudio();
        }

        PublishVisualization(AudioVisualizationFrame.Silent);
    }

    private void ClearRecordingState()
    {
        lock (_stateLock)
        {
            _recordingOwner = null;
            _recordingModelPath = string.Empty;
            _recordingDeviceName = string.Empty;
            _operationStage = VoiceOperationStage.None;
        }
    }

    private void PrepareSampleBuffer()
    {
        lock (_sampleLock)
        {
            _recordingSamples = new ArrayBufferWriter<float>(VoiceAudioAnalysis.SampleRate * 10);
            _captureFailure = null;
            _recordingLimitReached = false;
        }
    }

    private void CaptureAudioSamples(Span<float> samples, Capability capability)
    {
        if (capability != Capability.Record)
        {
            return;
        }

        try
        {
            var capturedCount = 0;
            lock (_sampleLock)
            {
                if (_recordingSamples == null || _captureFailure != null)
                {
                    return;
                }

                var remaining = MaximumRecordingSampleCount - _recordingSamples.WrittenCount;
                if (remaining <= 0)
                {
                    _recordingLimitReached = true;
                    return;
                }

                var count = Math.Min(samples.Length, remaining);
                var destination = _recordingSamples.GetSpan(count);
                for (var index = 0; index < count; index++)
                {
                    var sample = samples[index];
                    destination[index] = float.IsFinite(sample)
                        ? Math.Clamp(sample, -1f, 1f)
                        : 0f;
                }
                _recordingSamples.Advance(count);
                capturedCount = count;

                if (count < samples.Length)
                {
                    _recordingLimitReached = true;
                }
            }

            var now = Environment.TickCount64;
            var previous = Interlocked.Read(ref _lastVisualizationTick);
            if (capturedCount > 0 && now - previous >= 45 &&
                Interlocked.CompareExchange(ref _lastVisualizationTick, now, previous) == previous)
            {
                PublishVisualization(AudioSpectrumAnalysis.Analyze(
                    samples[..capturedCount],
                    VoiceAudioAnalysis.SampleRate));
            }
        }
        catch (Exception ex)
        {
            lock (_sampleLock)
            {
                _captureFailure ??= ex;
            }
        }
    }

    private CapturedAudio TakeCapturedAudio()
    {
        lock (_sampleLock)
        {
            var samples = _recordingSamples?.WrittenSpan.ToArray() ?? Array.Empty<float>();
            var result = new CapturedAudio(samples, _captureFailure, _recordingLimitReached);
            _recordingSamples = null;
            _captureFailure = null;
            _recordingLimitReached = false;
            return result;
        }
    }

    private void ClearCapturedAudio()
    {
        lock (_sampleLock)
        {
            _recordingSamples = null;
            _captureFailure = null;
            _recordingLimitReached = false;
        }
    }

    private static string FormatDbfs(double value)
    {
        return double.IsNegativeInfinity(value) ? "-∞" : value.ToString("F1");
    }

    private void PublishVisualization(AudioVisualizationFrame frame)
    {
        try
        {
            VisualizationFrameAvailable?.Invoke(frame);
        }
        catch
        {
            // UI 可视化订阅者不得中断实时录音回调。
        }
    }

    private sealed record CapturedAudio(float[] Samples, Exception? Failure, bool LimitReached);

    private enum VoiceOperationStage
    {
        None,
        Recording,
        Transcribing,
        Cancelling
    }
}
