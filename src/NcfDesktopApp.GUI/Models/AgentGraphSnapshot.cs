/*----------------------------------------------------------------
    Copyright (C) 2026 Senparc

    文件名：AgentGraphSnapshot.cs
    文件功能描述：AgentsManager 3D 工作图的桌面只读投影协议

    创建标识：Senparc - 20260807
    
    修改标识：Senparc - 20260812
    修改描述：v0.10.0 完善桌面端唤醒词会话激活与中英文提示

----------------------------------------------------------------*/

using System.Collections.Generic;

namespace NcfDesktopApp.GUI.Models;

public sealed class AgentGraphSnapshot
{
    public List<AgentGraphAgent> Agents { get; set; } = [];

    public List<AgentGraphGroup> Groups { get; set; } = [];

    public List<AgentGraphLink> Links { get; set; } = [];

    public List<AgentGraphCollaboration> Collaborations { get; set; } = [];
}

public sealed class AgentGraphAgent
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>AgentsManager 中与提示词版本关联的评估均分；小于 0 表示尚无评分。</summary>
    public float Score { get; set; } = -1;

    public int ChattingCount { get; set; }

    public bool Enable { get; set; }
}

public sealed class AgentGraphCollaboration
{
    public int TaskId { get; set; }

    public int GroupId { get; set; }

    public string TaskName { get; set; } = string.Empty;

    public int Status { get; set; }

    public List<int> AgentIds { get; set; } = [];
}

/// <summary>AgentsManager 3D 图中的工作组摘要，不包含提示词或对话内容。</summary>
public sealed class AgentGraphGroup
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public bool Enable { get; set; }

    public int State { get; set; }

    public int RunningTaskCount { get; set; }

    public Dictionary<int, int> TaskStatusCounts { get; set; } = [];

    public List<int> MemberAgentIds { get; set; } = [];
}

/// <summary>AgentsManager 3D 图中 Agent 与工作组的成员关系。</summary>
public sealed class AgentGraphLink
{
    public int GroupId { get; set; }

    public int AgentId { get; set; }
}
