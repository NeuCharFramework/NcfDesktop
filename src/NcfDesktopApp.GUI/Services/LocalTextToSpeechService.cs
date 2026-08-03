/*----------------------------------------------------------------
    Copyright (C) 2026 Senparc

    文件名：LocalTextToSpeechService.cs
    文件功能描述：跨平台离线 Kokoro 文本转语音、模型下载解压与音频播放

    创建标识：Senparc - 20260803
----------------------------------------------------------------*/

using System;
using System.Buffers;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using NcfDesktopApp.GUI.Models;
using SharpCompress.Readers;
using SherpaOnnx;
using SoundFlow.Abstracts.Devices;
using SoundFlow.Backends.MiniAudio;
using SoundFlow.Components;
using SoundFlow.Enums;
using SoundFlow.Providers;
using SoundFlow.Structs;

namespace NcfDesktopApp.GUI.Services;

internal interface ILocalTextToSpeechService
{
    event Action<AudioVisualizationFrame>? VisualizationFrameAvailable;

    bool IsPlaying { get; }

    Task DownloadModelAsync(
        TtsModelOption option,
        Action<long>? progress,
        CancellationToken cancellationToken);

    Task PlayAsync(
        TtsModelFiles files,
        string text,
        int speakerId,
        float speed,
        CancellationToken cancellationToken);

    Task PlayStreamingAsync(
        TtsModelFiles files,
        ChannelReader<string> textChunks,
        int speakerId,
        float speed,
        CancellationToken cancellationToken);

    void Stop();
}

internal sealed class LocalTextToSpeechService : ILocalTextToSpeechService, IDisposable
{
    private static readonly Lazy<LocalTextToSpeechService> SharedInstance = new(() => new LocalTextToSpeechService());
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly object _playbackLock = new();
    private readonly HttpClient _httpClient = new(new HttpClientHandler { AllowAutoRedirect = true });

    private OfflineTts? _loadedTts;
    private string _loadedModelPath = string.Empty;
    private long _loadedModelLength;
    private DateTime _loadedModelWriteTimeUtc;
    private CancellationTokenSource? _activePlaybackCts;
    private MiniAudioEngine? _activeEngine;
    private AudioPlaybackDevice? _activeDevice;
    private SoundPlayer? _activePlayer;
    private StreamingAudioDataProvider? _activeProvider;
    private bool _disposed;

    public static LocalTextToSpeechService Shared => SharedInstance.Value;

    public static void DisposeShared()
    {
        if (SharedInstance.IsValueCreated)
        {
            SharedInstance.Value.Dispose();
        }
    }

    public event Action<AudioVisualizationFrame>? VisualizationFrameAvailable;

    public bool IsPlaying
    {
        get
        {
            lock (_playbackLock)
            {
                return _activePlaybackCts is { IsCancellationRequested: false };
            }
        }
    }

    public async Task DownloadModelAsync(
        TtsModelOption option,
        Action<long>? progress,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!option.CanDownload || string.IsNullOrWhiteSpace(option.DownloadUrl))
        {
            throw new InvalidOperationException("手动模型不能自动下载，请选择已经解压的本地模型目录。");
        }

        if (!await _operationGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("朗读模型正在下载、加载或播放，请稍后重试。");
        }

        Directory.CreateDirectory(TtsModelCatalog.ModelsDirectory);
        var archivePath = Path.Combine(TtsModelCatalog.ModelsDirectory, $".{option.ArchiveFileName}.download");
        var temporaryDirectory = Path.Combine(TtsModelCatalog.ModelsDirectory, $".{option.Id}.extract-{Environment.ProcessId}");
        var targetDirectory = TtsModelCatalog.GetModelDirectory(option, null);
        try
        {
            DeleteFileIfPresent(archivePath);
            DeleteDirectoryIfPresent(temporaryDirectory);

            using var response = await _httpClient.GetAsync(
                option.DownloadUrl,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var destination = new FileStream(
                             archivePath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             128 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[128 * 1024];
                long downloaded = 0;
                int count;
                while ((count = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                    downloaded += count;
                    progress?.Invoke(downloaded);
                }

                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            Directory.CreateDirectory(temporaryDirectory);
            await Task.Run(
                () => ExtractArchiveSafely(archivePath, temporaryDirectory, cancellationToken),
                cancellationToken).ConfigureAwait(false);
            var readiness = TtsModelCatalog.EvaluateDirectory(temporaryDirectory);
            if (!readiness.IsReady)
            {
                throw new InvalidDataException(readiness.Message);
            }

            Stop();
            UnloadModelIfPathIsUnder(targetDirectory);
            DeleteDirectoryIfPresent(targetDirectory);
            Directory.Move(temporaryDirectory, targetDirectory);
        }
        finally
        {
            DeleteFileIfPresent(archivePath);
            DeleteDirectoryIfPresent(temporaryDirectory);
            _operationGate.Release();
        }
    }

    public async Task PlayAsync(
        TtsModelFiles files,
        string text,
        int speakerId,
        float speed,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var chunks = TtsTextNormalizer.SplitForSpeech(text);
        if (chunks.Count == 0)
        {
            throw new InvalidOperationException("这条消息没有可朗读的文本内容。");
        }

        var channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true
        });
        foreach (var chunk in chunks)
        {
            channel.Writer.TryWrite(chunk);
        }

        channel.Writer.TryComplete();
        await PlayStreamingAsync(
            files,
            channel.Reader,
            speakerId,
            speed,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task PlayStreamingAsync(
        TtsModelFiles files,
        ChannelReader<string> textChunks,
        int speakerId,
        float speed,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        Stop();
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_playbackLock)
        {
            _activePlaybackCts = linkedCts;
        }

        try
        {
            await _operationGate.WaitAsync(linkedCts.Token).ConfigureAwait(false);

            try
            {
                var tts = await Task.Run(() => GetOrLoadTts(files), linkedCts.Token).ConfigureAwait(false);
                StreamingPlayback? playback = null;
                try
                {
                    await foreach (var chunk in textChunks.ReadAllAsync(linkedCts.Token).ConfigureAwait(false))
                    {
                        if (string.IsNullOrWhiteSpace(chunk))
                        {
                            continue;
                        }

                        playback ??= StartStreamingPlayback(tts.SampleRate, linkedCts.Token);
                        var activePlayback = playback;
                        await Task.Run(
                            () => GenerateAudioInto(
                                tts,
                                chunk,
                                speakerId,
                                speed,
                                activePlayback.Provider,
                                linkedCts.Token),
                            linkedCts.Token).ConfigureAwait(false);
                    }

                    if (playback == null)
                    {
                        throw new InvalidOperationException("这条消息没有可朗读的文本内容。");
                    }

                    // 末尾保留一小段静音，避免音频回调刚读完最后一帧时立即关闭设备而截断尾音。
                    playback.Provider.AddSamples(new float[Math.Max(1, tts.SampleRate / 8)]);
                    playback.Provider.CompleteAdding();
                    await playback.Completion.Task.WaitAsync(linkedCts.Token).ConfigureAwait(false);
                    await playback.VisualizationTask.ConfigureAwait(false);
                }
                finally
                {
                    StopPlaybackDevice();
                }
            }
            finally
            {
                _operationGate.Release();
            }
        }
        finally
        {
            if (CleanupActivePlayback(linkedCts))
            {
                PublishVisualization(AudioVisualizationFrame.Silent);
            }
        }
    }

    public void Stop()
    {
        CancellationTokenSource? cts;
        lock (_playbackLock)
        {
            cts = _activePlaybackCts;
        }

        cts?.Cancel();
        StopPlaybackDevice();
        PublishVisualization(AudioVisualizationFrame.Silent);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
        lock (_playbackLock)
        {
            _activePlaybackCts?.Dispose();
            _activePlaybackCts = null;
        }
        _loadedTts?.Dispose();
        _loadedTts = null;
        _httpClient.Dispose();
        _operationGate.Dispose();
    }

    private static void GenerateAudioInto(
        OfflineTts tts,
        string text,
        int speakerId,
        float speed,
        StreamingAudioDataProvider provider,
        CancellationToken cancellationToken)
    {
        var config = new OfflineTtsGenerationConfig
        {
            Sid = Math.Clamp(speakerId, 0, Math.Max(0, tts.NumSpeakers - 1)),
            Speed = Math.Clamp(speed, 0.5f, 2f),
            SilenceScale = 0.2f
        };
        Exception? callbackFailure = null;
        var callback = new OfflineTtsCallbackProgressWithArg((samples, count, _, _) =>
        {
            if (cancellationToken.IsCancellationRequested || callbackFailure != null)
            {
                return 0;
            }

            if (samples == IntPtr.Zero || count <= 0)
            {
                return 1;
            }

            var buffer = ArrayPool<float>.Shared.Rent(count);
            try
            {
                Marshal.Copy(samples, buffer, 0, count);
                provider.AddSamples(buffer.AsSpan(0, count));
                return cancellationToken.IsCancellationRequested ? 0 : 1;
            }
            catch (Exception ex)
            {
                callbackFailure = ex;
                return 0;
            }
            finally
            {
                ArrayPool<float>.Shared.Return(buffer);
            }
        });
        var audio = tts.GenerateWithConfig(text, config, callback);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (callbackFailure != null)
            {
                throw new InvalidOperationException("向音频播放队列写入本地朗读数据失败。", callbackFailure);
            }

            if (audio.NumSamples <= 0 || audio.SampleRate <= 0)
            {
                throw new InvalidOperationException("本地朗读模型没有生成有效音频。");
            }
        }
        finally
        {
            audio.Dispose();
        }
    }

    private StreamingPlayback StartStreamingPlayback(int sampleRate, CancellationToken cancellationToken)
    {
        if (sampleRate <= 0)
        {
            throw new InvalidOperationException("本地朗读模型返回了无效的采样率。");
        }

        var format = new AudioFormat
        {
            SampleRate = sampleRate,
            Channels = 1,
            Format = SampleFormat.F32,
            Layout = ChannelLayout.Mono
        };
        var engine = new MiniAudioEngine();
        engine.UpdateAudioDevicesInfo();
        var selectedDevice = engine.PlaybackDevices.FirstOrDefault(device => device.IsDefault);
        var device = engine.InitializePlaybackDevice(
            string.IsNullOrWhiteSpace(selectedDevice.Name) ? null : selectedDevice,
            format);
        var provider = new StreamingAudioDataProvider(format, sampleRate * 20);
        var player = new SoundPlayer(engine, format, provider);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.EndOfStreamReached += (_, _) => completion.TrySetResult();

        lock (_playbackLock)
        {
            _activeEngine = engine;
            _activeDevice = device;
            _activePlayer = player;
            _activeProvider = provider;
        }

        cancellationToken.Register(() =>
        {
            completion.TrySetCanceled(cancellationToken);
            StopPlaybackDevice();
        });

        device.MasterMixer.AddComponent(player);
        device.Start();
        player.Play();
        var visualization = PublishStreamingVisualizationAsync(
            provider,
            sampleRate,
            completion.Task,
            cancellationToken);
        return new StreamingPlayback(provider, completion, visualization);
    }

    private async Task PublishStreamingVisualizationAsync(
        StreamingAudioDataProvider provider,
        int sampleRate,
        Task playbackCompletion,
        CancellationToken cancellationToken)
    {
        var windowSize = Math.Clamp(sampleRate / 20, 256, 4096);
        var samples = new float[windowSize];
        long observedVersion = 0;
        while (!cancellationToken.IsCancellationRequested && !playbackCompletion.IsCompleted)
        {
            if (provider.TryCopyLatestSamples(samples, ref observedVersion, out var count))
            {
                PublishVisualization(AudioSpectrumAnalysis.Analyze(
                    samples.AsSpan(0, count),
                    sampleRate));
            }

            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
    }

    private OfflineTts GetOrLoadTts(TtsModelFiles files)
    {
        var model = new FileInfo(files.Model);
        if (_loadedTts != null &&
            string.Equals(_loadedModelPath, model.FullName, StringComparison.OrdinalIgnoreCase) &&
            _loadedModelLength == model.Length &&
            _loadedModelWriteTimeUtc == model.LastWriteTimeUtc)
        {
            return _loadedTts;
        }

        _loadedTts?.Dispose();
        var config = new OfflineTtsConfig
        {
            RuleFsts = files.RuleFsts,
            MaxNumSentences = 2,
            SilenceScale = 0.2f
        };
        config.Model.Kokoro.Model = files.Model;
        config.Model.Kokoro.Voices = files.Voices;
        config.Model.Kokoro.Tokens = files.Tokens;
        config.Model.Kokoro.DataDir = files.DataDirectory;
        config.Model.Kokoro.Lexicon = files.Lexicons;
        config.Model.NumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
        config.Model.Provider = "cpu";
        config.Model.Debug = 0;
        _loadedTts = new OfflineTts(config);
        _loadedModelPath = model.FullName;
        _loadedModelLength = model.Length;
        _loadedModelWriteTimeUtc = model.LastWriteTimeUtc;
        return _loadedTts;
    }

    internal static void ExtractArchiveSafely(
        string archivePath,
        string targetDirectory,
        CancellationToken cancellationToken)
    {
        var targetRoot = Path.GetFullPath(targetDirectory) + Path.DirectorySeparatorChar;
        using var compressedStream = new FileStream(
            archivePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.SequentialScan);
        ValidateBZip2Header(compressedStream);
        using var reader = ReaderFactory.Open(
            compressedStream,
            new ReaderOptions { LeaveStreamOpen = true, ExtensionHint = "tar.bz2" });
        while (reader.MoveToNextEntry())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = reader.Entry;
            if (entry.IsDirectory)
            {
                continue;
            }

            var key = entry.Key?.Replace('/', Path.DirectorySeparatorChar) ?? string.Empty;
            var targetPath = Path.GetFullPath(Path.Combine(targetDirectory, key));
            if (!targetPath.StartsWith(targetRoot, PathComparison))
            {
                throw new InvalidDataException("模型压缩包包含不安全的文件路径。");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            using var destination = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None);
            reader.WriteEntryTo(destination);
        }
    }

    private static void ValidateBZip2Header(Stream stream)
    {
        Span<byte> header = stackalloc byte[3];
        if (stream.Read(header) != header.Length ||
            header[0] != (byte)'B' ||
            header[1] != (byte)'Z' ||
            header[2] != (byte)'h')
        {
            throw new InvalidDataException(
                "下载内容不是有效的 BZip2 模型包（缺少 BZh 文件头），请检查网络代理后重新下载。");
        }

        stream.Position = 0;
    }

    private bool CleanupActivePlayback(CancellationTokenSource owner)
    {
        PlaybackResources resources;
        lock (_playbackLock)
        {
            if (!ReferenceEquals(_activePlaybackCts, owner))
            {
                return false;
            }

            _activePlaybackCts = null;
            resources = DetachPlaybackResources();
        }

        DisposePlaybackResources(resources);
        return true;
    }

    private void StopPlaybackDevice()
    {
        PlaybackResources resources;
        lock (_playbackLock)
        {
            resources = DetachPlaybackResources();
        }

        DisposePlaybackResources(resources);
    }

    /// <summary>调用方必须持有 <see cref="_playbackLock"/>。</summary>
    private PlaybackResources DetachPlaybackResources()
    {
        var resources = new PlaybackResources(_activePlayer, _activeDevice, _activeEngine, _activeProvider);
        _activePlayer = null;
        _activeDevice = null;
        _activeEngine = null;
        _activeProvider = null;
        return resources;
    }

    private static void DisposePlaybackResources(PlaybackResources resources)
    {
        TryCleanup(() => resources.Player?.Stop());
        TryCleanup(() => resources.Device?.Stop());
        TryCleanup(() => resources.Player?.Dispose());
        TryCleanup(() => resources.Device?.Dispose());
        TryCleanup(() => resources.Engine?.Dispose());
        TryCleanup(() => resources.Provider?.Dispose());
    }

    private void UnloadModelIfPathIsUnder(string directory)
    {
        var root = Path.GetFullPath(directory) + Path.DirectorySeparatorChar;
        if (string.IsNullOrWhiteSpace(_loadedModelPath) ||
            !Path.GetFullPath(_loadedModelPath).StartsWith(root, PathComparison))
        {
            return;
        }

        _loadedTts?.Dispose();
        _loadedTts = null;
        _loadedModelPath = string.Empty;
        _loadedModelLength = 0;
        _loadedModelWriteTimeUtc = default;
    }

    private void PublishVisualization(AudioVisualizationFrame frame)
    {
        try
        {
            VisualizationFrameAvailable?.Invoke(frame);
        }
        catch
        {
            // 可视化订阅者不得中断录音或播放线程。
        }
    }

    private static void TryCleanup(Action action)
    {
        try
        {
            action();
        }
        catch
        {
            // 音频后端清理失败不能掩盖原始播放结果或阻止应用退出。
        }
    }

    private static void DeleteFileIfPresent(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // finally 清理采用尽力而为；主异常更有诊断价值。
        }
    }

    private static void DeleteDirectoryIfPresent(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // finally 清理采用尽力而为；主异常更有诊断价值。
        }
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private readonly record struct PlaybackResources(
        SoundPlayer? Player,
        AudioPlaybackDevice? Device,
        MiniAudioEngine? Engine,
        StreamingAudioDataProvider? Provider);

    private sealed record StreamingPlayback(
        StreamingAudioDataProvider Provider,
        TaskCompletionSource Completion,
        Task VisualizationTask);
}
