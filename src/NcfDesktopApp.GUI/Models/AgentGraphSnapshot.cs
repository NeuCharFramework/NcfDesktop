/*----------------------------------------------------------------
    Copyright (C) 2026 Senparc

    文件名：AgentGraphSnapshot.cs
    文件功能描述：AgentsManager 3D 工作图的桌面只读投影协议

    创建标识：Senparc - 20260807
----------------------------------------------------------------*/

using System.Collections.Generic;

namespace NcfDesktopApp.GUI.Models;

public sealed class AgentGraphSnapshot
{
    public List<AgentGraphAgent> Agents { get; set; } = [];

    public List<AgentGraphCollaboration> Collaborations { get; set; } = [];
}

public sealed class AgentGraphAgent
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

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
