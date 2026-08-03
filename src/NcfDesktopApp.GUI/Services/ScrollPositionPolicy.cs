/*----------------------------------------------------------------
    Copyright (C) 2026 Senparc

    文件名：ScrollPositionPolicy.cs
    文件功能描述：判断聊天与日志视图是否应保持自动滚动

    创建标识：Senparc - 20260803

    修改标识：Senparc - 20260804
    修改描述：v0.6.0 保持用户离开底部时的聊天与日志滚动位置

----------------------------------------------------------------*/

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
