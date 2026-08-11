/*----------------------------------------------------------------
    Copyright (C) 2026 Senparc

    文件名：AgentPortalRenderGraph.cs
    文件功能描述：将 AgentsManager 只读快照投影为桌面门户的安全实时拓扑

    创建标识：Senparc - 20260810
    
    修改标识：Senparc - 20260812
    修改描述：v0.10.0 完善桌面端唤醒词会话激活与中英文提示

----------------------------------------------------------------*/

using System;
using System.Collections.Generic;
using System.Linq;

namespace NcfDesktopApp.GUI.Models;

/// <summary>所有工作组任务状态的只读汇总。</summary>
public sealed record AgentPortalTaskSummary(
    int WaitingCount,
    int WorkingCount,
    int PausedCount,
    int FinishedCount,
    int CancelledCount,
    int FailedCount)
{
    public static AgentPortalTaskSummary Empty { get; } = new(0, 0, 0, 0, 0, 0);

    public int ActiveCount => WaitingCount + WorkingCount + PausedCount;

    public int TotalCount => ActiveCount + FinishedCount + CancelledCount + FailedCount;
}

/// <summary>门户可显示的 Agent 运行负载和配置健康度，不包含提示词正文或会话内容。</summary>
public sealed record AgentPortalAgentSummary(
    int TotalCount,
    int WorkingCount,
    int StandbyCount,
    int PausedCount,
    int DisabledCount,
    int ActiveAssignments,
    int ScoredCount,
    double AveragePromptScore)
{
    public static AgentPortalAgentSummary Empty { get; } = new(0, 0, 0, 0, 0, 0, 0, -1);

    public int EnabledCount => Math.Max(0, TotalCount - DisabledCount);

    public bool HasPromptScore => ScoredCount > 0 && AveragePromptScore >= 0;
}

/// <summary>
/// 供浮动门户绘制的实时图；仅包含 Agent、工作组、任务状态计数和成员关系，
/// 不携带提示词、对话内容、头像或其他业务负载。
/// </summary>
public sealed record AgentPortalRenderGraph(
    IReadOnlyList<AgentPortalRenderNode> Agents,
    IReadOnlyList<AgentPortalRenderGroup> Groups,
    IReadOnlyList<AgentPortalRenderLink> Links,
    IReadOnlyList<AgentPortalRenderCollaboration> Collaborations,
    AgentPortalTaskSummary TaskSummary,
    AgentPortalAgentSummary AgentSummary,
    bool UsesLiveSnapshot)
{
    public static AgentPortalRenderGraph Empty { get; } = new(
        [], [], [], [], AgentPortalTaskSummary.Empty, AgentPortalAgentSummary.Empty, false);

    public bool HasContent => Agents.Count > 0 || Groups.Count > 0;
}

public sealed record AgentPortalRenderNode(
    int Id,
    string Label,
    AgentPortalNodeState State,
    int ActiveTaskCount,
    bool IsEnabled,
    float PromptScore);

public sealed record AgentPortalRenderGroup(
    int Id,
    string Label,
    AgentPortalNodeState State,
    int ActiveTaskCount,
    bool IsEnabled,
    AgentPortalTaskSummary TaskSummary);

public sealed record AgentPortalRenderLink(
    int GroupId,
    int AgentId,
    bool IsActive,
    AgentPortalNodeState State);

public sealed record AgentPortalRenderCollaboration(
    int TaskId,
    int GroupId,
    string Label,
    IReadOnlyList<int> AgentIds,
    AgentPortalNodeState State);

public static class AgentPortalRenderGraphProjection
{
    /// <summary>
    /// 活动流的兜底节点上限。实时快照本身不会在此裁剪，确保任务和负载汇总覆盖全部工作组。
    /// </summary>
    public const int MaximumVisibleGroups = 12;

    /// <summary>
    /// 将完整快照压缩为适合浮动门户的图。快照为空时，才使用事件流节点；两者都为空则显示示意门户。
    /// </summary>
    public static AgentPortalRenderGraph Create(
        AgentGraphSnapshot? snapshot,
        IReadOnlyList<AgentPortalNode>? fallbackNodes)
    {
        if (snapshot?.Agents.Count > 0)
        {
            return CreateFromSnapshot(snapshot);
        }

        return CreateFromFallbackNodes(fallbackNodes);
    }

    private static AgentPortalRenderGraph CreateFromSnapshot(AgentGraphSnapshot snapshot)
    {
        var activeCollaborations = snapshot.Collaborations
            .Where(item => item.Status is 0 or 1 or 2)
            .ToArray();
        var collaborationStatesByAgent = activeCollaborations
            .SelectMany(item => item.AgentIds.Select(agentId => new { AgentId = agentId, item.Status }))
            .GroupBy(item => item.AgentId)
            .ToDictionary(group => group.Key, group => group.Select(item => item.Status).ToArray());

        var selectedAgents = snapshot.Agents
            .Select(agent => new
            {
                Agent = agent,
                State = GetAgentState(agent, collaborationStatesByAgent.GetValueOrDefault(agent.Id, [])),
                ActiveTaskCount = GetActiveTaskCount(agent, collaborationStatesByAgent.GetValueOrDefault(agent.Id, []))
            })
            .OrderByDescending(item => item.State == AgentPortalNodeState.Working)
            .ThenByDescending(item => item.State == AgentPortalNodeState.Info)
            .ThenByDescending(item => item.ActiveTaskCount)
            .ThenByDescending(item => item.Agent.Enable)
            .ThenBy(item => item.Agent.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        var visibleAgentIds = selectedAgents.Select(item => item.Agent.Id).ToHashSet();

        var selectedGroups = snapshot.Groups
            .Select(group => new
            {
                Group = group,
                TaskSummary = CreateTaskSummary(group.TaskStatusCounts, group.RunningTaskCount),
                State = GetGroupState(group, activeCollaborations),
                ActiveTaskCount = Math.Max(0, group.RunningTaskCount)
            })
            .OrderByDescending(item => item.State == AgentPortalNodeState.Working)
            .ThenByDescending(item => item.ActiveTaskCount)
            .ThenByDescending(item => item.Group.Enable)
            .ThenBy(item => item.Group.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        var visibleGroupIds = selectedGroups.Select(item => item.Group.Id).ToHashSet();

        var agents = selectedAgents
            .Select(item => new AgentPortalRenderNode(
                item.Agent.Id,
                NormalizeLabel(item.Agent.Name, $"Agent {item.Agent.Id}"),
                item.State,
                item.ActiveTaskCount,
                item.Agent.Enable,
                item.Agent.Score))
            .ToArray();
        var groups = selectedGroups
            .Select(item => new AgentPortalRenderGroup(
                item.Group.Id,
                NormalizeLabel(item.Group.Name, $"Group {item.Group.Id}"),
                item.State,
                item.ActiveTaskCount,
                item.Group.Enable,
                item.TaskSummary))
            .ToArray();
        var links = snapshot.Links
            .Where(item => visibleGroupIds.Contains(item.GroupId) && visibleAgentIds.Contains(item.AgentId))
            .Select(item =>
            {
                var states = collaborationStatesByAgent.GetValueOrDefault(item.AgentId, []);
                var status = GetLinkState(item.GroupId, states, activeCollaborations, item.AgentId);
                return new AgentPortalRenderLink(
                    item.GroupId,
                    item.AgentId,
                    status == AgentPortalNodeState.Working,
                    status);
            })
            .ToArray();
        var collaborations = activeCollaborations
            .Where(item => visibleGroupIds.Contains(item.GroupId))
            .Select(item => new AgentPortalRenderCollaboration(
                item.TaskId,
                item.GroupId,
                NormalizeLabel(item.TaskName, $"协作任务 {item.TaskId}"),
                item.AgentIds.Where(visibleAgentIds.Contains).ToArray(),
                StateFromTaskStatus(item.Status)))
            .ToArray();

        return new AgentPortalRenderGraph(
            agents,
            groups,
            links,
            collaborations,
            CombineTaskSummaries(groups.Select(item => item.TaskSummary)),
            CreateAgentSummary(agents),
            true);
    }

    private static AgentPortalRenderGraph CreateFromFallbackNodes(IReadOnlyList<AgentPortalNode>? fallbackNodes)
    {
        if (fallbackNodes is not { Count: > 0 })
        {
            return AgentPortalRenderGraph.Empty;
        }

        var agents = fallbackNodes
            .Take(AgentPortalProjection.MaximumVisibleNodes)
            .Select((node, index) => new AgentPortalRenderNode(
                index + 1,
                NormalizeLabel(node.Label, "Agent"),
                node.State,
                node.State == AgentPortalNodeState.Working ? 1 : 0,
                node.State != AgentPortalNodeState.Cancelled,
                -1))
            .ToArray();
        return new AgentPortalRenderGraph(
            agents,
            [],
            [],
            [],
            new AgentPortalTaskSummary(0, agents.Count(item => item.State == AgentPortalNodeState.Working), 0, 0, 0, 0),
            CreateAgentSummary(agents),
            false);
    }

    private static AgentPortalAgentSummary CreateAgentSummary(IReadOnlyCollection<AgentPortalRenderNode> agents)
    {
        // 已停用 Agent 不参与当前运行配置的评估均分，避免历史/废弃配置拉低实时判断。
        var scored = agents
            .Where(item => item.IsEnabled && item.PromptScore >= 0)
            .Select(item => (double)item.PromptScore)
            .ToArray();
        return new AgentPortalAgentSummary(
            agents.Count,
            agents.Count(item => item.State == AgentPortalNodeState.Working),
            agents.Count(item => item.State == AgentPortalNodeState.Waiting),
            agents.Count(item => item.State == AgentPortalNodeState.Info),
            agents.Count(item => item.State == AgentPortalNodeState.Cancelled),
            agents.Sum(item => Math.Max(0, item.ActiveTaskCount)),
            scored.Length,
            scored.Length == 0 ? -1 : scored.Average());
    }

    private static AgentPortalTaskSummary CombineTaskSummaries(IEnumerable<AgentPortalTaskSummary> summaries) =>
        new(
            summaries.Sum(item => item.WaitingCount),
            summaries.Sum(item => item.WorkingCount),
            summaries.Sum(item => item.PausedCount),
            summaries.Sum(item => item.FinishedCount),
            summaries.Sum(item => item.CancelledCount),
            summaries.Sum(item => item.FailedCount));

    private static AgentPortalTaskSummary CreateTaskSummary(
        IReadOnlyDictionary<int, int>? taskStatusCounts,
        int runningTaskCount)
    {
        static int Get(IReadOnlyDictionary<int, int>? counts, int status) =>
            counts is not null && counts.TryGetValue(status, out var value) ? Math.Max(0, value) : 0;

        var waiting = Get(taskStatusCounts, 0);
        var working = Get(taskStatusCounts, 1);
        var paused = Get(taskStatusCounts, 2);
        var reportedActive = waiting + working + paused;
        if (runningTaskCount > reportedActive)
        {
            // 兼容旧服务端只提供 RunningTaskCount 的响应：未知活动任务以“执行中”显示，绝不记作完成。
            working += runningTaskCount - reportedActive;
        }

        return new AgentPortalTaskSummary(
            waiting,
            working,
            paused,
            Get(taskStatusCounts, 3),
            Get(taskStatusCounts, 4),
            Get(taskStatusCounts, 5));
    }

    private static AgentPortalNodeState GetAgentState(AgentGraphAgent agent, IReadOnlyCollection<int> taskStates)
    {
        if (!agent.Enable)
        {
            return AgentPortalNodeState.Cancelled;
        }

        if (taskStates.Contains(1) || (taskStates.Count == 0 && agent.ChattingCount > 0))
        {
            return AgentPortalNodeState.Working;
        }

        if (taskStates.Contains(2))
        {
            return AgentPortalNodeState.Info;
        }

        return AgentPortalNodeState.Waiting;
    }

    private static int GetActiveTaskCount(AgentGraphAgent agent, IReadOnlyCollection<int> taskStates) =>
        Math.Max(Math.Max(0, agent.ChattingCount), taskStates.Count);

    private static AgentPortalNodeState GetGroupState(
        AgentGraphGroup group,
        IEnumerable<AgentGraphCollaboration> activeCollaborations)
    {
        if (!group.Enable)
        {
            return AgentPortalNodeState.Cancelled;
        }

        var states = activeCollaborations
            .Where(item => item.GroupId == group.Id)
            .Select(item => item.Status)
            .ToArray();
        if (states.Contains(1) || (states.Length == 0 && group.RunningTaskCount > 0))
        {
            return AgentPortalNodeState.Working;
        }

        return states.Contains(2) ? AgentPortalNodeState.Info : AgentPortalNodeState.Waiting;
    }

    private static AgentPortalNodeState GetLinkState(
        int groupId,
        IReadOnlyCollection<int> taskStates,
        IEnumerable<AgentGraphCollaboration> activeCollaborations,
        int agentId)
    {
        var states = activeCollaborations
            .Where(item => item.GroupId == groupId && item.AgentIds.Contains(agentId))
            .Select(item => item.Status)
            .ToArray();
        return states.Contains(1)
            ? AgentPortalNodeState.Working
            : states.Contains(2)
                ? AgentPortalNodeState.Info
                : taskStates.Contains(0)
                    ? AgentPortalNodeState.Waiting
                    : AgentPortalNodeState.Waiting;
    }

    private static AgentPortalNodeState StateFromTaskStatus(int status) => status switch
    {
        1 => AgentPortalNodeState.Working,
        2 => AgentPortalNodeState.Info,
        0 => AgentPortalNodeState.Waiting,
        4 => AgentPortalNodeState.Cancelled,
        5 => AgentPortalNodeState.Failed,
        _ => AgentPortalNodeState.Info
    };

    private static string NormalizeLabel(string? value, string fallback)
    {
        var label = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        return label.Length <= 20 ? label : label[..19] + "…";
    }
}
