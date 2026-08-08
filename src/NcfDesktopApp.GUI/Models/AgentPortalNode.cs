/*----------------------------------------------------------------
    Copyright (C) 2026 Senparc

    文件名：AgentPortalNode.cs
    文件功能描述：桌面宠物 Agents 空间门户的轻量状态投影模型

    创建标识：Senparc - 20260807
----------------------------------------------------------------*/

using System;

namespace NcfDesktopApp.GUI.Models;

public enum AgentPortalNodeState
{
    Waiting,
    Working,
    Succeeded,
    Failed,
    Cancelled,
    Info
}

/// <summary>
/// 门户只保留绘制所需的安全摘要，不携带提示词、密钥或完整业务数据。
/// </summary>
public sealed record AgentPortalNode(
    string Id,
    string Label,
    AgentPortalNodeState State,
    double Progress,
    DateTimeOffset Time);

public static class AgentPortalProjection
{
    public const int MaximumVisibleNodes = 8;

    public static bool IsAgentsManagerActivity(DesktopActivityMessage activity) =>
        string.Equals(activity.Source, "AgentsManager", StringComparison.OrdinalIgnoreCase);

    public static AgentPortalNode Project(DesktopActivityMessage activity)
    {
        var label = string.IsNullOrWhiteSpace(activity.Title) ? "Agent" : activity.Title.Trim();
        if (label.Length > 24)
        {
            label = label[..23] + "…";
        }

        return new AgentPortalNode(
            string.IsNullOrWhiteSpace(activity.ActivityId) ? activity.Sequence.ToString() : activity.ActivityId,
            label,
            activity.State switch
            {
                "Working" => AgentPortalNodeState.Working,
                "Succeeded" => AgentPortalNodeState.Succeeded,
                "Failed" => AgentPortalNodeState.Failed,
                "Cancelled" => AgentPortalNodeState.Cancelled,
                "Info" => AgentPortalNodeState.Info,
                _ => AgentPortalNodeState.Waiting
            },
            Math.Clamp(activity.Progress ?? 0, 0, 100),
            activity.Time);
    }
}
