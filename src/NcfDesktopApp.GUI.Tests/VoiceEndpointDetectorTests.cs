using Microsoft.VisualStudio.TestTools.UnitTesting;
using NcfDesktopApp.GUI.Services;

namespace NcfDesktopApp.GUI.Tests;

[TestClass]
public sealed class VoiceEndpointDetectorTests
{
    [TestMethod]
    public void Process_WhenSpeechThenSustainedSilence_ReturnsSpeechEnded()
    {
        var detector = new VoiceEndpointDetector();

        Assert.IsNull(detector.Process(CreateSamples(0.25, 0)));
        Assert.IsNull(detector.Process(CreateSamples(0.30, 0.05f)));
        Assert.IsNull(detector.Process(CreateSamples(VoiceEndpointDetector.SpeechEndSilenceSeconds - 0.05, 0f)));

        var result = detector.Process(CreateSamples(0.10, 0));

        Assert.AreEqual(VoiceRecordingAutoStopReason.SpeechEnded, result);
        Assert.IsNull(detector.Process(CreateSamples(1, 0)));
    }

    [TestMethod]
    public void Process_WhenNoSpeechArrives_ReturnsSpeechNotDetected()
    {
        var detector = new VoiceEndpointDetector();

        Assert.IsNull(detector.Process(CreateSamples(VoiceEndpointDetector.NoSpeechTimeoutSeconds - 0.1, 0)));

        var result = detector.Process(CreateSamples(0.2, 0));

        Assert.AreEqual(VoiceRecordingAutoStopReason.SpeechNotDetected, result);
    }

    [TestMethod]
    public void Process_WhenSpeechResumesBeforeSilenceThreshold_DoesNotEndEarly()
    {
        var detector = new VoiceEndpointDetector();

        Assert.IsNull(detector.Process(CreateSamples(0.25, 0)));
        Assert.IsNull(detector.Process(CreateSamples(0.30, 0.05f)));
        Assert.IsNull(detector.Process(CreateSamples(VoiceEndpointDetector.SpeechEndSilenceSeconds - 0.1, 0f)));
        Assert.IsNull(detector.Process(CreateSamples(0.20, 0.05f)));

        var result = detector.Process(CreateSamples(VoiceEndpointDetector.SpeechEndSilenceSeconds + 0.05, 0f));

        Assert.AreEqual(VoiceRecordingAutoStopReason.SpeechEnded, result);
    }

    [TestMethod]
    public void Process_WhenContinuousBackgroundNoiseRemainsAfterSpeech_UsesNoiseFloorAsSilence()
    {
        var detector = new VoiceEndpointDetector();

        // 稳定的环境噪声为 -30 dBFS 左右；用户说话明显高于底噪后又回落到同一环境声。
        Assert.IsNull(detector.Process(CreateSamples(0.25, 0.03f)));
        Assert.IsNull(detector.Process(CreateSamples(0.30, 0.065f)));
        Assert.IsNull(detector.Process(CreateSamples(VoiceEndpointDetector.SpeechEndSilenceSeconds - 0.05, 0.03f)));

        var result = detector.Process(CreateSamples(0.10, 0.03f));

        Assert.AreEqual(VoiceRecordingAutoStopReason.SpeechEnded, result);
    }

    [TestMethod]
    public void Process_WhenHighBackgroundNoiseHasDistinctSpeech_UsesAbsoluteEnergyExcess()
    {
        var detector = new VoiceEndpointDetector();

        // 底噪约 -26 dBFS 时，说话声只高约 2.3 dB。仅使用 1.7 倍比例会完全漏检，
        // 但稳定背景不应阻止一句正常命令在停顿后结束。
        Assert.IsNull(detector.Process(CreateSamples(0.25, 0.05f)));
        Assert.IsNull(detector.Process(CreateSamples(0.30, 0.065f)));
        Assert.IsTrue(detector.SpeechStartedNow);
        Assert.IsNull(detector.Process(CreateSamples(VoiceEndpointDetector.SpeechEndSilenceSeconds - 0.05, 0.05f)));

        var result = detector.Process(CreateSamples(0.10, 0.05f));

        Assert.AreEqual(VoiceRecordingAutoStopReason.SpeechEnded, result);
    }

    [TestMethod]
    public void Process_WhenOnlyBackgroundNoiseIsPresent_DoesNotMistakeItForSpeech()
    {
        var detector = new VoiceEndpointDetector();

        Assert.IsNull(detector.Process(CreateSamples(0.25, 0.03f)));
        Assert.IsNull(detector.Process(CreateSamples(VoiceEndpointDetector.NoSpeechTimeoutSeconds - 0.35, 0.04f)));

        var result = detector.Process(CreateSamples(0.20, 0.03f));

        Assert.AreEqual(VoiceRecordingAutoStopReason.SpeechNotDetected, result);
    }

    private static float[] CreateSamples(double seconds, float amplitude)
    {
        var sampleCount = (int)Math.Ceiling(seconds * VoiceAudioAnalysis.SampleRate);
        return Enumerable.Repeat(amplitude, sampleCount).ToArray();
    }
}
