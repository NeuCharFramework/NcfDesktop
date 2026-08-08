using Microsoft.VisualStudio.TestTools.UnitTesting;
using NcfDesktopApp.GUI.Services;

namespace NcfDesktopApp.GUI.Tests;

[TestClass]
public sealed class WorkspaceTabSelectionPolicyTests
{
    [TestMethod]
    public void GetNextSelectedIndex_WhenNoWorkspaceRemains_ReturnsNone()
    {
        Assert.AreEqual(-1, WorkspaceTabSelectionPolicy.GetNextSelectedIndex(0, 0));
    }

    [TestMethod]
    public void GetNextSelectedIndex_WhenClosingMiddleWorkspace_SelectsNextWorkspace()
    {
        Assert.AreEqual(1, WorkspaceTabSelectionPolicy.GetNextSelectedIndex(1, 2));
    }

    [TestMethod]
    public void GetNextSelectedIndex_WhenClosingLastWorkspace_SelectsPreviousWorkspace()
    {
        Assert.AreEqual(1, WorkspaceTabSelectionPolicy.GetNextSelectedIndex(2, 2));
    }
}
