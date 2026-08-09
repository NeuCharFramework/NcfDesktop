using Microsoft.VisualStudio.TestTools.UnitTesting;
using NcfDesktopApp.GUI.Services;
using SharpCompress.Compressors;
using SharpCompress.Compressors.BZip2;
using System.Formats.Tar;
using System.Text;

namespace NcfDesktopApp.GUI.Tests;

[TestClass]
public sealed class TtsModelCatalogTests
{
    private string _testRoot = string.Empty;

    [TestInitialize]
    public void Initialize()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "ncf-tts-model-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testRoot);
        LocalizationService.Instance.Initialize("zh");
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, recursive: true);
        }
    }

    [TestMethod]
    public void Options_ShowEstimatedDownloadSizeInSelectorLabel()
    {
        var downloadable = TtsModelCatalog.Options.Where(option => option.CanDownload).ToArray();

        Assert.AreEqual(2, downloadable.Length);
        Assert.IsTrue(downloadable.All(option => option.DisplayLabel.Contains("MiB", StringComparison.Ordinal)));
        Assert.IsTrue(downloadable.All(option => option.ApproximateBytes > 100_000_000));
    }

    [TestMethod]
    public void EvaluateDirectory_WhenRequiredFilesExist_ReturnsResolvedModelFiles()
    {
        CreateSparseFile(Path.Combine(_testRoot, "model-int8.onnx"), 9L * 1024 * 1024);
        CreateSparseFile(Path.Combine(_testRoot, "voices.bin"), 2L * 1024 * 1024);
        File.WriteAllText(Path.Combine(_testRoot, "tokens.txt"), "token");
        File.WriteAllText(Path.Combine(_testRoot, "lexicon-us-en.txt"), "word w er d");
        Directory.CreateDirectory(Path.Combine(_testRoot, "espeak-ng-data"));

        var readiness = TtsModelCatalog.EvaluateDirectory(_testRoot);

        Assert.IsTrue(readiness.IsReady, readiness.Message);
        Assert.IsNotNull(readiness.Files);
        StringAssert.EndsWith(readiness.Files.Model, "model-int8.onnx");
        StringAssert.EndsWith(readiness.Files.Voices, "voices.bin");
    }

    [TestMethod]
    public void EvaluateDirectory_WhenModelIsIncomplete_ReturnsDiagnosticMessage()
    {
        File.WriteAllText(Path.Combine(_testRoot, "tokens.txt"), "token");

        var readiness = TtsModelCatalog.EvaluateDirectory(_testRoot);

        Assert.IsFalse(readiness.IsReady);
        StringAssert.Contains(readiness.Message, "不完整");
    }

    [TestMethod]
    public void ExtractArchiveSafely_WhenArchiveIsTarBZip2_ExtractsFiles()
    {
        var archivePath = Path.Combine(_testRoot, "model.tar.bz2");
        var outputPath = Path.Combine(_testRoot, "output");
        Directory.CreateDirectory(outputPath);
        CreateTarBZip2(archivePath, "kokoro/model.txt", "offline tts model");

        LocalTextToSpeechService.ExtractArchiveSafely(
            archivePath,
            outputPath,
            CancellationToken.None);

        var extracted = Path.Combine(outputPath, "kokoro", "model.txt");
        Assert.IsTrue(File.Exists(extracted));
        Assert.AreEqual("offline tts model", File.ReadAllText(extracted));
    }

    [TestMethod]
    public void ExtractArchiveSafely_WhenContentIsNotBZip2_ReturnsClearDiagnostic()
    {
        var archivePath = Path.Combine(_testRoot, "invalid.tar.bz2");
        var outputPath = Path.Combine(_testRoot, "output");
        Directory.CreateDirectory(outputPath);
        File.WriteAllText(archivePath, "not a model archive");

        var exception = Assert.ThrowsException<InvalidDataException>(() =>
            LocalTextToSpeechService.ExtractArchiveSafely(
                archivePath,
                outputPath,
                CancellationToken.None));

        StringAssert.Contains(exception.Message, "BZh");
    }

    private static void CreateSparseFile(string path, long length)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.SetLength(length);
    }

    private static void CreateTarBZip2(
        string archivePath,
        string entryName,
        string content)
    {
        using var tarStream = new MemoryStream();
        using (var writer = new TarWriter(tarStream, leaveOpen: true))
        {
            var entry = new UstarTarEntry(TarEntryType.RegularFile, entryName)
            {
                DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content))
            };
            writer.WriteEntry(entry);
        }

        tarStream.Position = 0;
        using var output = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var bzip2 = new BZip2Stream(output, CompressionMode.Compress, false);
        tarStream.CopyTo(bzip2);
        bzip2.Finish();
    }
}
