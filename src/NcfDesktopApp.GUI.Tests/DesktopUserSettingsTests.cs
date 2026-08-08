using NcfDesktopApp.GUI.Models;
using NcfDesktopApp.GUI.Services;
using NcfDesktopApp.GUI.Views;
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
        Assert.AreEqual(DesktopRobotLayoutMode.FreeFloating, settings.DesktopRobotLayoutMode);
        Assert.AreEqual(DesktopRobotPlacementPolicy.MinimumScale, settings.DesktopRobotScale);
        Assert.AreEqual(DesktopRobotPlacementPolicy.DefaultMaximumScale, settings.DesktopRobotMaximumScale);
        Assert.IsNull(settings.DesktopRobotPositionX);
        Assert.IsNull(settings.DesktopRobotPositionY);
    }

    [TestMethod]
    public void DesktopRobotLayoutModePolicy_InvalidValueFallsBackToFreeFloating()
    {
        Assert.AreEqual(
            DesktopRobotLayoutMode.GroupedList,
            DesktopRobotLayoutModePolicy.Normalize(DesktopRobotLayoutMode.GroupedList));
        Assert.AreEqual(
            DesktopRobotLayoutMode.FreeFloating,
            DesktopRobotLayoutModePolicy.Normalize((DesktopRobotLayoutMode)999));
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

    [TestMethod]
    public void DesktopRobotWindow_CollapsedLayout_ReleasesHiddenCardArea()
    {
        var expanded = DesktopRobotWindow.CalculateLayout(
            scale: 1,
            isCardExpanded: true,
            compactStatusWidth: 64);
        var collapsed = DesktopRobotWindow.CalculateLayout(
            scale: 1,
            isCardExpanded: false,
            compactStatusWidth: 64);

        Assert.AreEqual(400, expanded.WindowWidth, .001);
        Assert.AreEqual(174, collapsed.WindowWidth, .001);
        Assert.IsTrue(collapsed.WindowWidth < expanded.WindowWidth);
        Assert.AreEqual(154, expanded.WindowHeight, .001);
        Assert.AreEqual(128, collapsed.WindowHeight, .001);
        Assert.IsTrue(collapsed.WindowHeight < expanded.WindowHeight);
        Assert.AreEqual(13, expanded.MascotTop, .001);
        Assert.AreEqual(0, collapsed.MascotTop, .001);
        Assert.AreEqual(expanded.CardLeft, collapsed.CardLeft, .001);
        Assert.AreEqual(
            26,
            DesktopRobotWindow.CalculateWindowTopOffset(
                scale: 1,
                isCardExpanded: false,
                displayScaling: 2));
        Assert.AreEqual(
            0,
            DesktopRobotWindow.CalculateWindowTopOffset(
                scale: 1,
                isCardExpanded: true,
                displayScaling: 2));
    }

    [TestMethod]
    public void DesktopRobotWindow_HitRegions_ExcludeTransparentPixels()
    {
        var collapsed = DesktopRobotWindow.CalculateLayout(
            scale: 1,
            isCardExpanded: false,
            compactStatusWidth: 64);
        var collapsedRegions = DesktopRobotWindow.CalculateHitRegions(
            collapsed,
            isCardExpanded: false);

        Assert.IsTrue(collapsedRegions.Any(region => region.Contains(new Avalonia.Point(64, 64))));
        Assert.IsTrue(collapsedRegions.Any(region => region.Contains(new Avalonia.Point(134, 21))));
        Assert.IsFalse(collapsedRegions.Any(region => region.Contains(new Avalonia.Point(160, 100))));
        Assert.IsFalse(collapsedRegions.Any(region => region.Contains(new Avalonia.Point(1, 1))));

        var expanded = DesktopRobotWindow.CalculateLayout(
            scale: 1,
            isCardExpanded: true,
            compactStatusWidth: 64);
        var expandedRegions = DesktopRobotWindow.CalculateHitRegions(
            expanded,
            isCardExpanded: true);

        Assert.IsTrue(expandedRegions.Any(region => region.Contains(new Avalonia.Point(221, 77))));
        Assert.IsFalse(expandedRegions.Any(region => region.Contains(new Avalonia.Point(2, 2))));
    }

    [TestMethod]
    public void DesktopRobotWindow_FreeFloatingMode_OffsetsAdditionalPetsWithinWorkingArea()
    {
        var workingArea = new Avalonia.PixelRect(0, 0, 1920, 1080);
        var windowSize = new Avalonia.PixelSize(360, 154);
        var first = new Avalonia.PixelPoint(1542, 908);

        Assert.AreEqual(
            first,
            DesktopRobotWindow.CalculateInitialFreeFloatingPosition(
                first, windowSize, workingArea, offsetIndex: 0));
        Assert.AreEqual(
            new Avalonia.PixelPoint(1542, 796),
            DesktopRobotWindow.CalculateInitialFreeFloatingPosition(
                first, windowSize, workingArea, offsetIndex: 1));
        Assert.AreEqual(
            new Avalonia.PixelPoint(1352, 908),
            DesktopRobotWindow.CalculateInitialFreeFloatingPosition(
                first, windowSize, workingArea, offsetIndex: 5));
    }
}
