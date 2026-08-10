/*----------------------------------------------------------------
    Copyright (C) 2026 Senparc

    文件名：VoiceEndpointDetector.cs
    文件功能描述：固定唤醒词后的本地语音端点检测

    创建标识：Senparc - 20260810

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
/// 不使用模型、网络或计时器，所有判断均由 16 kHz PCM 采样时长驱动。
/// </summary>
internal sealed class VoiceEndpointDetector
{
    public const double SpeechStartMinimumSeconds = 0.18;
    public const double SpeechEndSilenceSeconds = 1.35;
    public const double NoSpeechTimeoutSeconds = 8;

    private const double NoiseCalibrationSeconds = 0.25;
    // 起始阈值相对底噪计算。除比例外还要求一个绝对能量增量：只用倍率时，
    // 底噪较高的麦克风会把正常近讲语音的门槛抬得过高，导致永远无法进入“已讲话”。
    // 0.01 约为 -40 dBFS，足以排除稳定的风扇、空调和轻微底噪，同时保留常见说话声。
    private const double MinimumSpeechRootMeanSquare = 0.0045;
    private const double MinimumSpeechPeak = 0.012;
    private const double NoiseFloorMultiplier = 1.25;
    private const double QuietEnvironmentSpeechExcess = 0.0045;
    private const double NoisyEnvironmentSpeechExcess = 0.01;
    private const double NoisyEnvironmentRootMeanSquare = 0.012;

    private double _elapsedSeconds;
    private double _speechCandidateSeconds;
    private double _silenceSeconds;
    private double _noiseFloorRootMeanSquare = MinimumSpeechRootMeanSquare / NoiseFloorMultiplier;
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

        var duration = samples.Length / (double)VoiceAudioAnalysis.SampleRate;
        if (duration <= 0)
        {
            return null;
        }

        var metrics = VoiceAudioAnalysis.Analyze(samples);
        _elapsedSeconds += duration;

        // 录音刚开始的一小段时间用于估计底噪。唤醒词检测已结束，用户通常会在此后再说指令。
        if (!_speechStarted && _elapsedSeconds <= NoiseCalibrationSeconds)
        {
            UpdateNoiseFloor(metrics.RootMeanSquare);
            return null;
        }

        var isSpeech = IsSpeech(metrics);
        if (!_speechStarted)
        {
            if (isSpeech)
            {
                _speechCandidateSeconds += duration;
                if (_speechCandidateSeconds >= SpeechStartMinimumSeconds)
                {
                    _speechStarted = true;
                    _silenceSeconds = 0;
                    SpeechStartedNow = true;
                }
            }
            else
            {
                _speechCandidateSeconds = 0;
                UpdateNoiseFloor(metrics.RootMeanSquare);
            }

            if (_elapsedSeconds >= NoSpeechTimeoutSeconds)
            {
                _completed = true;
                return VoiceRecordingAutoStopReason.SpeechNotDetected;
            }

            return null;
        }

        if (isSpeech)
        {
            _silenceSeconds = 0;
            return null;
        }

        _silenceSeconds += duration;
        if (_silenceSeconds < SpeechEndSilenceSeconds)
        {
            return null;
        }

        _completed = true;
        return VoiceRecordingAutoStopReason.SpeechEnded;
    }

    private bool IsSpeech(VoiceAudioMetrics metrics)
    {
        var speechExcess = _noiseFloorRootMeanSquare >= NoisyEnvironmentRootMeanSquare
            ? NoisyEnvironmentSpeechExcess
            : QuietEnvironmentSpeechExcess;
        var rootMeanSquareThreshold = Math.Max(
            MinimumSpeechRootMeanSquare,
            Math.Max(
                _noiseFloorRootMeanSquare * NoiseFloorMultiplier,
                _noiseFloorRootMeanSquare + speechExcess));
        return metrics.RootMeanSquare > rootMeanSquareThreshold &&
               metrics.Peak >= MinimumSpeechPeak;
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
}
