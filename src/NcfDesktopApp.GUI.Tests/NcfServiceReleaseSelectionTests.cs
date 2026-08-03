using Microsoft.VisualStudio.TestTools.UnitTesting;
using NcfDesktopApp.GUI.Services;

namespace NcfDesktopApp.GUI.Tests;

[TestClass]
public sealed class NcfServiceReleaseSelectionTests
{
    private string _testRoot = null!;

    [TestInitialize]
    public void Initialize()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "ncf-runtime-package-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testRoot);
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
    public void GetTargetAsset_WhenDesktopAssetComesFirst_SelectsHostRuntimeZip()
    {
        using var httpClient = new HttpClient();
        var service = new NcfService(httpClient);
        var release = new GitHubRelease
        {
            TagName = "v-test",
            Assets = CreateMixedAssets()
        };

        var selected = service.GetTargetAsset(release);

        Assert.IsNotNull(selected);
        Assert.IsTrue(selected.Name!.StartsWith("ncf-", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(selected.Name.StartsWith("ncf-desktop-", StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(selected.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void GetTargetAsset_WhenRuntimeAssetIsDuplicated_ReturnsNull()
    {
        using var httpClient = new HttpClient();
        var service = new NcfService(httpClient);
        var assets = CreateMixedAssets().ToList();
        var selectedHostAsset = service.GetTargetAsset(new GitHubRelease { Assets = assets.ToArray() })!;
        assets.Add(new GitHubAsset
        {
            Name = selectedHostAsset.Name!.Replace(".zip", "-duplicate.zip", StringComparison.OrdinalIgnoreCase),
            BrowserDownloadUrl = "https://example.test/duplicate.zip",
            Size = 1
        });

        var selected = service.GetTargetAsset(new GitHubRelease { Assets = assets.ToArray() });

        Assert.IsNull(selected);
    }

    [TestMethod]
    public void ValidateExtractedRuntimePackage_RequiresExactlyOneSenparcWebDll()
    {
        Assert.ThrowsException<InvalidDataException>(() =>
            NcfService.ValidateExtractedRuntimePackage(_testRoot));

        var appDirectory = Path.Combine(_testRoot, "app");
        Directory.CreateDirectory(appDirectory);
        File.WriteAllBytes(Path.Combine(appDirectory, "Senparc.Web.dll"), []);
        NcfService.ValidateExtractedRuntimePackage(_testRoot);

        File.WriteAllBytes(Path.Combine(_testRoot, "Senparc.Web.dll"), []);
        Assert.ThrowsException<InvalidDataException>(() =>
            NcfService.ValidateExtractedRuntimePackage(_testRoot));
    }

    [TestMethod]
    public void ApplyMirrorBaseToPackageDownloadUrl_PreservesHostSubfolder()
    {
        using var httpClient = new HttpClient();
        var service = new NcfService(httpClient)
        {
            MirrorServerBaseUrl = "https://mirror.example.test/root/"
        };

        var resolved = service.ApplyMirrorBaseToPackageDownloadUrl(
            "https://www.ncf.pub/NcfPackages/host/v-test/ncf-osx-arm64-v-test.zip");

        Assert.AreEqual(
            "https://mirror.example.test/root/NcfPackages/host/v-test/ncf-osx-arm64-v-test.zip",
            resolved);
    }

    private static GitHubAsset[] CreateMixedAssets()
    {
        var runtimeIdentifiers = new[]
        {
            "win-x64",
            "win-arm64",
            "osx-x64",
            "osx-arm64",
            "linux-x64",
            "linux-arm64"
        };
        var assets = new List<GitHubAsset>();
        foreach (var runtimeIdentifier in runtimeIdentifiers)
        {
            assets.Add(new GitHubAsset
            {
                Name = $"ncf-desktop-{runtimeIdentifier}-v-test.zip",
                BrowserDownloadUrl = $"https://example.test/ncf-desktop-{runtimeIdentifier}.zip",
                Size = 1
            });
            assets.Add(new GitHubAsset
            {
                Name = $"ncf-{runtimeIdentifier}-v-test.zip",
                BrowserDownloadUrl = $"https://example.test/ncf-{runtimeIdentifier}.zip",
                Size = 1
            });
        }

        return assets.ToArray();
    }
}
