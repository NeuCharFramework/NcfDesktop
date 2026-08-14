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

        Assert.IsNull(detector.Process(CreateSilence(0.25)));
        Assert.IsNull(detector.Process(CreateSpeech(0.45)));
        Assert.IsTrue(detector.SpeechStartedNow);
        Assert.IsNull(detector.Process(CreateSilence(VoiceEndpointDetector.SpeechEndSilenceSeconds - 0.05)));

        var result = detector.Process(CreateSilence(0.10));

        Assert.AreEqual(VoiceRecordingAutoStopReason.SpeechEnded, result);
        Assert.IsNull(detector.Process(CreateSilence(1)));
    }

    [TestMethod]
    public void Process_WhenNoSpeechArrives_ReturnsSpeechNotDetected()
    {
        var detector = new VoiceEndpointDetector();

        Assert.IsNull(detector.Process(CreateSilence(VoiceEndpointDetector.NoSpeechTimeoutSeconds - 0.1)));

        var result = detector.Process(CreateSilence(0.2));

        Assert.AreEqual(VoiceRecordingAutoStopReason.SpeechNotDetected, result);
    }

    [TestMethod]
    public void Process_WhenSpeechResumesBeforeSilenceThreshold_DoesNotEndEarly()
    {
        var detector = new VoiceEndpointDetector();

        Assert.IsNull(detector.Process(CreateSilence(0.25)));
        Assert.IsNull(detector.Process(CreateSpeech(0.45)));
        Assert.IsNull(detector.Process(CreateSilence(VoiceEndpointDetector.SpeechEndSilenceSeconds - 0.1)));
        Assert.IsNull(detector.Process(CreateSpeech(0.30)));

        var result = detector.Process(CreateSilence(VoiceEndpointDetector.SpeechEndSilenceSeconds + 0.05));

        Assert.AreEqual(VoiceRecordingAutoStopReason.SpeechEnded, result);
    }

    [TestMethod]
    public void Process_WhenContinuousBackgroundNoiseRemainsAfterSpeech_UsesNoiseFloorAsSilence()
    {
        var detector = new VoiceEndpointDetector();
        var background = CreateBroadbandNoise(0.25, 0.04f, seed: 11);

        Assert.IsNull(detector.Process(background));
        Assert.IsNull(detector.Process(Mix(
            CreateSpeech(0.45, 0.16f),
            CreateBroadbandNoise(0.45, 0.04f, seed: 12))));
        Assert.IsTrue(detector.SpeechStartedNow);
        Assert.IsNull(detector.Process(CreateBroadbandNoise(
            VoiceEndpointDetector.SpeechEndSilenceSeconds - 0.05,
            0.04f,
            seed: 13)));

        var result = detector.Process(CreateBroadbandNoise(0.20, 0.04f, seed: 14));

        Assert.AreEqual(VoiceRecordingAutoStopReason.SpeechEnded, result);
    }

    [TestMethod]
    public void Process_WhenBackgroundNoiseGetsLouderAfterSpeech_AdaptsAndStops()
    {
        var detector = new VoiceEndpointDetector();

        Assert.IsNull(detector.Process(CreateBroadbandNoise(0.25, 0.018f, seed: 21)));
        Assert.IsNull(detector.Process(Mix(
            CreateSpeech(0.55, 0.11f),
            CreateBroadbandNoise(0.55, 0.018f, seed: 22))));

        var result = detector.Process(CreateBroadbandNoise(2.8, 0.05f, seed: 23));

        Assert.AreEqual(VoiceRecordingAutoStopReason.SpeechEnded, result);
    }

    [TestMethod]
    public void Process_WhenWindGetsLouderAfterSpeech_TreatsLowFrequencyEnergyAsNoise()
    {
        var detector = new VoiceEndpointDetector();

        Assert.IsNull(detector.Process(CreateWind(0.25, 0.025f)));
        Assert.IsNull(detector.Process(Mix(
            CreateSpeech(0.55, 0.10f),
            CreateWind(0.55, 0.025f))));

        var result = detector.Process(CreateWind(2.0, 0.12f));

        Assert.AreEqual(VoiceRecordingAutoStopReason.SpeechEnded, result);
    }

    [TestMethod]
    public void Process_WhenGustyWindContainsBroadbandNoise_StillStopsAfterSpeech()
    {
        var detector = new VoiceEndpointDetector();

        Assert.IsNull(detector.Process(Mix(
            CreateWind(0.25, 0.025f),
            CreateBroadbandNoise(0.25, 0.008f, seed: 41))));
        Assert.IsNull(detector.Process(Mix(
            CreateSpeech(0.55, 0.12f),
            Mix(
                CreateWind(0.55, 0.025f),
                CreateBroadbandNoise(0.55, 0.008f, seed: 42)))));

        var result = detector.Process(Mix(
            CreateWind(2.4, 0.10f),
            CreateBroadbandNoise(2.4, 0.025f, seed: 43)));

        Assert.AreEqual(VoiceRecordingAutoStopReason.SpeechEnded, result);
    }

    [TestMethod]
    public void Process_WhenOnlyWindIsPresent_DoesNotMistakeItForSpeech()
    {
        var detector = new VoiceEndpointDetector();

        Assert.IsNull(detector.Process(CreateWind(4, 0.04f)));
        var result = detector.Process(CreateWind(4.1, 0.10f));

        Assert.AreEqual(VoiceRecordingAutoStopReason.SpeechNotDetected, result);
    }

    [TestMethod]
    public void Process_WhenHighBackgroundNoiseHasDistinctSpeech_DetectsSpeechAndStops()
    {
        var detector = new VoiceEndpointDetector();

        Assert.IsNull(detector.Process(CreateBroadbandNoise(0.25, 0.07f, seed: 31)));
        Assert.IsNull(detector.Process(Mix(
            CreateSpeech(0.55, 0.24f),
            CreateBroadbandNoise(0.55, 0.07f, seed: 32))));
        Assert.IsTrue(detector.SpeechStartedNow);

        var result = detector.Process(CreateBroadbandNoise(2.4, 0.07f, seed: 33));

        Assert.AreEqual(VoiceRecordingAutoStopReason.SpeechEnded, result);
    }

    [TestMethod]
    public void Process_WhenNaturalSpeechContinuesThroughShortPause_DoesNotEndEarly()
    {
        var detector = new VoiceEndpointDetector();

        Assert.IsNull(detector.Process(CreateWind(0.25, 0.025f)));
        Assert.IsNull(detector.Process(Mix(CreateSpeech(0.7), CreateWind(0.7, 0.025f))));
        Assert.IsNull(detector.Process(CreateWind(0.75, 0.04f)));
        Assert.IsNull(detector.Process(Mix(CreateSpeech(0.6), CreateWind(0.6, 0.04f))));

        var result = detector.Process(CreateWind(2.0, 0.04f));

        Assert.AreEqual(VoiceRecordingAutoStopReason.SpeechEnded, result);
    }

    [TestMethod]
    public void Process_WhenNoiseCannotBeDistinguished_StopsAtMaximumUtteranceDuration()
    {
        var detector = new VoiceEndpointDetector();

        Assert.IsNull(detector.Process(CreateSilence(0.25)));
        Assert.IsNull(detector.Process(CreateSpeech(0.5)));

        var result = detector.Process(CreateSpeech(VoiceEndpointDetector.MaximumSpeechSeconds + 0.1));

        Assert.AreEqual(VoiceRecordingAutoStopReason.SpeechEnded, result);
    }

    private static float[] CreateSilence(double seconds) => new float[SampleCount(seconds)];

    private static float[] CreateSpeech(double seconds, float amplitude = 0.10f)
    {
        var samples = new float[SampleCount(seconds)];
        for (var index = 0; index < samples.Length; index++)
        {
            var time = index / (double)VoiceAudioAnalysis.SampleRate;
            var envelope = 0.68 + 0.24 * Math.Sin(2 * Math.PI * 3.7 * time) +
                           0.08 * Math.Sin(2 * Math.PI * 7.3 * time);
            var voice = 0.48 * Math.Sin(2 * Math.PI * 220 * time) +
                        0.34 * Math.Sin(2 * Math.PI * 720 * time) +
                        0.18 * Math.Sin(2 * Math.PI * 1680 * time);
            samples[index] = (float)(amplitude * envelope * voice);
        }

        return samples;
    }

    private static float[] CreateWind(double seconds, float amplitude)
    {
        var samples = new float[SampleCount(seconds)];
        for (var index = 0; index < samples.Length; index++)
        {
            var time = index / (double)VoiceAudioAnalysis.SampleRate;
            var gust = 0.82 + 0.18 * Math.Sin(2 * Math.PI * 0.65 * time);
            var wind = 0.78 * Math.Sin(2 * Math.PI * 48 * time) +
                       0.22 * Math.Sin(2 * Math.PI * 92 * time);
            samples[index] = (float)(amplitude * gust * wind);
        }

        return samples;
    }

    private static float[] CreateBroadbandNoise(double seconds, float amplitude, int seed)
    {
        var samples = new float[SampleCount(seconds)];
        var random = new Random(seed);
        for (var index = 0; index < samples.Length; index++)
        {
            samples[index] = (float)((random.NextDouble() * 2 - 1) * amplitude);
        }

        return samples;
    }

    private static float[] Mix(float[] first, float[] second)
    {
        Assert.AreEqual(first.Length, second.Length);
        var mixed = new float[first.Length];
        for (var index = 0; index < mixed.Length; index++)
        {
            mixed[index] = Math.Clamp(first[index] + second[index], -1f, 1f);
        }

        return mixed;
    }

    private static int SampleCount(double seconds) =>
        (int)Math.Ceiling(seconds * VoiceAudioAnalysis.SampleRate);
}
