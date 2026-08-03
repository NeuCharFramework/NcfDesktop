using Microsoft.VisualStudio.TestTools.UnitTesting;
using NcfDesktopApp.GUI.Services;

namespace NcfDesktopApp.GUI.Tests;

[TestClass]
public sealed class TtsTextNormalizerTests
{
    [TestMethod]
    public void Normalize_RemovesMarkdownUrlsAndCodeBodies()
    {
        var result = TtsTextNormalizer.Normalize(
            "请查看 [NCF 文档](https://example.test/docs)。\n```csharp\nDeleteEverything();\n```\n![图](image.png)");

        StringAssert.Contains(result, "请查看 NCF 文档");
        StringAssert.Contains(result, "代码内容已省略");
        Assert.IsFalse(result.Contains("https://", StringComparison.Ordinal));
        Assert.IsFalse(result.Contains("DeleteEverything", StringComparison.Ordinal));
        Assert.IsFalse(result.Contains("image.png", StringComparison.Ordinal));
    }

    [TestMethod]
    public void SplitForSpeech_LongReplyProducesBoundedNonEmptyChunks()
    {
        var text = string.Concat(Enumerable.Repeat("这是一个用于测试本地朗读分段的完整句子。", 40));

        var chunks = TtsTextNormalizer.SplitForSpeech(text);

        Assert.IsTrue(chunks.Count > 1);
        Assert.IsTrue(chunks.All(chunk => chunk.Length is > 0 and <= 280));
    }

    [TestMethod]
    public void StreamingBuffer_EmitsCompletedSentenceBeforeFinalMessage()
    {
        var buffer = new StreamingTtsTextBuffer();

        var first = buffer.Append("这是第一段完整的话。后面");
        var second = buffer.Append("是第二段完整的话。");

        CollectionAssert.AreEqual(new[] { "这是第一段完整的话。" }, first.ToArray());
        CollectionAssert.AreEqual(new[] { "后面是第二段完整的话。" }, second.ToArray());
    }

    [TestMethod]
    public void StreamingBuffer_DoesNotEmitIncompleteCodeFence()
    {
        var buffer = new StreamingTtsTextBuffer();

        var beforeClosingFence = buffer.Append(
            "说明如下：```csharp\nConsole.WriteLine(\"不应朗读。\");");
        var afterClosingFence = buffer.Append("\n```处理已经完成。");

        Assert.AreEqual(0, beforeClosingFence.Count);
        Assert.AreEqual(1, afterClosingFence.Count);
        StringAssert.Contains(afterClosingFence[0], "代码内容已省略");
        StringAssert.Contains(afterClosingFence[0], "处理已经完成");
        Assert.IsFalse(afterClosingFence[0].Contains("Console.WriteLine", StringComparison.Ordinal));
    }

    [TestMethod]
    public void StreamingBuffer_FallbackFinalMessageIsReadWhenNoTokensArrived()
    {
        var buffer = new StreamingTtsTextBuffer();

        var chunks = buffer.Complete("旧版接口也能够正常完成朗读。");

        CollectionAssert.AreEqual(new[] { "旧版接口也能够正常完成朗读。" }, chunks.ToArray());
    }

    [TestMethod]
    public void StreamingBuffer_DoesNotSplitInsideBareUrl()
    {
        var buffer = new StreamingTtsTextBuffer();

        var streaming = buffer.Append("相关资料位于 https://example.test/docs/v1.2?q=test。 ");
        var completed = buffer.Complete("相关资料位于 https://example.test/docs/v1.2?q=test。 ");

        Assert.AreEqual(0, streaming.Count);
        CollectionAssert.AreEqual(new[] { "相关资料位于" }, completed.ToArray());
    }
}
