using Microsoft.VisualStudio.TestTools.UnitTesting;
using NcfDesktopApp.GUI.Services;

namespace NcfDesktopApp.GUI.Tests;

[TestClass]
public sealed class VoiceAudioAnalysisTests
{
    [TestMethod]
    public void Analyze_WhenSamplesAreSilent_RejectsSignal()
    {
        var samples = new float[VoiceAudioAnalysis.SampleRate];

        var metrics = VoiceAudioAnalysis.Analyze(samples);

        Assert.IsFalse(VoiceAudioAnalysis.HasUsableSignal(metrics));
        Assert.AreEqual(0d, metrics.Peak);
        Assert.AreEqual(0d, metrics.RootMeanSquare);
        Assert.IsTrue(double.IsNegativeInfinity(metrics.PeakDbfs));
    }

    [TestMethod]
    public void Analyze_WhenSamplesContainAudibleSpeechLikeSignal_AcceptsSignal()
    {
        var samples = new float[VoiceAudioAnalysis.SampleRate];
        for (var index = 0; index < samples.Length; index++)
        {
            samples[index] = 0.05f * MathF.Sin(2 * MathF.PI * 220 * index / VoiceAudioAnalysis.SampleRate);
        }

        var metrics = VoiceAudioAnalysis.Analyze(samples);

        Assert.IsTrue(VoiceAudioAnalysis.HasUsableSignal(metrics));
        Assert.IsTrue(metrics.Peak >= 0.049);
        Assert.IsTrue(metrics.RootMeanSquare > VoiceAudioAnalysis.MinimumRootMeanSquare);
    }

    [TestMethod]
    public void Analyze_WhenRecordingIsTooShort_RejectsEvenLoudSignal()
    {
        var samples = Enumerable.Repeat(0.1f, VoiceAudioAnalysis.MinimumSampleCount - 1).ToArray();

        var metrics = VoiceAudioAnalysis.Analyze(samples);

        Assert.IsFalse(VoiceAudioAnalysis.HasUsableSignal(metrics));
    }

    [TestMethod]
    public void Analyze_WhenSamplesContainInvalidValues_IgnoresThemSafely()
    {
        var samples = new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity, 0.25f };

        var metrics = VoiceAudioAnalysis.Analyze(samples);

        Assert.AreEqual(3, metrics.InvalidSampleCount);
        Assert.AreEqual(0.25d, metrics.Peak);
    }

    [DataTestMethod]
    [DataRow("[Music]")]
    [DataRow("(音)")]
    [DataRow("（音）")]
    [DataRow("(音樂)")]
    [DataRow("[BLANK_AUDIO]")]
    [DataRow("[Silence], [Noise]")]
    public void IsOnlyNonSpeechAnnotation_WhenOnlySoundTags_ReturnsTrue(string transcript)
    {
        Assert.IsTrue(VoiceAudioAnalysis.IsOnlyNonSpeechAnnotation(transcript));
    }

    [DataTestMethod]
    [DataRow("请帮我查看系统状态")]
    [DataRow("请播放音乐")]
    [DataRow("[Music] 现在开始汇报")]
    public void IsOnlyNonSpeechAnnotation_WhenSpeechTextExists_ReturnsFalse(string transcript)
    {
        Assert.IsFalse(VoiceAudioAnalysis.IsOnlyNonSpeechAnnotation(transcript));
    }
}
