using Microsoft.VisualStudio.TestTools.UnitTesting;
using NcfDesktopApp.GUI.Services;

namespace NcfDesktopApp.GUI.Tests;

[TestClass]
public sealed class ScrollPositionPolicyTests
{
    [TestMethod]
    public void WasNearBottom_WhenContentGrowsFromBottom_KeepsViewPinned()
    {
        Assert.IsTrue(ScrollPositionPolicy.WasNearBottom(
            extentHeight: 1_240,
            viewportHeight: 400,
            offsetY: 800,
            extentDeltaY: 40,
            viewportDeltaY: 0,
            offsetDeltaY: 0));
    }

    [TestMethod]
    public void WasNearBottom_WhenViewportShrinksFromBottom_KeepsViewPinned()
    {
        Assert.IsTrue(ScrollPositionPolicy.WasNearBottom(
            extentHeight: 1_200,
            viewportHeight: 360,
            offsetY: 800,
            extentDeltaY: 0,
            viewportDeltaY: -40,
            offsetDeltaY: 0));
    }

    [TestMethod]
    public void WasNearBottom_WhenUserWasReadingHistory_DoesNotMoveView()
    {
        Assert.IsFalse(ScrollPositionPolicy.WasNearBottom(
            extentHeight: 1_240,
            viewportHeight: 400,
            offsetY: 500,
            extentDeltaY: 40,
            viewportDeltaY: 0,
            offsetDeltaY: 0));
    }

    [DataTestMethod]
    [DataRow(768, true)]
    [DataRow(767, false)]
    public void IsNearBottom_UsesTolerance(double offsetY, bool expected)
    {
        Assert.AreEqual(expected, ScrollPositionPolicy.IsNearBottom(
            extentHeight: 1_200,
            viewportHeight: 400,
            offsetY: offsetY));
    }
}
