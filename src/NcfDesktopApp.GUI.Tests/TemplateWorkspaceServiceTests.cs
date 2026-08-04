using Microsoft.VisualStudio.TestTools.UnitTesting;
using NcfDesktopApp.GUI.Models;
using NcfDesktopApp.GUI.Services;

namespace NcfDesktopApp.GUI.Tests;

[TestClass]
public sealed class TemplateWorkspaceServiceTests
{
    private string _testRoot = null!;

    [TestInitialize]
    public void Initialize()
    {
        _testRoot = Path.Combine(
            Path.GetTempPath(),
            "ncf-template-workspace-tests",
            Guid.NewGuid().ToString("N"));
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
    public void CopyConfigurationFiles_CopiesSupportedFilesAndOverwritesTemplateDefaults()
    {
        var source = CreateApplicationDirectory("source");
        var target = CreateApplicationDirectory("target");
        WriteConfiguration(source, "appsettings.json", "source-appsettings");
        WriteConfiguration(source, Path.Combine("App_Data", "Database", "SenparcConfig.config"), "source-senparc");
        WriteConfiguration(source, "Web.Config", "source-web");
        WriteConfiguration(target, "appsettings.json", "template-appsettings");
        WriteConfiguration(target, Path.Combine("App_Data", "Database", "SenparcConfig.config"), "template-senparc");

        var result = TemplateWorkspaceService.CopyConfigurationFiles(source, target, "测试工作区");

        Assert.AreEqual("source-appsettings", File.ReadAllText(Path.Combine(target, "appsettings.json")));
        Assert.AreEqual(
            "source-senparc",
            File.ReadAllText(Path.Combine(target, "App_Data", "Database", "SenparcConfig.config")));
        Assert.AreEqual("source-web", File.ReadAllText(Path.Combine(target, "web.config")));
        CollectionAssert.AreEquivalent(
            new[] { "appsettings.json", "App_Data/Database/SenparcConfig.config", "web.config" },
            result.CopiedFiles.ToArray());
        Assert.AreEqual(0, result.MissingFiles.Count);
    }

    [TestMethod]
    public void CopyConfigurationFiles_WhenOptionalWebConfigIsMissing_ReportsItWithoutFailing()
    {
        var source = CreateApplicationDirectory("source");
        var target = CreateApplicationDirectory("target");
        WriteConfiguration(source, "appsettings.json", "source-appsettings");
        WriteConfiguration(source, Path.Combine("App_Data", "Database", "SenparcConfig.config"), "source-senparc");

        var result = TemplateWorkspaceService.CopyConfigurationFiles(source, target, "测试工作区");

        Assert.AreEqual(2, result.CopiedFiles.Count);
        CollectionAssert.AreEqual(new[] { "web.config" }, result.MissingFiles.ToArray());
    }

    [TestMethod]
    public void ResolveConfigurationSource_WhenWorkspaceHasNoSupportedConfiguration_RejectsBeforeCreate()
    {
        var source = CreateApplicationDirectory("source");
        File.WriteAllText(
            Path.Combine(source, "Senparc.Web.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk.Web\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");

        var exception = Assert.ThrowsException<FileNotFoundException>(() =>
            TemplateWorkspaceService.ResolveConfigurationSource(
                TemplateWorkspaceConfigurationSourceKind.OtherWorkspace,
                source));

        StringAssert.Contains(exception.Message, "未找到 appsettings.json");
    }

    [TestMethod]
    public void ResolveConfigurationSource_WhenTemplateDefaultsSelected_DoesNotRequireAPath()
    {
        var result = TemplateWorkspaceService.ResolveConfigurationSource(
            TemplateWorkspaceConfigurationSourceKind.TemplateDefault,
            selectedPath: null);

        Assert.IsNull(result);
    }

    [TestMethod]
    public void ResolveConfigurationSource_WhenManagedPackageIsNested_UsesPublishedApplicationDirectory()
    {
        var runtimeRoot = CreateApplicationDirectory("runtime");
        var publishedApplication = Path.Combine(runtimeRoot, "package", "app");
        Directory.CreateDirectory(publishedApplication);
        File.WriteAllBytes(Path.Combine(publishedApplication, "Senparc.Web.dll"), Array.Empty<byte>());
        WriteConfiguration(publishedApplication, "appsettings.json", "managed-appsettings");

        var result = TemplateWorkspaceService.ResolveConfigurationSource(
            TemplateWorkspaceConfigurationSourceKind.ManagedRuntime,
            runtimeRoot);

        Assert.IsNotNull(result);
        Assert.AreEqual(publishedApplication, result.ApplicationDirectory);
        Assert.AreEqual("当前托管版本", result.Description);
    }

    private string CreateApplicationDirectory(string name)
    {
        var path = Path.Combine(_testRoot, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static void WriteConfiguration(string root, string relativePath, string content)
    {
        var path = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
