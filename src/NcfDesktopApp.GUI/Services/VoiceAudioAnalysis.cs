/*----------------------------------------------------------------
    Copyright (C) 2026 Senparc

    文件名：VoiceAudioAnalysis.cs
    文件功能描述：本地语音输入的信号有效性与非语音转写判断

    创建标识：Senparc - 20260803

    修改标识：Senparc - 20260804
    修改描述：v0.6.0 增加录音音量和静音状态诊断

----------------------------------------------------------------*/

using System;
using System.Text.RegularExpressions;

namespace NcfDesktopApp.GUI.Services;

internal readonly record struct VoiceAudioMetrics(
    int SampleCount,
    double DurationSeconds,
    double RootMeanSquare,
    double Peak,
    int InvalidSampleCount)
{
    public double RootMeanSquareDbfs => VoiceAudioAnalysis.ToDbfs(RootMeanSquare);

    public double PeakDbfs => VoiceAudioAnalysis.ToDbfs(Peak);
}

internal static partial class VoiceAudioAnalysis
{
    public const int SampleRate = 16000;
    public const int MinimumSampleCount = SampleRate / 4;
    public const double MinimumRootMeanSquare = 0.0015;
    public const double MinimumPeak = 0.01;

    public static VoiceAudioMetrics Analyze(ReadOnlySpan<float> samples)
    {
        double sumOfSquares = 0;
        double peak = 0;
        var invalidSampleCount = 0;
        var validSampleCount = 0;

        foreach (var sample in samples)
        {
            if (!float.IsFinite(sample))
            {
                invalidSampleCount++;
                continue;
            }

            var value = Math.Clamp((double)sample, -1d, 1d);
            var absolute = Math.Abs(value);
            peak = Math.Max(peak, absolute);
            sumOfSquares += value * value;
            validSampleCount++;
        }

        var rootMeanSquare = validSampleCount == 0
            ? 0
            : Math.Sqrt(sumOfSquares / validSampleCount);
        return new VoiceAudioMetrics(
            samples.Length,
            samples.Length / (double)SampleRate,
            rootMeanSquare,
            peak,
            invalidSampleCount);
    }

    public static bool HasUsableSignal(VoiceAudioMetrics metrics)
    {
        return metrics.SampleCount >= MinimumSampleCount &&
               metrics.Peak >= MinimumPeak &&
               metrics.RootMeanSquare >= MinimumRootMeanSquare;
    }

    public static bool IsOnlyNonSpeechAnnotation(string? transcript)
    {
        if (string.IsNullOrWhiteSpace(transcript))
        {
            return false;
        }

        var withoutAnnotations = NonSpeechAnnotationRegex().Replace(transcript, string.Empty);
        if (withoutAnnotations.Length == transcript.Length)
        {
            return false;
        }

        foreach (var character in withoutAnnotations)
        {
            if (!char.IsWhiteSpace(character) &&
                !char.IsPunctuation(character) &&
                !char.IsSymbol(character))
            {
                return false;
            }
        }

        return true;
    }

    public static double ToDbfs(double amplitude)
    {
        return amplitude <= 0 ? double.NegativeInfinity : 20 * Math.Log10(amplitude);
    }

    [GeneratedRegex(
        @"[\[\(（【]\s*(?:music|sound|noise|silence|blank[_\s-]?audio|applause|inaudible|音|音乐|音樂|音频|音頻|音讯|音訊|声音|聲音|噪音|静音|靜音|无声|無聲|掌声|掌聲)\s*[\]\)）】]",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NonSpeechAnnotationRegex();
}
