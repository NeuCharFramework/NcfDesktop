using Microsoft.VisualStudio.TestTools.UnitTesting;
using NcfDesktopApp.GUI.Services;
using SoundFlow.Enums;
using SoundFlow.Structs;

namespace NcfDesktopApp.GUI.Tests;

[TestClass]
public sealed class StreamingAudioDataProviderTests
{
    [TestMethod]
    public void Provider_PreservesOrderAndSignalsCompletionAfterDrain()
    {
        var format = new AudioFormat
        {
            SampleRate = 16_000,
            Channels = 1,
            Format = SampleFormat.F32,
            Layout = ChannelLayout.Mono
        };
        using var provider = new StreamingAudioDataProvider(format, 16_000);
        var ended = false;
        provider.EndOfStreamReached += (_, _) => ended = true;

        provider.AddSamples(new[] { 0.1f, 0.2f, 0.3f });
        provider.CompleteAdding();

        var output = new float[4];
        var count = provider.ReadBytes(output);
        var finalCount = provider.ReadBytes(output.AsSpan(count));

        Assert.AreEqual(3, count);
        Assert.AreEqual(0, finalCount);
        Assert.IsTrue(ended);
        CollectionAssert.AreEqual(new[] { 0.1f, 0.2f, 0.3f }, output[..count]);
    }

    [TestMethod]
    public void Provider_ExposesOnlyNewPlaybackSamplesForVisualization()
    {
        var format = new AudioFormat
        {
            SampleRate = 16_000,
            Channels = 1,
            Format = SampleFormat.F32,
            Layout = ChannelLayout.Mono
        };
        using var provider = new StreamingAudioDataProvider(format, 16_000);
        provider.AddSamples(new[] { 0.25f, 0.5f, 0.75f });
        var playbackBuffer = new float[3];
        provider.ReadBytes(playbackBuffer);

        var visualizationBuffer = new float[3];
        long version = 0;
        var firstRead = provider.TryCopyLatestSamples(visualizationBuffer, ref version, out var count);
        var duplicateRead = provider.TryCopyLatestSamples(visualizationBuffer, ref version, out _);

        Assert.IsTrue(firstRead);
        Assert.IsFalse(duplicateRead);
        Assert.AreEqual(3, count);
        CollectionAssert.AreEqual(playbackBuffer, visualizationBuffer);
    }
}
