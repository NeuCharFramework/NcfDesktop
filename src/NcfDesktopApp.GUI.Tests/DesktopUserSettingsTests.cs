using NcfDesktopApp.GUI.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace NcfDesktopApp.GUI.Tests;

[TestClass]
public sealed class DesktopUserSettingsTests
{
    [TestMethod]
    public void SttAutoSend_DefaultsToDisabled()
    {
        var settings = new DesktopUserSettings();

        Assert.IsFalse(settings.SttAutoSend);
    }

    [TestMethod]
    public void DesktopRobotSettings_DefaultToCurrentSizeAndOptInWheelZoom()
    {
        var settings = new DesktopUserSettings();

        Assert.IsFalse(settings.DesktopRobotWheelZoomEnabled);
        Assert.AreEqual(DesktopRobotPlacementPolicy.MinimumScale, settings.DesktopRobotScale);
        Assert.AreEqual(DesktopRobotPlacementPolicy.DefaultMaximumScale, settings.DesktopRobotMaximumScale);
        Assert.IsNull(settings.DesktopRobotPositionX);
        Assert.IsNull(settings.DesktopRobotPositionY);
    }

    [TestMethod]
    public void DesktopRobotPlacementPolicy_NormalizesScaleAndMaximum()
    {
        Assert.AreEqual(1.0, DesktopRobotPlacementPolicy.NormalizeScale(.25, 3));
        Assert.AreEqual(2.5, DesktopRobotPlacementPolicy.NormalizeScale(2.5, 3));
        Assert.AreEqual(3.0, DesktopRobotPlacementPolicy.NormalizeScale(5, 3));
        Assert.AreEqual(4.0, DesktopRobotPlacementPolicy.NormalizeMaximumScale(9));
    }

    [TestMethod]
    public void DesktopRobotPlacementPolicy_RejectsOffScreenPositionAndBuildsDefault()
    {
        var workingArea = new Avalonia.PixelRect(0, 0, 1920, 1080);
        var windowSize = new Avalonia.PixelSize(360, 154);

        Assert.IsTrue(DesktopRobotPlacementPolicy.IsFullyVisible(
            new Avalonia.PixelPoint(100, 100),
            windowSize,
            workingArea));
        Assert.IsFalse(DesktopRobotPlacementPolicy.IsFullyVisible(
            new Avalonia.PixelPoint(1900, 100),
            windowSize,
            workingArea));
        Assert.AreEqual(
            new Avalonia.PixelPoint(1542, 908),
            DesktopRobotPlacementPolicy.GetDefaultPosition(windowSize, workingArea));
    }

    [TestMethod]
    public void DesktopRobotPlacementPolicy_LimitsScaleToVisibleWorkingArea()
    {
        var maximumScale = DesktopRobotPlacementPolicy.GetMaximumScaleThatFits(
            new Avalonia.PixelRect(0, 0, 800, 600),
            displayScaling: 1,
            scaledWidth: 112,
            scaledCardOffset: 88,
            fixedCardWidth: 256,
            scaledHeight: 112,
            fixedCardHeight: 138,
            windowPadding: 16);

        Assert.AreEqual(4.0, maximumScale, .001);
    }
}
