using Microsoft.VisualStudio.TestTools.UnitTesting;
using NcfDesktopApp.GUI.Services;

namespace NcfDesktopApp.GUI.Tests;

[TestClass]
public sealed class WebViewEditBridgeTests
{
    [TestMethod]
    public void ScriptBridge_IsDisabledForMacOSCallbackSafety()
    {
        Assert.IsFalse(WebViewEditBridge.IsScriptBridgeSupportedForPlatform(isMacOS: true));
    }

    [TestMethod]
    public void ScriptBridge_RemainsEnabledForOtherPlatforms()
    {
        Assert.IsTrue(WebViewEditBridge.IsScriptBridgeSupportedForPlatform(isMacOS: false));
    }

    [TestMethod]
    public void NativeMacSelectors_UseStandardResponderActions()
    {
        Assert.AreEqual("cut:", WebViewEditBridge.GetNativeMacSelector(WebViewEditBridge.EditCommand.Cut));
        Assert.AreEqual("copy:", WebViewEditBridge.GetNativeMacSelector(WebViewEditBridge.EditCommand.Copy));
        Assert.AreEqual("paste:", WebViewEditBridge.GetNativeMacSelector(WebViewEditBridge.EditCommand.Paste));
        Assert.AreEqual("selectAll:", WebViewEditBridge.GetNativeMacSelector(WebViewEditBridge.EditCommand.SelectAll));
        Assert.IsNull(WebViewEditBridge.GetNativeMacSelector(WebViewEditBridge.EditCommand.None));
    }

    [DataTestMethod]
    [DataRow("c", "Copy")]
    [DataRow("C", "Copy")]
    [DataRow("x", "Cut")]
    [DataRow("v", "Paste")]
    [DataRow("a", "SelectAll")]
    [DataRow("q", "None")]
    public void NativeMacCommandKey_MapsStandardEditShortcuts(
        string key,
        string expected)
    {
        var actual = WebViewEditBridge.GetNativeMacEditCommand(
            key,
            hasCommand: true,
            hasControl: false,
            hasAlternate: false,
            hasShift: false);

        Assert.AreEqual(expected, actual.ToString());
    }

    [DataTestMethod]
    [DataRow(false, false, false, false)]
    [DataRow(true, true, false, false)]
    [DataRow(true, false, true, false)]
    [DataRow(true, false, false, true)]
    public void NativeMacCommandKey_DoesNotHijackOtherModifierCombinations(
        bool hasCommand,
        bool hasControl,
        bool hasAlternate,
        bool hasShift)
    {
        var actual = WebViewEditBridge.GetNativeMacEditCommand(
            "v",
            hasCommand,
            hasControl,
            hasAlternate,
            hasShift);

        Assert.AreEqual(WebViewEditBridge.EditCommand.None, actual);
    }
}
