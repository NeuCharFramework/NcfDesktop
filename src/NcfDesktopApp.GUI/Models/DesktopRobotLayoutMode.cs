/*----------------------------------------------------------------
    Copyright (C) 2026 Senparc

    文件名：DesktopRobotLayoutMode.cs
    文件功能描述：定义多个桌面宠物的展示方式

    创建标识：Senparc - 20260807

----------------------------------------------------------------*/

namespace NcfDesktopApp.GUI.Models;

/// <summary>
/// 多个工作区同时打开时，桌面宠物的窗口组织方式。
/// </summary>
public enum DesktopRobotLayoutMode
{
    /// <summary>每个工作区保留一个可独立拖动的浮动宠物。</summary>
    FreeFloating,

    /// <summary>所有工作区宠物收纳到一个置顶标签列表中。</summary>
    GroupedList
}

public static class DesktopRobotLayoutModePolicy
{
    public static DesktopRobotLayoutMode Normalize(DesktopRobotLayoutMode mode) =>
        mode is DesktopRobotLayoutMode.FreeFloating or DesktopRobotLayoutMode.GroupedList
            ? mode
            : DesktopRobotLayoutMode.FreeFloating;
}
