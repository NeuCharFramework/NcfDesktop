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
}
