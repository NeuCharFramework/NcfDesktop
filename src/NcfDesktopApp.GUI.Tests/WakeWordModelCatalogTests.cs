using Microsoft.VisualStudio.TestTools.UnitTesting;
using NcfDesktopApp.GUI.Services;
using SharpCompress.Compressors;
using SharpCompress.Compressors.BZip2;
using System.Formats.Tar;
using System.Text;

namespace NcfDesktopApp.GUI.Tests;

[TestClass]
public sealed class WakeWordModelCatalogTests
{
    private string? _temporaryDirectory;

    [TestCleanup]
    public void Cleanup()
    {
        if (!string.IsNullOrWhiteSpace(_temporaryDirectory) && Directory.Exists(_temporaryDirectory))
        {
            Directory.Delete(_temporaryDirectory, recursive: true);
        }
    }

    [TestMethod]
    public void EvaluateDirectory_WhenModelIsMissing_ReportsNotReady()
    {
        var directory = CreateTemporaryDirectory();

        var readiness = WakeWordModelCatalog.EvaluateDirectory(directory);

        Assert.IsFalse(readiness.IsReady);
        StringAssert.Contains(readiness.Message, "尚未下载");
    }

    [TestMethod]
    public void EvaluateDirectory_WhenInt8FilesAndFixedKeywordAreValid_ReportsReady()
    {
        var directory = CreateCompleteModelDirectory();

        var readiness = WakeWordModelCatalog.EvaluateDirectory(directory);

        Assert.IsTrue(readiness.IsReady);
        Assert.IsNotNull(readiness.Files);
        Assert.AreEqual(WakeWordModelCatalog.EncoderFileName, Path.GetFileName(readiness.Files.Encoder));
        StringAssert.Contains(readiness.Message, WakeWordModelCatalog.WakePhraseDisplay);
    }

    [TestMethod]
    public void EvaluateDirectory_WhenKeywordWasChanged_RejectsModel()
    {
        var directory = CreateCompleteModelDirectory();
        File.WriteAllText(
            Path.Combine(directory, WakeWordModelCatalog.KeywordsFileName),
            "n ǐ h ǎo @你好\n");

        var readiness = WakeWordModelCatalog.EvaluateDirectory(directory);

        Assert.IsFalse(readiness.IsReady);
        StringAssert.Contains(readiness.Message, "配置与当前应用版本不一致");
    }

    [TestMethod]
    public void ExtractRequiredModelFiles_WhenArchiveIsTarBZip2_ExtractsOnlyRuntimeFiles()
    {
        var directory = CreateTemporaryDirectory();
        var archivePath = Path.Combine(directory, "wake-model.tar.bz2");
        var outputPath = Path.Combine(directory, "output");
        Directory.CreateDirectory(outputPath);
        CreateTarBZip2(
            archivePath,
            WakeWordModelCatalog.EncoderFileName,
            WakeWordModelCatalog.DecoderFileName,
            WakeWordModelCatalog.JoinerFileName,
            WakeWordModelCatalog.TokensFileName,
            "unused-fp32-model.onnx");

        LocalWakeWordService.ExtractRequiredModelFiles(
            archivePath,
            outputPath,
            CancellationToken.None);

        Assert.IsTrue(File.Exists(Path.Combine(outputPath, WakeWordModelCatalog.EncoderFileName)));
        Assert.IsTrue(File.Exists(Path.Combine(outputPath, WakeWordModelCatalog.DecoderFileName)));
        Assert.IsTrue(File.Exists(Path.Combine(outputPath, WakeWordModelCatalog.JoinerFileName)));
        Assert.IsTrue(File.Exists(Path.Combine(outputPath, WakeWordModelCatalog.TokensFileName)));
        Assert.IsFalse(File.Exists(Path.Combine(outputPath, "unused-fp32-model.onnx")));
    }

    [TestMethod]
    public void ShouldListen_WhenFeatureIsDisabled_NeverUsesMicrophone()
    {
        var result = WakeWordListeningPolicy.ShouldListen(
            enabled: false,
            wakeModelReady: true,
            voiceModelReady: true,
            adminChatActive: true,
            adminChatBusy: false,
            voiceInputBusy: false,
            ttsPlaying: false,
            workspaceDisposed: false);

        Assert.IsFalse(result);
    }

    [TestMethod]
    public void DesktopUserSettings_DefaultsWakeWordToDisabled()
    {
        var settings = new DesktopUserSettings();

        Assert.IsFalse(settings.WakeWordEnabled);
    }

    [TestMethod]
    public void DownloadSources_UseOfficialSourceThenHttpsFallbacks()
    {
        Assert.IsTrue(WakeWordModelCatalog.DownloadSources.Count >= 2);
        Assert.AreEqual(WakeWordModelCatalog.DownloadUrl, WakeWordModelCatalog.DownloadSources[0].Url);
        Assert.IsTrue(WakeWordModelCatalog.DownloadSources.All(source =>
            Uri.TryCreate(source.Url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps));
    }

    [TestMethod]
    public async Task ValidateDownloadedArchiveAsync_WhenHashDoesNotMatch_RejectsArchive()
    {
        var directory = CreateTemporaryDirectory();
        var archivePath = Path.Combine(directory, WakeWordModelCatalog.ArchiveFileName);
        await using (var stream = new FileStream(
                         archivePath,
                         FileMode.CreateNew,
                         FileAccess.Write,
                         FileShare.None))
        {
            stream.SetLength(WakeWordModelCatalog.ApproximateDownloadBytes);
        }

        var exception = await Assert.ThrowsExceptionAsync<InvalidDataException>(() =>
            LocalWakeWordService.ValidateDownloadedArchiveAsync(
                archivePath,
                CancellationToken.None));

        StringAssert.Contains(exception.Message, "SHA-256");
    }

    [TestMethod]
    public void ShouldListen_WhenAllPrerequisitesAreReady_UsesMicrophone()
    {
        var result = WakeWordListeningPolicy.ShouldListen(
            enabled: true,
            wakeModelReady: true,
            voiceModelReady: true,
            adminChatActive: true,
            adminChatBusy: false,
            voiceInputBusy: false,
            ttsPlaying: false,
            workspaceDisposed: false);

        Assert.IsTrue(result);
    }

    [DataTestMethod]
    [DataRow(false, true)]
    [DataRow(true, false)]
    public void ShouldListen_WhenRequiredModelOrAdminChatIsUnavailable_DoesNotUseMicrophone(
        bool wakeModelReady,
        bool adminChatActive)
    {
        var result = WakeWordListeningPolicy.ShouldListen(
            enabled: true,
            wakeModelReady: wakeModelReady,
            voiceModelReady: true,
            adminChatActive: adminChatActive,
            adminChatBusy: false,
            voiceInputBusy: false,
            ttsPlaying: false,
            workspaceDisposed: false);

        Assert.IsFalse(result);
    }

    [DataTestMethod]
    [DataRow(true, false, false, false, false)]
    [DataRow(false, true, false, false, false)]
    [DataRow(false, false, true, false, false)]
    [DataRow(false, false, false, true, false)]
    [DataRow(false, false, false, false, true)]
    public void ShouldListen_WhenAnOperationConflicts_PausesListener(
        bool adminBusy,
        bool voiceBusy,
        bool ttsPlaying,
        bool workspaceDisposed,
        bool voiceModelMissing)
    {
        var result = WakeWordListeningPolicy.ShouldListen(
            enabled: true,
            wakeModelReady: true,
            voiceModelReady: !voiceModelMissing,
            adminChatActive: true,
            adminChatBusy: adminBusy,
            voiceInputBusy: voiceBusy,
            ttsPlaying: ttsPlaying,
            workspaceDisposed: workspaceDisposed);

        Assert.IsFalse(result);
    }

    private string CreateCompleteModelDirectory()
    {
        var directory = CreateTemporaryDirectory();
        CreateSparseFile(Path.Combine(directory, WakeWordModelCatalog.EncoderFileName), 4 * 1024L * 1024L);
        CreateSparseFile(Path.Combine(directory, WakeWordModelCatalog.DecoderFileName), 128 * 1024L);
        CreateSparseFile(Path.Combine(directory, WakeWordModelCatalog.JoinerFileName), 48 * 1024L);
        CreateSparseFile(Path.Combine(directory, WakeWordModelCatalog.TokensFileName), 1024L);
        File.WriteAllText(
            Path.Combine(directory, WakeWordModelCatalog.KeywordsFileName),
            WakeWordModelCatalog.KeywordsFileContent);
        return directory;
    }

    private string CreateTemporaryDirectory()
    {
        _temporaryDirectory = Path.Combine(Path.GetTempPath(), $"ncf-wake-word-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_temporaryDirectory);
        return _temporaryDirectory;
    }

    private static void CreateSparseFile(string path, long length)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.SetLength(length);
    }

    private static void CreateTarBZip2(string archivePath, params string[] entryNames)
    {
        using var tarStream = new MemoryStream();
        using (var writer = new TarWriter(tarStream, leaveOpen: true))
        {
            foreach (var entryName in entryNames)
            {
                var entry = new PaxTarEntry(TarEntryType.RegularFile, $"model/{entryName}")
                {
                    DataStream = new MemoryStream(Encoding.UTF8.GetBytes(entryName))
                };
                writer.WriteEntry(entry);
            }
        }

        tarStream.Position = 0;
        using var output = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var bzip2 = new BZip2Stream(output, CompressionMode.Compress, false);
        tarStream.CopyTo(bzip2);
        bzip2.Finish();
    }
}
