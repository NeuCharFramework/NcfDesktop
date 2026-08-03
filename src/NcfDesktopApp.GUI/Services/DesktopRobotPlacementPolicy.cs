using System;
using Avalonia;

namespace NcfDesktopApp.GUI.Services;

internal static class DesktopRobotPlacementPolicy
{
    public const double MinimumScale = 1.0;
    public const double DefaultMaximumScale = 3.0;
    public const double MaximumAllowedScale = 4.0;
    public const int DefaultScreenMargin = 18;

    public static double NormalizeMaximumScale(double value) =>
        double.IsFinite(value)
            ? Math.Clamp(value, MinimumScale, MaximumAllowedScale)
            : DefaultMaximumScale;

    public static double NormalizeScale(double value, double maximumScale)
    {
        var normalizedMaximum = NormalizeMaximumScale(maximumScale);
        return double.IsFinite(value)
            ? Math.Clamp(value, MinimumScale, normalizedMaximum)
            : MinimumScale;
    }

    public static bool IsFullyVisible(PixelPoint position, PixelSize windowSize, PixelRect workingArea) =>
        windowSize.Width > 0 &&
        windowSize.Height > 0 &&
        position.X >= workingArea.X &&
        position.Y >= workingArea.Y &&
        position.X + windowSize.Width <= workingArea.Right &&
        position.Y + windowSize.Height <= workingArea.Bottom;

    public static double GetMaximumScaleThatFits(
        PixelRect workingArea,
        double displayScaling,
        double scaledWidth,
        double scaledCardOffset,
        double fixedCardWidth,
        double scaledHeight,
        double fixedCardHeight,
        double windowPadding,
        int margin = DefaultScreenMargin)
    {
        var scaling = double.IsFinite(displayScaling) && displayScaling > 0 ? displayScaling : 1;
        var availableWidth = Math.Max(0, (workingArea.Width - margin * 2d) / scaling - windowPadding);
        var availableHeight = Math.Max(0, (workingArea.Height - margin * 2d) / scaling - windowPadding);
        var mascotWidthScale = scaledWidth > 0 ? availableWidth / scaledWidth : MinimumScale;
        var cardWidthScale = scaledCardOffset > 0
            ? (availableWidth - fixedCardWidth) / scaledCardOffset
            : MinimumScale;
        var heightScale = scaledHeight > 0 ? availableHeight / scaledHeight : MinimumScale;
        if (availableWidth < fixedCardWidth || availableHeight < fixedCardHeight)
        {
            return MinimumScale;
        }

        return Math.Clamp(
            Math.Min(Math.Min(mascotWidthScale, cardWidthScale), heightScale),
            MinimumScale,
            MaximumAllowedScale);
    }

    public static PixelPoint GetDefaultPosition(
        PixelSize windowSize,
        PixelRect workingArea,
        int margin = DefaultScreenMargin)
    {
        var x = Math.Max(workingArea.X + margin, workingArea.Right - windowSize.Width - margin);
        var y = Math.Max(workingArea.Y + margin, workingArea.Bottom - windowSize.Height - margin);
        return new PixelPoint(x, y);
    }
}
