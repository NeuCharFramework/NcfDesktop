/*----------------------------------------------------------------
    Copyright (C) 2026 Senparc

    文件名：AudioSpectrumAnalysis.cs
    文件功能描述：将实时 PCM 样本转换为声波能量与频段数据

    创建标识：Senparc - 20260803
----------------------------------------------------------------*/

using System;
using NcfDesktopApp.GUI.Models;

namespace NcfDesktopApp.GUI.Services;

internal static class AudioSpectrumAnalysis
{
    private static readonly double[] CenterFrequencies =
    {
        90, 150, 240, 380, 600, 950, 1500, 2300, 3400, 4700, 6100, 7600
    };

    public static AudioVisualizationFrame Analyze(ReadOnlySpan<float> samples, int sampleRate)
    {
        if (samples.IsEmpty || sampleRate <= 0)
        {
            return AudioVisualizationFrame.Silent;
        }

        var windowLength = Math.Min(samples.Length, 1024);
        var window = samples[^windowLength..];
        double sumOfSquares = 0;
        var validCount = 0;
        foreach (var sample in window)
        {
            if (!float.IsFinite(sample))
            {
                continue;
            }

            var value = Math.Clamp((double)sample, -1d, 1d);
            sumOfSquares += value * value;
            validCount++;
        }

        if (validCount == 0)
        {
            return AudioVisualizationFrame.Silent;
        }

        var rms = Math.Sqrt(sumOfSquares / validCount);
        var dbfs = rms <= 0 ? -120 : 20 * Math.Log10(rms);
        var level = Math.Clamp((dbfs + 58) / 46, 0, 1);
        var magnitudes = new double[CenterFrequencies.Length];
        var maxMagnitude = 0d;
        for (var band = 0; band < CenterFrequencies.Length; band++)
        {
            var frequency = Math.Min(CenterFrequencies[band], sampleRate * 0.46);
            var magnitude = MeasureFrequency(window, sampleRate, frequency);
            magnitudes[band] = magnitude;
            maxMagnitude = Math.Max(maxMagnitude, magnitude);
        }

        var bands = new double[magnitudes.Length];
        for (var band = 0; band < bands.Length; band++)
        {
            var relative = maxMagnitude <= 1e-9 ? 0 : magnitudes[band] / maxMagnitude;
            bands[band] = Math.Clamp(level * (0.18 + relative * 0.82), 0, 1);
        }

        return new AudioVisualizationFrame(level, bands);
    }

    private static double MeasureFrequency(ReadOnlySpan<float> samples, int sampleRate, double frequency)
    {
        double real = 0;
        double imaginary = 0;
        var denominator = Math.Max(1, samples.Length - 1);
        for (var index = 0; index < samples.Length; index++)
        {
            var sample = float.IsFinite(samples[index]) ? samples[index] : 0;
            var hann = 0.5 - 0.5 * Math.Cos(2 * Math.PI * index / denominator);
            var angle = 2 * Math.PI * frequency * index / sampleRate;
            real += sample * hann * Math.Cos(angle);
            imaginary -= sample * hann * Math.Sin(angle);
        }

        return Math.Sqrt(real * real + imaginary * imaginary) / samples.Length;
    }
}
