/*----------------------------------------------------------------
    Copyright (C) 2026 Senparc

    文件名：VoiceEndpointDetector.cs
    文件功能描述：固定唤醒词后的本地语音端点检测

    创建标识：Senparc - 20260810

    修改标识：Senparc - 20260812
    修改描述：v0.10.0 完善桌面端唤醒词会话激活与中英文提示

    修改标识：Senparc - 20260815
    修改描述：v0.10.1 增强风噪与动态背景噪声下的语音端点检测

----------------------------------------------------------------*/

using System;

namespace NcfDesktopApp.GUI.Services;

/// <summary>
/// 录音自动结束的原因。仅由固定唤醒词启动的录音启用，手动录音始终由用户停止。
/// </summary>
internal enum VoiceRecordingAutoStopReason
{
    SpeechEnded,
    SpeechNotDetected
}

/// <summary>
/// 轻量级本地端点检测器：先确认持续人声，再以连续静音作为说话结束信号。
/// 使用短帧、语音频带能量及自适应底噪，避免风声或逐渐增大的稳定噪声让录音一直持续。
/// 不使用模型、网络或计时器，所有判断均由 16 kHz PCM 采样时长驱动。
/// </summary>
internal sealed class VoiceEndpointDetector
{
    public const double SpeechStartMinimumSeconds = 0.18;
    public const double SpeechEndSilenceSeconds = 1.35;
    public const double NoSpeechTimeoutSeconds = 8;
    public const double MaximumSpeechSeconds = 30;

    private const double NoiseCalibrationSeconds = 0.25;
    private const int FrameSampleCount = VoiceAudioAnalysis.SampleRate / 50; // 20 ms
    private const int StableActivityFrameCount = 30; // 600 ms
    private const double HighPassCutoffHertz = 180;
    private const double MinimumSpeechRootMeanSquare = 0.0035;
    private const double MinimumSpeechPeak = 0.009;
    private const double MinimumSpeechBandRatio = 0.30;
    private const double LowFrequencyNoiseBandRatio = 0.55;
    private const double NoiseFloorMultiplier = 1.25;
    private const double QuietEnvironmentSpeechExcess = 0.0035;
    private const double NoisyEnvironmentSpeechExcess = 0.008;
    private const double NoisyEnvironmentRootMeanSquare = 0.01;
    private const double StableActivityRelativeRange = 0.24;
    private const double MaximumAdaptiveNoiseFractionOfSpeech = 0.82;

    private readonly float[] _frameBuffer = new float[FrameSampleCount];
    private readonly double[] _recentActivity = new double[StableActivityFrameCount];
    private int _frameBufferCount;
    private int _recentActivityCount;
    private int _recentActivityIndex;
    private double _highPassPreviousInput;
    private double _highPassPreviousOutput;
    private double _elapsedSeconds;
    private double _speechElapsedSeconds;
    private double _speechCandidateSeconds;
    private double _silenceSeconds;
    private double _noiseFloorRootMeanSquare = MinimumSpeechRootMeanSquare / NoiseFloorMultiplier;
    private double _speechReferenceRootMeanSquare;
    private bool _hasNoiseFloor;
    private bool _speechStarted;
    private bool _completed;

    /// <summary>
    /// 当前 <see cref="Process"/> 调用是否刚刚确认了有效讲话。供 UI 显示诊断状态，
    /// 不会产生额外的采样、计时器或线程调度开销。
    /// </summary>
    public bool SpeechStartedNow { get; private set; }

    public VoiceRecordingAutoStopReason? Process(ReadOnlySpan<float> samples)
    {
        SpeechStartedNow = false;
        if (_completed || samples.IsEmpty)
        {
            return null;
        }

        while (!samples.IsEmpty)
        {
            var copyCount = Math.Min(FrameSampleCount - _frameBufferCount, samples.Length);
            samples[..copyCount].CopyTo(_frameBuffer.AsSpan(_frameBufferCount));
            _frameBufferCount += copyCount;
            samples = samples[copyCount..];

            if (_frameBufferCount < FrameSampleCount)
            {
                continue;
            }

            _frameBufferCount = 0;
            var result = ProcessFrame(_frameBuffer);
            if (result != null)
            {
                return result;
            }
        }

        return null;
    }

    private VoiceRecordingAutoStopReason? ProcessFrame(ReadOnlySpan<float> samples)
    {
        var duration = samples.Length / (double)VoiceAudioAnalysis.SampleRate;
        var metrics = AnalyzeFrame(samples);
        _elapsedSeconds += duration;

        // 录音刚开始的一小段时间用于估计底噪。唤醒词检测已结束，用户通常会在此后再说指令。
        if (!_speechStarted && _elapsedSeconds <= NoiseCalibrationSeconds)
        {
            UpdateNoiseFloor(metrics.SpeechBandRootMeanSquare);
            return null;
        }

        var isSpeech = IsSpeech(metrics);
        if (!_speechStarted)
        {
            if (isSpeech)
            {
                _speechCandidateSeconds += duration;
                _speechReferenceRootMeanSquare = Math.Max(
                    _speechReferenceRootMeanSquare,
                    metrics.SpeechBandRootMeanSquare);
                if (_speechCandidateSeconds >= SpeechStartMinimumSeconds)
                {
                    _speechStarted = true;
                    _silenceSeconds = 0;
                    _speechElapsedSeconds = 0;
                    ResetRecentActivity();
                    SpeechStartedNow = true;
                }
            }
            else
            {
                _speechCandidateSeconds = 0;
                _speechReferenceRootMeanSquare = 0;
                UpdateNoiseFloor(metrics.SpeechBandRootMeanSquare);
            }

            if (_elapsedSeconds >= NoSpeechTimeoutSeconds)
            {
                _completed = true;
                return VoiceRecordingAutoStopReason.SpeechNotDetected;
            }

            return null;
        }

        _speechElapsedSeconds += duration;
        AddRecentActivity(metrics.SpeechBandRootMeanSquare);

        // 风噪通常主要集中在低频；普通持续噪声的短帧包络也比自然说话稳定。
        // 对这两类信号缓慢抬升底噪后重新判断，避免讲话结束后的噪声永久重置静音计时。
        var isLowFrequencyNoise = metrics.RawRootMeanSquare > MinimumSpeechRootMeanSquare &&
                                  metrics.SpeechBandRatio < LowFrequencyNoiseBandRatio;
        var isStableActivity = IsRecentActivityStable();
        if (isLowFrequencyNoise || isStableActivity)
        {
            UpdateNoiseFloorAfterSpeech(
                metrics.SpeechBandRootMeanSquare,
                isLowFrequencyNoise ? 0.35 : 0.12);
            isSpeech = IsSpeech(metrics);
        }

        if (isSpeech)
        {
            if (!isLowFrequencyNoise && !isStableActivity)
            {
                _speechReferenceRootMeanSquare = Math.Max(
                    _speechReferenceRootMeanSquare,
                    metrics.SpeechBandRootMeanSquare);
            }
            _silenceSeconds = 0;
        }
        else
        {
            _silenceSeconds += duration;
            if (_silenceSeconds >= SpeechEndSilenceSeconds)
            {
                _completed = true;
                return VoiceRecordingAutoStopReason.SpeechEnded;
            }
        }

        // 无论环境如何变化，单条唤醒指令都不应无限录音；长内容仍可使用手动录音。
        if (_speechElapsedSeconds >= MaximumSpeechSeconds)
        {
            _completed = true;
            return VoiceRecordingAutoStopReason.SpeechEnded;
        }

        return null;
    }

    private bool IsSpeech(VoiceEndpointFrameMetrics metrics)
    {
        var speechExcess = _noiseFloorRootMeanSquare >= NoisyEnvironmentRootMeanSquare
            ? NoisyEnvironmentSpeechExcess
            : QuietEnvironmentSpeechExcess;
        var rootMeanSquareThreshold = Math.Max(
            MinimumSpeechRootMeanSquare,
            Math.Max(
                _noiseFloorRootMeanSquare * NoiseFloorMultiplier,
                _noiseFloorRootMeanSquare + speechExcess));
        return metrics.SpeechBandRootMeanSquare > rootMeanSquareThreshold &&
               metrics.SpeechBandPeak >= MinimumSpeechPeak &&
               metrics.SpeechBandRatio >= MinimumSpeechBandRatio;
    }

    private VoiceEndpointFrameMetrics AnalyzeFrame(ReadOnlySpan<float> samples)
    {
        // 一阶高通保留主要语音频带，同时显著衰减麦克风吹风和桌面振动等低频能量。
        var alpha = VoiceAudioAnalysis.SampleRate /
                    (VoiceAudioAnalysis.SampleRate + 2 * Math.PI * HighPassCutoffHertz);
        double rawSumOfSquares = 0;
        double speechBandSumOfSquares = 0;
        double speechBandPeak = 0;
        var validCount = 0;

        foreach (var sample in samples)
        {
            var input = float.IsFinite(sample) ? Math.Clamp((double)sample, -1, 1) : 0;
            var output = alpha * (_highPassPreviousOutput + input - _highPassPreviousInput);
            _highPassPreviousInput = input;
            _highPassPreviousOutput = output;
            rawSumOfSquares += input * input;
            speechBandSumOfSquares += output * output;
            speechBandPeak = Math.Max(speechBandPeak, Math.Abs(output));
            validCount++;
        }

        var rawRootMeanSquare = validCount == 0 ? 0 : Math.Sqrt(rawSumOfSquares / validCount);
        var speechBandRootMeanSquare = validCount == 0
            ? 0
            : Math.Sqrt(speechBandSumOfSquares / validCount);
        var speechBandRatio = rawRootMeanSquare <= 1e-9
            ? 0
            : speechBandRootMeanSquare / rawRootMeanSquare;
        return new VoiceEndpointFrameMetrics(
            rawRootMeanSquare,
            speechBandRootMeanSquare,
            speechBandPeak,
            speechBandRatio);
    }

    private void UpdateNoiseFloor(double rootMeanSquare)
    {
        if (!double.IsFinite(rootMeanSquare) || rootMeanSquare < 0)
        {
            return;
        }

        if (!_hasNoiseFloor)
        {
            _noiseFloorRootMeanSquare = rootMeanSquare;
            _hasNoiseFloor = true;
            return;
        }

        // 仅在尚未确认人声时更新底噪，避免把用户说话的能量当成静音阈值。
        // 若唤醒词尾音刚好落入首帧，优先快速回落到后续真实环境声；不再人为钳制高底噪，
        // 否则嘈杂环境会被固定阈值错误判断为“始终在讲话”。
        _noiseFloorRootMeanSquare = rootMeanSquare < _noiseFloorRootMeanSquare
            ? _noiseFloorRootMeanSquare * 0.25 + rootMeanSquare * 0.75
            : _noiseFloorRootMeanSquare * 0.92 + rootMeanSquare * 0.08;
    }

    private void UpdateNoiseFloorAfterSpeech(double rootMeanSquare, double weight)
    {
        if (!double.IsFinite(rootMeanSquare) || rootMeanSquare < 0 ||
            _speechReferenceRootMeanSquare <= 0)
        {
            return;
        }

        var maximumAdaptiveNoise = Math.Max(
            _noiseFloorRootMeanSquare,
            _speechReferenceRootMeanSquare * MaximumAdaptiveNoiseFractionOfSpeech);
        var target = Math.Min(rootMeanSquare, maximumAdaptiveNoise);
        _noiseFloorRootMeanSquare += (target - _noiseFloorRootMeanSquare) * weight;
    }

    private void AddRecentActivity(double rootMeanSquare)
    {
        _recentActivity[_recentActivityIndex] = rootMeanSquare;
        _recentActivityIndex = (_recentActivityIndex + 1) % _recentActivity.Length;
        _recentActivityCount = Math.Min(_recentActivityCount + 1, _recentActivity.Length);
    }

    private bool IsRecentActivityStable()
    {
        if (_recentActivityCount < _recentActivity.Length)
        {
            return false;
        }

        double minimum = double.PositiveInfinity;
        double maximum = 0;
        double sum = 0;
        for (var index = 0; index < _recentActivityCount; index++)
        {
            var value = _recentActivity[index];
            minimum = Math.Min(minimum, value);
            maximum = Math.Max(maximum, value);
            sum += value;
        }

        var mean = sum / _recentActivityCount;
        return mean > MinimumSpeechRootMeanSquare &&
               (maximum - minimum) / mean <= StableActivityRelativeRange;
    }

    private void ResetRecentActivity()
    {
        Array.Clear(_recentActivity);
        _recentActivityCount = 0;
        _recentActivityIndex = 0;
    }

    private readonly record struct VoiceEndpointFrameMetrics(
        double RawRootMeanSquare,
        double SpeechBandRootMeanSquare,
        double SpeechBandPeak,
        double SpeechBandRatio);
}
