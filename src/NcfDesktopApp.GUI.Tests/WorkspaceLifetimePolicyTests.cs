using Microsoft.VisualStudio.TestTools.UnitTesting;
using NcfDesktopApp.GUI.Services;

namespace NcfDesktopApp.GUI.Tests;

[TestClass]
public sealed class WorkspaceLifetimePolicyTests
{
    [TestMethod]
    public void ShouldShutdown_WhenAnotherWorkspaceRemains_ReturnsFalse()
    {
        Assert.IsFalse(WorkspaceLifetimePolicy.ShouldShutdown(
            activeWorkspaceCount: 1,
            pendingCloseOperations: 0));
    }

    [TestMethod]
    public void ShouldShutdown_WhenLastWorkspaceCleanupFinishes_ReturnsTrue()
    {
        Assert.IsTrue(WorkspaceLifetimePolicy.ShouldShutdown(
            activeWorkspaceCount: 0,
            pendingCloseOperations: 0));
    }

    [TestMethod]
    public void ShouldShutdown_WhenLastWorkspaceStillCleaning_ReturnsFalse()
    {
        Assert.IsFalse(WorkspaceLifetimePolicy.ShouldShutdown(
            activeWorkspaceCount: 0,
            pendingCloseOperations: 1));
    }
}
