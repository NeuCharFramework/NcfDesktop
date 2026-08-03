/*----------------------------------------------------------------
    Copyright (C) 2026 Senparc

    文件名：AudioVisualizationFrame.cs
    文件功能描述：语音输入与本地朗读共享的实时音频可视化数据

    创建标识：Senparc - 20260803
----------------------------------------------------------------*/

namespace NcfDesktopApp.GUI.Models;

public enum AudioVisualizationMode
{
    None,
    Listening,
    Speaking
}

/// <summary>
/// 供主窗口与桌面机器人绘制的归一化音频帧。Level 和 Bands 的范围均为 0 到 1。
/// </summary>
public sealed record AudioVisualizationFrame(double Level, double[] Bands)
{
    public static AudioVisualizationFrame Silent { get; } = new(0, new double[12]);
}
