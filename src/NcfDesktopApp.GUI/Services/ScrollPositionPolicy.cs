using System;

namespace NcfDesktopApp.GUI.Services;

/// <summary>
/// Keeps auto-scrolling views pinned only when they were already near the bottom.
/// </summary>
internal static class ScrollPositionPolicy
{
    internal const double DefaultTolerance = 32;

    internal static bool IsNearBottom(
        double extentHeight,
        double viewportHeight,
        double offsetY,
        double tolerance = DefaultTolerance)
    {
        var maximumOffset = Math.Max(0, extentHeight - viewportHeight);
        var distanceFromBottom = Math.Max(0, maximumOffset - offsetY);
        return distanceFromBottom <= tolerance;
    }

    internal static bool WasNearBottom(
        double extentHeight,
        double viewportHeight,
        double offsetY,
        double extentDeltaY,
        double viewportDeltaY,
        double offsetDeltaY,
        double tolerance = DefaultTolerance) =>
        IsNearBottom(
            extentHeight - extentDeltaY,
            viewportHeight - viewportDeltaY,
            offsetY - offsetDeltaY,
            tolerance);
}
