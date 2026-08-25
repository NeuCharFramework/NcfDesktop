/*----------------------------------------------------------------
    Copyright (C) 2026 Senparc

    文件名：LocalWakeWordService.cs
    文件功能描述：跨平台固定唤醒词模型下载与低开销流式检测

    创建标识：Senparc - 20260803

    修改标识：Senparc - 20260804
    修改描述：v0.6.0 增加固定唤醒词流式监听与音频协同

----------------------------------------------------------------*/

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Readers;
using SherpaOnnx;
using SoundFlow.Abstracts.Devices;
using SoundFlow.Backends.MiniAudio;
using SoundFlow.Components;
using SoundFlow.Enums;
using SoundFlow.Structs;

namespace NcfDesktopApp.GUI.Services;

internal interface ILocalWakeWordService
{
    event Action<WakeWordDetectedEvent>? KeywordDetected;

    event Action<WakeWordServiceFailure>? ListeningFailed;

    Guid? ListeningOwner { get; }

    bool IsListening(Guid owner);

    Task DownloadModelAsync(
        Action<WakeWordDownloadProgress>? progress,
        CancellationToken cancellationToken);

    Task StartListeningAsync(
        Guid owner,
        WakeWordModelFiles files,
        CancellationToken cancellationToken);

    Task StopListeningAsync(Guid owner);
}

internal sealed class LocalWakeWordService : ILocalWakeWordService, IDisposable
{
    private const int SampleRate = 16_000;
    private const int ChunkSampleCount = SampleRate / 10;
    private const int PreallocatedChunkCount = 8;
    private static readonly TimeSpan DownloadStallTimeout = TimeSpan.FromSeconds(15);
    private static readonly Lazy<LocalWakeWordService> SharedInstance = new(() => new LocalWakeWordService());
    private static readonly StringComparer FileNameComparer = StringComparer.OrdinalIgnoreCase;

    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly SemaphoreSlim _pendingChunkSignal = new(0, int.MaxValue);
    private readonly object _stateLock = new();
    private readonly object _captureBufferLock = new();
    private readonly ConcurrentQueue<float[]> _availableChunks = new();
    private readonly ConcurrentQueue<float[]> _pendingChunks = new();
    private readonly HttpClient _httpClient = new(new HttpClientHandler { AllowAutoRedirect = true })
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    private MiniAudioEngine? _audioEngine;
    private AudioCaptureDevice? _captureDevice;
    private Recorder? _recorder;
    private KeywordSpotter? _loadedSpotter;
    private string _loadedModelDirectory = string.Empty;
    private string _loadedKeywordsPath = string.Empty;
    private OnlineStream? _stream;
    private CancellationTokenSource? _decoderCts;
    private Task? _decoderTask;
    private float[]? _captureChunk;
    private int _captureChunkCount;
    private Guid? _listeningOwner;
    private int _keywordTriggered;
    private bool _disposed;

    public static LocalWakeWordService Shared => SharedInstance.Value;

    public event Action<WakeWordDetectedEvent>? KeywordDetected;

    public event Action<WakeWordServiceFailure>? ListeningFailed;

    public static void DisposeShared()
    {
        if (SharedInstance.IsValueCreated)
        {
            SharedInstance.Value.Dispose();
        }
    }

    public Guid? ListeningOwner
    {
        get
        {
            lock (_stateLock)
            {
                return _listeningOwner;
            }
        }
    }

    public bool IsListening(Guid owner)
    {
        lock (_stateLock)
        {
            return _listeningOwner == owner &&
                   _captureDevice != null &&
                   _decoderCts is { IsCancellationRequested: false };
        }
    }

    public async Task DownloadModelAsync(
        Action<WakeWordDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var archivePath = Path.Combine(
            WakeWordModelCatalog.ModelsDirectory,
            $".{WakeWordModelCatalog.ArchiveFileName}.download");
        var temporaryDirectory = Path.Combine(
            WakeWordModelCatalog.ModelsDirectory,
            $".{WakeWordModelCatalog.ModelId}.extract-{Environment.ProcessId}");
        var backupDirectory = Path.Combine(
            WakeWordModelCatalog.ModelsDirectory,
            $".{WakeWordModelCatalog.ModelId}.backup-{Environment.ProcessId}");
        try
        {
            if (ListeningOwner != null)
            {
                throw new InvalidOperationException("请先结束正在运行的唤醒监听，再下载模型。");
            }

            Directory.CreateDirectory(WakeWordModelCatalog.ModelsDirectory);
            DeleteFileIfPresent(archivePath);
            DeleteDirectoryIfPresent(temporaryDirectory);
            DeleteDirectoryIfPresent(backupDirectory);

            await DownloadArchiveWithFallbackAsync(
                archivePath,
                progress,
                cancellationToken).ConfigureAwait(false);

            Directory.CreateDirectory(temporaryDirectory);
            await Task.Run(
                () => ExtractRequiredModelFiles(archivePath, temporaryDirectory, cancellationToken),
                cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                Path.Combine(temporaryDirectory, WakeWordModelCatalog.KeywordsFileName),
                WakeWordModelCatalog.KeywordsFileContent,
                cancellationToken).ConfigureAwait(false);

            var readiness = WakeWordModelCatalog.EvaluateDirectory(temporaryDirectory);
            if (!readiness.IsReady)
            {
                throw new InvalidDataException(readiness.Message);
            }

            UnloadModelIfPathIsUnder(WakeWordModelCatalog.ModelDirectory);
            var hadExistingModel = Directory.Exists(WakeWordModelCatalog.ModelDirectory);
            if (hadExistingModel)
            {
                Directory.Move(WakeWordModelCatalog.ModelDirectory, backupDirectory);
            }

            try
            {
                Directory.Move(temporaryDirectory, WakeWordModelCatalog.ModelDirectory);
                DeleteDirectoryIfPresent(backupDirectory);
            }
            catch
            {
                if (!Directory.Exists(WakeWordModelCatalog.ModelDirectory) && Directory.Exists(backupDirectory))
                {
                    Directory.Move(backupDirectory, WakeWordModelCatalog.ModelDirectory);
                }

                throw;
            }
        }
        finally
        {
            DeleteFileIfPresent(archivePath);
            DeleteDirectoryIfPresent(temporaryDirectory);
            _lifecycleGate.Release();
        }
    }

    private async Task DownloadArchiveWithFallbackAsync(
        string archivePath,
        Action<WakeWordDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var failures = new List<string>();
        foreach (var downloadSource in WakeWordModelCatalog.DownloadSources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DeleteFileIfPresent(archivePath);
            progress?.Invoke(new WakeWordDownloadProgress(
                downloadSource.DisplayName,
                0,
                null,
                WakeWordDownloadStage.Connecting));

            using var sourceTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            sourceTimeout.CancelAfter(DownloadStallTimeout);
            try
            {
                using var response = await _httpClient.GetAsync(
                    downloadSource.Url,
                    HttpCompletionOption.ResponseHeadersRead,
                    sourceTimeout.Token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                var totalBytes = response.Content.Headers.ContentLength;
                progress?.Invoke(new WakeWordDownloadProgress(
                    downloadSource.DisplayName,
                    0,
                    totalBytes,
                    WakeWordDownloadStage.Downloading));

                await using var source = await response.Content
                    .ReadAsStreamAsync(sourceTimeout.Token)
                    .ConfigureAwait(false);
                var downloaded = await WriteDownloadedFileAsync(
                    source,
                    archivePath,
                    bytesWritten =>
                    {
                        sourceTimeout.CancelAfter(DownloadStallTimeout);
                        progress?.Invoke(new WakeWordDownloadProgress(
                            downloadSource.DisplayName,
                            bytesWritten,
                            totalBytes,
                            WakeWordDownloadStage.Downloading));
                    },
                    sourceTimeout.Token).ConfigureAwait(false);

                // WriteDownloadedFileAsync 返回时写入流已经关闭；此时才能重新打开文件做哈希校验。
                sourceTimeout.CancelAfter(Timeout.InfiniteTimeSpan);
                progress?.Invoke(new WakeWordDownloadProgress(
                    downloadSource.DisplayName,
                    downloaded,
                    totalBytes,
                    WakeWordDownloadStage.Validating));
                await ValidateDownloadedArchiveAsync(archivePath, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                var failure = DescribeDownloadFailure(ex);
                failures.Add($"{downloadSource.DisplayName}：{failure}");
                progress?.Invoke(new WakeWordDownloadProgress(
                    downloadSource.DisplayName,
                    0,
                    null,
                    WakeWordDownloadStage.SourceFailed,
                    failure));
                DeleteFileIfPresent(archivePath);
            }
        }

        throw new HttpRequestException(
            $"全部下载源均不可用。{string.Join("；", failures)}");
    }

    internal static async Task<long> WriteDownloadedFileAsync(
        Stream source,
        string archivePath,
        Action<long>? progress,
        CancellationToken cancellationToken)
    {
        long downloaded = 0;
        await using (var destination = new FileStream(
                         archivePath,
                         FileMode.CreateNew,
                         FileAccess.Write,
                         FileShare.None,
                         128 * 1024,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            var buffer = new byte[128 * 1024];
            int count;
            while ((count = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await destination.WriteAsync(
                    buffer.AsMemory(0, count),
                    cancellationToken).ConfigureAwait(false);
                downloaded += count;
                progress?.Invoke(downloaded);
            }

            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        return downloaded;
    }

    internal static async Task ValidateDownloadedArchiveAsync(
        string archivePath,
        CancellationToken cancellationToken)
    {
        var fileLength = new FileInfo(archivePath).Length;
        if (fileLength != WakeWordModelCatalog.ApproximateDownloadBytes)
        {
            throw new InvalidDataException(
                $"模型包大小不正确：实际 {fileLength} 字节，预期 {WakeWordModelCatalog.ApproximateDownloadBytes} 字节。");
        }

        await using var stream = new FileStream(
            archivePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        var actualHash = Convert.ToHexString(hash).ToLowerInvariant();
        if (!string.Equals(actualHash, WakeWordModelCatalog.ArchiveSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"模型包 SHA-256 校验失败（实际 {actualHash}），已拒绝安装。");
        }
    }

    private static string DescribeDownloadFailure(Exception exception)
    {
        if (exception is OperationCanceledException)
        {
            return $"连接或数据传输超过 {DownloadStallTimeout.TotalSeconds:F0} 秒无响应";
        }

        return exception.Message;
    }

    public async Task StartListeningAsync(
        Guid owner,
        WakeWordModelFiles files,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var currentOwner = ListeningOwner;
            if (currentOwner == owner && IsListening(owner))
            {
                return;
            }

            if (currentOwner != null)
            {
                throw new InvalidOperationException("麦克风唤醒正在被另一个工作台使用。");
            }

            var spotter = GetOrLoadSpotter(files);
            var stream = spotter.CreateStream();
            var decoderCts = new CancellationTokenSource();
            MiniAudioEngine? engine = null;
            AudioCaptureDevice? captureDevice = null;
            Recorder? recorder = null;
            try
            {
                engine = new MiniAudioEngine();
                engine.UpdateAudioDevicesInfo();
                var selectedDevice = engine.CaptureDevices.FirstOrDefault(device => device.IsDefault);
                if (string.IsNullOrWhiteSpace(selectedDevice.Name))
                {
                    selectedDevice = engine.CaptureDevices.FirstOrDefault();
                }

                if (string.IsNullOrWhiteSpace(selectedDevice.Name))
                {
                    throw new InvalidOperationException("未检测到可用于唤醒监听的麦克风。");
                }

                var format = new AudioFormat
                {
                    SampleRate = SampleRate,
                    Channels = 1,
                    Format = SampleFormat.F32,
                    Layout = ChannelLayout.Mono
                };
                PrepareCaptureBuffers();
                captureDevice = engine.InitializeCaptureDevice(selectedDevice, format);
                recorder = new Recorder(captureDevice, CaptureWakeAudio);

                lock (_stateLock)
                {
                    _audioEngine = engine;
                    _captureDevice = captureDevice;
                    _recorder = recorder;
                    _stream = stream;
                    _decoderCts = decoderCts;
                    _listeningOwner = owner;
                    Interlocked.Exchange(ref _keywordTriggered, 0);
                }

                _decoderTask = Task.Run(
                    () => DecodeLoopAsync(
                        owner,
                        spotter,
                        stream,
                        files.KeywordDefinitions,
                        decoderCts.Token),
                    CancellationToken.None);
                captureDevice.Start();
                var startResult = recorder.StartRecording();
                if (!startResult.IsSuccess)
                {
                    throw new InvalidOperationException(startResult.Error?.Message ?? "无法启动唤醒监听。");
                }

                // 所有权已移交给字段；失败清理改由 StopListeningCoreAsync 负责。
                engine = null;
                captureDevice = null;
                recorder = null;
                stream = null!;
                decoderCts = null!;
            }
            catch
            {
                if (ListeningOwner == owner)
                {
                    await StopListeningCoreAsync(owner).ConfigureAwait(false);
                }
                else
                {
                    TryCleanup(() => recorder?.Dispose());
                    TryCleanup(() => captureDevice?.Dispose());
                    TryCleanup(() => engine?.Dispose());
                    stream?.Dispose();
                    decoderCts?.Dispose();
                    ClearCaptureBuffers();
                }

                throw;
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task StopListeningAsync(Guid owner)
    {
        if (_disposed)
        {
            return;
        }

        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopListeningCoreAsync(owner).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            var owner = ListeningOwner;
            if (owner != null)
            {
                StopListeningAsync(owner.Value).GetAwaiter().GetResult();
            }
        }
        catch
        {
            // 应用退出时采用尽力而为清理。
        }

        _disposed = true;
        _loadedSpotter?.Dispose();
        _loadedSpotter = null;
        _httpClient.Dispose();
        _pendingChunkSignal.Dispose();
        _lifecycleGate.Dispose();
    }

    private async Task StopListeningCoreAsync(Guid owner)
    {
        Recorder? recorder;
        AudioCaptureDevice? captureDevice;
        MiniAudioEngine? engine;
        OnlineStream? stream;
        CancellationTokenSource? decoderCts;
        Task? decoderTask;
        lock (_stateLock)
        {
            if (_listeningOwner == null)
            {
                return;
            }

            if (_listeningOwner != owner)
            {
                throw new InvalidOperationException("唤醒监听正在被另一个工作台使用。");
            }

            recorder = _recorder;
            captureDevice = _captureDevice;
            engine = _audioEngine;
            stream = _stream;
            decoderCts = _decoderCts;
            decoderTask = _decoderTask;
            _recorder = null;
            _captureDevice = null;
            _audioEngine = null;
            _stream = null;
            _decoderCts = null;
            _decoderTask = null;
            _listeningOwner = null;
        }

        decoderCts?.Cancel();
        TrySignalDecoder();
        if (recorder != null)
        {
            try
            {
                await recorder.StopRecordingAsync().ConfigureAwait(false);
            }
            catch
            {
                // 后续仍需释放麦克风和推理流。
            }
        }

        TryCleanup(() => captureDevice?.Stop());
        if (decoderTask != null)
        {
            try
            {
                await decoderTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 正常停止。
            }
        }

        TryCleanup(() => recorder?.Dispose());
        TryCleanup(() => captureDevice?.Dispose());
        TryCleanup(() => engine?.Dispose());
        stream?.Dispose();
        decoderCts?.Dispose();
        ClearCaptureBuffers();
        Interlocked.Exchange(ref _keywordTriggered, 0);
    }

    private async Task DecodeLoopAsync(
        Guid owner,
        KeywordSpotter spotter,
        OnlineStream stream,
        IReadOnlyDictionary<string, WakeWordKeywordDefinition>? keywordDefinitions,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await _pendingChunkSignal.WaitAsync(cancellationToken).ConfigureAwait(false);
                while (_pendingChunks.TryDequeue(out var samples))
                {
                    try
                    {
                        if (Volatile.Read(ref _keywordTriggered) != 0)
                        {
                            continue;
                        }

                        stream.AcceptWaveform(SampleRate, samples);
                        while (spotter.IsReady(stream))
                        {
                            spotter.Decode(stream);
                            var result = spotter.GetResult(stream);
                            if (string.IsNullOrWhiteSpace(result.Keyword))
                            {
                                continue;
                            }

                            spotter.Reset(stream);
                            if (Interlocked.Exchange(ref _keywordTriggered, 1) == 0)
                            {
                                var alias = NormalizeKeywordAlias(result.Keyword);
                                var phrase = keywordDefinitions != null &&
                                             keywordDefinitions.TryGetValue(alias, out var definition)
                                    ? definition.Phrase
                                    : result.Keyword;
                                PublishKeywordDetected(new WakeWordDetectedEvent(
                                    owner,
                                    alias,
                                    phrase,
                                    DateTimeOffset.Now));
                            }

                            break;
                        }
                    }
                    finally
                    {
                        Array.Clear(samples);
                        _availableChunks.Enqueue(samples);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 正常停止。
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref _keywordTriggered, 1);
            PublishListeningFailure(new WakeWordServiceFailure(owner, ex));
        }
    }

    private void CaptureWakeAudio(Span<float> samples, Capability capability)
    {
        if (capability != Capability.Record || Volatile.Read(ref _keywordTriggered) != 0)
        {
            return;
        }

        lock (_captureBufferLock)
        {
            var sourceIndex = 0;
            while (sourceIndex < samples.Length)
            {
                _captureChunk ??= TakeAvailableChunk();
                if (_captureChunk == null)
                {
                    return;
                }

                var count = Math.Min(samples.Length - sourceIndex, ChunkSampleCount - _captureChunkCount);
                for (var index = 0; index < count; index++)
                {
                    var sample = samples[sourceIndex + index];
                    _captureChunk[_captureChunkCount + index] = float.IsFinite(sample)
                        ? Math.Clamp(sample, -1f, 1f)
                        : 0f;
                }

                sourceIndex += count;
                _captureChunkCount += count;
                if (_captureChunkCount < ChunkSampleCount)
                {
                    continue;
                }

                _pendingChunks.Enqueue(_captureChunk);
                _captureChunk = null;
                _captureChunkCount = 0;
                TrySignalDecoder();
            }
        }
    }

    private KeywordSpotter GetOrLoadSpotter(WakeWordModelFiles files)
    {
        if (_loadedSpotter != null &&
            string.Equals(_loadedModelDirectory, files.RootDirectory, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(_loadedKeywordsPath, files.Keywords, StringComparison.OrdinalIgnoreCase))
        {
            return _loadedSpotter;
        }

        _loadedSpotter?.Dispose();
        var config = new KeywordSpotterConfig();
        config.FeatConfig.SampleRate = SampleRate;
        config.FeatConfig.FeatureDim = 80;
        config.ModelConfig.Transducer.Encoder = files.Encoder;
        config.ModelConfig.Transducer.Decoder = files.Decoder;
        config.ModelConfig.Transducer.Joiner = files.Joiner;
        config.ModelConfig.Tokens = files.Tokens;
        config.ModelConfig.Provider = "cpu";
        config.ModelConfig.NumThreads = 1;
        config.ModelConfig.Debug = 0;
        config.MaxActivePaths = 4;
        config.NumTrailingBlanks = 1;
        config.KeywordsScore = 1.5f;
        config.KeywordsThreshold = 0.35f;
        config.KeywordsFile = files.Keywords;
        _loadedSpotter = new KeywordSpotter(config);
        _loadedModelDirectory = files.RootDirectory;
        _loadedKeywordsPath = files.Keywords;
        return _loadedSpotter;
    }

    private static string NormalizeKeywordAlias(string keyword)
    {
        var normalized = (keyword ?? string.Empty).Trim();
        return normalized.StartsWith('@') ? normalized[1..] : normalized;
    }

    private void PrepareCaptureBuffers()
    {
        ClearCaptureBuffers();
        for (var index = 0; index < PreallocatedChunkCount; index++)
        {
            _availableChunks.Enqueue(new float[ChunkSampleCount]);
        }

        _captureChunk = TakeAvailableChunk();
        _captureChunkCount = 0;
    }

    private void ClearCaptureBuffers()
    {
        lock (_captureBufferLock)
        {
            _captureChunk = null;
            _captureChunkCount = 0;
            while (_availableChunks.TryDequeue(out _))
            {
            }

            while (_pendingChunks.TryDequeue(out _))
            {
            }

            while (_pendingChunkSignal.Wait(0))
            {
            }
        }
    }

    private float[]? TakeAvailableChunk()
    {
        return _availableChunks.TryDequeue(out var chunk) ? chunk : null;
    }

    private void TrySignalDecoder()
    {
        try
        {
            _pendingChunkSignal.Release();
        }
        catch (SemaphoreFullException)
        {
            // 已有足够信号等待处理，无需追加。
        }
        catch (ObjectDisposedException)
        {
            // 应用退出并发清理。
        }
    }

    internal static void ExtractRequiredModelFiles(
        string archivePath,
        string targetDirectory,
        CancellationToken cancellationToken)
    {
        var requiredNames = new HashSet<string>(FileNameComparer)
        {
            WakeWordModelCatalog.EncoderFileName,
            WakeWordModelCatalog.DecoderFileName,
            WakeWordModelCatalog.JoinerFileName,
            WakeWordModelCatalog.TokensFileName
        };
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

            var fileName = Path.GetFileName(entry.Key?.Replace('/', Path.DirectorySeparatorChar));
            if (string.IsNullOrWhiteSpace(fileName) || !requiredNames.Remove(fileName))
            {
                continue;
            }

            var targetPath = Path.Combine(targetDirectory, fileName);
            using var destination = new FileStream(targetPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            reader.WriteEntryTo(destination);
        }

        if (requiredNames.Count > 0)
        {
            throw new InvalidDataException($"唤醒模型压缩包缺少文件：{string.Join("、", requiredNames)}");
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
                "下载内容不是有效的 BZip2 唤醒模型包（缺少 BZh 文件头），请检查网络代理后重新下载。");
        }

        stream.Position = 0;
    }

    private void UnloadModelIfPathIsUnder(string directory)
    {
        if (string.IsNullOrWhiteSpace(_loadedModelDirectory) ||
            !string.Equals(_loadedModelDirectory, directory, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _loadedSpotter?.Dispose();
        _loadedSpotter = null;
        _loadedModelDirectory = string.Empty;
    }

    private void PublishKeywordDetected(WakeWordDetectedEvent detected)
    {
        try
        {
            KeywordDetected?.Invoke(detected);
        }
        catch
        {
            // 订阅者异常不得破坏常驻音频线程。
        }
    }

    private void PublishListeningFailure(WakeWordServiceFailure failure)
    {
        try
        {
            ListeningFailed?.Invoke(failure);
        }
        catch
        {
            // 订阅者异常不得掩盖原始监听错误。
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
            // 音频后端清理失败不能阻止后续资源释放。
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
            // 临时文件采用尽力而为清理，主异常更有诊断价值。
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
            // 临时目录采用尽力而为清理，主异常更有诊断价值。
        }
    }
}

internal sealed record WakeWordDetectedEvent(
    Guid Owner,
    string KeywordId,
    string Phrase,
    DateTimeOffset DetectedAt);

internal sealed record WakeWordServiceFailure(Guid Owner, Exception Exception);
