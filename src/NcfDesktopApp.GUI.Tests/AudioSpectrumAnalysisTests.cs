using Microsoft.VisualStudio.TestTools.UnitTesting;
using NcfDesktopApp.GUI.Services;

namespace NcfDesktopApp.GUI.Tests;

[TestClass]
public sealed class AudioSpectrumAnalysisTests
{
    [TestMethod]
    public void Analyze_WhenSilent_ReturnsZeroLevelAndBands()
    {
        var frame = AudioSpectrumAnalysis.Analyze(new float[1024], 16_000);

        Assert.AreEqual(0d, frame.Level);
        Assert.IsTrue(frame.Bands.All(value => value == 0));
    }

    [TestMethod]
    public void Analyze_WhenToneIsAudible_ReturnsFrequencyShapedBands()
    {
        const int sampleRate = 16_000;
        var samples = new float[1024];
        for (var index = 0; index < samples.Length; index++)
        {
            samples[index] = 0.12f * MathF.Sin(2 * MathF.PI * 440 * index / sampleRate);
        }

        var frame = AudioSpectrumAnalysis.Analyze(samples, sampleRate);
        var dominantBand = Array.IndexOf(frame.Bands, frame.Bands.Max());

        Assert.IsTrue(frame.Level > 0.35);
        Assert.IsTrue(dominantBand is >= 2 and <= 5, $"unexpected dominant band: {dominantBand}");
        Assert.IsTrue(frame.Bands.Max() > frame.Bands.Min());
    }
}
