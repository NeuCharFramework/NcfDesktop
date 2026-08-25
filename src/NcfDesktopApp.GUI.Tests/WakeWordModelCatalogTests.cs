using Microsoft.VisualStudio.TestTools.UnitTesting;
using NcfDesktopApp.GUI.Models;
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
        StringAssert.Contains(readiness.Message, "自定义唤醒词");
    }

    [TestMethod]
    public void EvaluateDirectory_WhenKeywordWasChanged_AllowsCustomKeywordConfiguration()
    {
        var directory = CreateCompleteModelDirectory();
        File.WriteAllText(
            Path.Combine(directory, WakeWordModelCatalog.KeywordsFileName),
            "n ǐ h ǎo @你好\n");

        var readiness = WakeWordModelCatalog.EvaluateDirectory(directory);

        Assert.IsTrue(readiness.IsReady);
        StringAssert.Contains(readiness.Message, "自定义唤醒词");
    }

    [TestMethod]
    public void BuildConfiguredFiles_WhenChinesePhraseHasTonePinyin_WritesAliasAndMapsPhrase()
    {
        var directory = CreateCompleteModelDirectory(
            "n ǐ h ǎo x ī x ī :1.5 #0.35 @legacy\n");
        var outputDirectory = Path.Combine(directory, "active");
        var configuration = WakeWordModelCatalog.CreateDefaultConfiguration();

        var result = WakeWordModelCatalog.BuildConfiguredFiles(
            directory,
            new[] { configuration },
            outputDirectory);

        Assert.IsTrue(result.IsReady, result.Message);
        Assert.IsNotNull(result.Files);
        Assert.IsNotNull(result.Files.KeywordDefinitions);
        var alias = WakeWordModelCatalog.GetKeywordAlias(configuration);
        Assert.IsTrue(result.Files.KeywordDefinitions.ContainsKey(alias));
        StringAssert.Contains(
            File.ReadAllText(result.Files.Keywords),
            $"@{alias}");
        Assert.AreEqual(
            configuration.Phrase,
            result.Files.KeywordDefinitions[alias].Phrase);
    }

    [TestMethod]
    public void BuildConfiguredFiles_WhenChinesePhraseHasUntonedManualPinyin_UsesDerivedTonePinyin()
    {
        var directory = CreateCompleteModelDirectory();
        var outputDirectory = Path.Combine(directory, "active");
        var configuration = new WakeWordConfiguration
        {
            Phrase = "你好 小明",
            Pinyin = "ni hao xiao ming",
            IsEnabled = true
        };

        var result = WakeWordModelCatalog.BuildConfiguredFiles(
            directory,
            new[] { configuration },
            outputDirectory);

        Assert.IsTrue(result.IsReady, result.Message);
        Assert.IsNotNull(result.Files);
        var keywordContent = File.ReadAllText(result.Files.Keywords);
        StringAssert.Contains(keywordContent, "n ǐ h ǎo x iǎo m íng");
    }

    [TestMethod]
    public void BuildConfiguredFiles_WhenOnePhraseIsInvalid_KeepsOtherValidPhrasesListening()
    {
        var directory = CreateCompleteModelDirectory();
        var outputDirectory = Path.Combine(directory, "active");
        var validConfiguration = WakeWordModelCatalog.CreateDefaultConfiguration();
        var invalidConfiguration = new WakeWordConfiguration
        {
            Phrase = "Wake alpha",
            Pinyin = "invalid",
            IsEnabled = true
        };

        var result = WakeWordModelCatalog.BuildConfiguredFiles(
            directory,
            new[] { validConfiguration, invalidConfiguration },
            outputDirectory);

        Assert.IsTrue(result.IsReady, result.Message);
        Assert.IsNotNull(result.Files);
        Assert.AreEqual(1, result.Files.KeywordDefinitions?.Count);
        Assert.IsTrue(result.Validations.Single(validation =>
            validation.ConfigurationId == validConfiguration.Id).IsValid);
        Assert.IsFalse(result.Validations.Single(validation =>
            validation.ConfigurationId == invalidConfiguration.Id).IsValid);
        StringAssert.Contains(result.Message, "无效词条已跳过");
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

    [DataTestMethod]
    [DataRow(true, true, false, true)]
    [DataRow(true, true, true, false)]
    [DataRow(true, false, false, false)]
    [DataRow(false, true, false, false)]
    public void StartupPolicy_OnlyRequiresActivationForPersistedMacOsWakeWordSetting(
        bool isMacOS,
        bool wakeWordEnabled,
        bool explicitlyActivatedThisSession,
        bool expected)
    {
        var requiresActivation = WakeWordStartupPolicy.RequiresCurrentSessionActivation(
            isMacOS,
            wakeWordEnabled,
            explicitlyActivatedThisSession);

        Assert.AreEqual(expected, requiresActivation);
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
    public async Task WriteDownloadedFileAsync_ClosesDestinationBeforeReturning()
    {
        var directory = CreateTemporaryDirectory();
        var archivePath = Path.Combine(directory, "download.bin");
        var expected = Encoding.UTF8.GetBytes("wake-word-download");
        await using var source = new MemoryStream(expected);

        var written = await LocalWakeWordService.WriteDownloadedFileAsync(
            source,
            archivePath,
            progress: null,
            CancellationToken.None);

        Assert.AreEqual(expected.Length, written);
        await using var reopened = new FileStream(
            archivePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        using var copy = new MemoryStream();
        await reopened.CopyToAsync(copy);
        CollectionAssert.AreEqual(expected, copy.ToArray());
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
    [DataRow(false, true, false, false, false)]
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

    [TestMethod]
    public void ShouldListen_WhenAgentIsBusy_KeepsWakeListenerAvailable()
    {
        var result = WakeWordListeningPolicy.ShouldListen(
            enabled: true,
            wakeModelReady: true,
            voiceModelReady: true,
            adminChatActive: true,
            adminChatBusy: true,
            voiceInputBusy: false,
            ttsPlaying: false,
            workspaceDisposed: false);

        Assert.IsTrue(result);
    }

    [TestMethod]
    public void ShouldListen_WhenStreamingTtsIsPlayingDuringAdminReply_UsesMicrophone()
    {
        var result = WakeWordListeningPolicy.ShouldListen(
            enabled: true,
            wakeModelReady: true,
            voiceModelReady: true,
            adminChatActive: true,
            adminChatBusy: true,
            voiceInputBusy: false,
            ttsPlaying: true,
            workspaceDisposed: false);

        Assert.IsTrue(result);
    }

    private string CreateCompleteModelDirectory(
        string keywordContent = WakeWordModelCatalog.KeywordsFileContent)
    {
        var directory = CreateTemporaryDirectory();
        CreateSparseFile(Path.Combine(directory, WakeWordModelCatalog.EncoderFileName), 4 * 1024L * 1024L);
        CreateSparseFile(Path.Combine(directory, WakeWordModelCatalog.DecoderFileName), 128 * 1024L);
        CreateSparseFile(Path.Combine(directory, WakeWordModelCatalog.JoinerFileName), 48 * 1024L);
        File.WriteAllText(
            Path.Combine(directory, WakeWordModelCatalog.TokensFileName),
            "n 1\nǐ 2\nh 3\nǎo 4\nx 5\nī 6\niǎo 7\nm 8\níng 9\n");
        using (var tokens = new FileStream(
                   Path.Combine(directory, WakeWordModelCatalog.TokensFileName),
                   FileMode.Open,
                   FileAccess.Write,
                   FileShare.Read))
        {
            tokens.SetLength(Math.Max(tokens.Length, 1024));
        }
        File.WriteAllText(
            Path.Combine(directory, WakeWordModelCatalog.KeywordsFileName),
            keywordContent);
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
