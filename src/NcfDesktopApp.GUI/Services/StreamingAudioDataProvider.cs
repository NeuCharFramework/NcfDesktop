/*----------------------------------------------------------------
    Copyright (C) 2026 Senparc

    文件名：StreamingAudioDataProvider.cs
    文件功能描述：为本地流式 TTS 提供有界 PCM 队列与播放态可视化采样

    创建标识：Senparc - 20260803

    修改标识：Senparc - 20260804
    修改描述：v0.6.0 增加有界 PCM 队列与播放态可视化采样

----------------------------------------------------------------*/

using System;
using SoundFlow.Enums;
using SoundFlow.Interfaces;
using SoundFlow.Metadata.Models;
using SoundFlow.Providers;
using SoundFlow.Structs;

namespace NcfDesktopApp.GUI.Services;

internal sealed class StreamingAudioDataProvider : ISoundDataProvider, IDisposable
{
    private readonly QueueDataProvider _inner;
    private readonly int _maximumBufferedSamples;
    private readonly object _visualizationLock = new();
    private float[] _latestSamples = Array.Empty<float>();
    private int _latestSampleCount;
    private long _latestVersion;

    public StreamingAudioDataProvider(AudioFormat format, int maximumBufferedSamples)
    {
        _maximumBufferedSamples = Math.Max(maximumBufferedSamples, format.SampleRate);
        _inner = new QueueDataProvider(
            format,
            _maximumBufferedSamples,
            QueueFullBehavior.Block);
    }

    public int Position => _inner.Position;

    public int Length => _inner.Length;

    public bool CanSeek => _inner.CanSeek;

    public SampleFormat SampleFormat => _inner.SampleFormat;

    public int SampleRate => _inner.SampleRate;

    public bool IsDisposed => _inner.IsDisposed;

    public SoundFormatInfo? FormatInfo => _inner.FormatInfo;

    public int SamplesAvailable => _inner.SamplesAvailable;

    public event EventHandler<EventArgs>? EndOfStreamReached
    {
        add => _inner.EndOfStreamReached += value;
        remove => _inner.EndOfStreamReached -= value;
    }

    public event EventHandler<PositionChangedEventArgs>? PositionChanged
    {
        add => _inner.PositionChanged += value;
        remove => _inner.PositionChanged -= value;
    }

    public void AddSamples(ReadOnlySpan<float> samples)
    {
        var maximumWriteSize = Math.Max(1, Math.Min(SampleRate * 5, _maximumBufferedSamples));
        while (!samples.IsEmpty)
        {
            var count = Math.Min(samples.Length, maximumWriteSize);
            _inner.AddSamples(samples[..count]);
            samples = samples[count..];
        }
    }

    public void CompleteAdding() => _inner.CompleteAdding();

    public int ReadBytes(Span<float> buffer)
    {
        var count = _inner.ReadBytes(buffer);
        if (count <= 0)
        {
            return count;
        }

        var samples = buffer[..count];
        lock (_visualizationLock)
        {
            if (_latestSamples.Length < count)
            {
                _latestSamples = new float[count];
            }

            samples.CopyTo(_latestSamples);
            _latestSampleCount = count;
            _latestVersion++;
        }

        return count;
    }

    public bool TryCopyLatestSamples(float[] destination, ref long observedVersion, out int count)
    {
        lock (_visualizationLock)
        {
            if (_latestVersion == observedVersion || _latestSampleCount == 0)
            {
                count = 0;
                return false;
            }

            count = Math.Min(destination.Length, _latestSampleCount);
            _latestSamples.AsSpan(_latestSampleCount - count, count).CopyTo(destination);
            observedVersion = _latestVersion;
            return true;
        }
    }

    public void Seek(int offset) => _inner.Seek(offset);

    public void Dispose()
    {
        _inner.Dispose();
        lock (_visualizationLock)
        {
            _latestSamples = Array.Empty<float>();
            _latestSampleCount = 0;
        }
    }
}
