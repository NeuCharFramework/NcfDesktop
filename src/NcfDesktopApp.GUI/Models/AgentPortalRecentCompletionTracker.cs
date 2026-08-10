/*----------------------------------------------------------------
    Copyright (C) 2026 Senparc

    文件名：AgentPortalRecentCompletionTracker.cs
    文件功能描述：以连续 AgentsManager 快照确认刚完成任务的本地观测记录

    创建标识：Senparc - 20260810
----------------------------------------------------------------*/

using System;
using System.Collections.Generic;
using System.Linq;

namespace NcfDesktopApp.GUI.Models;

/// <summary>
/// 门户中可显示的“刚完成”记录。任务名称仅在上一帧活跃任务消失且完成计数增加时保留；
/// 其他情况只报告可验证的工作组完成增量，避免把取消或刷新丢失误判为完成。
/// </summary>
public sealed record AgentPortalRecentCompletion(
    int GroupId,
    string GroupLabel,
    string? TaskLabel,
    int CompletedTaskCount,
    DateTimeOffset ObservedAt);

/// <summary>在桌面进程内对连续只读快照做差，保留有限时长的完成记录。</summary>
public sealed class AgentPortalRecentCompletionTracker
{
    private static readonly TimeSpan Retention = TimeSpan.FromMinutes(5);
    private const int MaximumRecords = 4;

    private readonly Dictionary<int, int> _previousFinishedCounts = [];
    private readonly Dictionary<int, AgentGraphCollaboration> _previousActiveCollaborations = [];
    private readonly List<AgentPortalRecentCompletion> _recentCompletions = [];
    private bool _hasPreviousSnapshot;

    public IReadOnlyList<AgentPortalRecentCompletion> Update(
        AgentGraphSnapshot snapshot,
        DateTimeOffset observedAt)
    {
        var activeCollaborations = snapshot.Collaborations
            .Where(item => item.Status is 0 or 1 or 2)
            .ToArray();
        var activeTaskIds = activeCollaborations.Select(item => item.TaskId).ToHashSet();

        if (_hasPreviousSnapshot)
        {
            foreach (var group in snapshot.Groups)
            {
                var finishedCount = GetFinishedCount(group);
                if (!_previousFinishedCounts.TryGetValue(group.Id, out var previousFinishedCount) ||
                    finishedCount <= previousFinishedCount)
                {
                    continue;
                }

                var completedDelta = finishedCount - previousFinishedCount;
                var disappearedTasks = _previousActiveCollaborations.Values
                    .Where(item => item.GroupId == group.Id && !activeTaskIds.Contains(item.TaskId))
                    .OrderBy(item => item.TaskId)
                    .ToArray();

                // 只有一个完成增量且唯一的上帧任务消失时，才把任务名标为“刚完成”。
                // 多任务同时变化时名称归属无法由当前接口证明，故只显示工作组增量。
                var taskLabel = completedDelta == 1 && disappearedTasks.Length == 1
                    ? NormalizeLabel(disappearedTasks[0].TaskName, disappearedTasks[0].TaskId)
                    : null;
                _recentCompletions.Insert(0, new AgentPortalRecentCompletion(
                    group.Id,
                    NormalizeLabel(group.Name, group.Id),
                    taskLabel,
                    completedDelta,
                    observedAt));
            }
        }

        _previousFinishedCounts.Clear();
        foreach (var group in snapshot.Groups)
        {
            _previousFinishedCounts[group.Id] = GetFinishedCount(group);
        }

        _previousActiveCollaborations.Clear();
        foreach (var collaboration in activeCollaborations)
        {
            _previousActiveCollaborations[collaboration.TaskId] = collaboration;
        }

        _hasPreviousSnapshot = true;
        _recentCompletions.RemoveAll(item => observedAt - item.ObservedAt > Retention);
        if (_recentCompletions.Count > MaximumRecords)
        {
            _recentCompletions.RemoveRange(MaximumRecords, _recentCompletions.Count - MaximumRecords);
        }

        return _recentCompletions.ToArray();
    }

    public void Reset()
    {
        _previousFinishedCounts.Clear();
        _previousActiveCollaborations.Clear();
        _recentCompletions.Clear();
        _hasPreviousSnapshot = false;
    }

    private static int GetFinishedCount(AgentGraphGroup group) =>
        group.TaskStatusCounts.TryGetValue(3, out var count) ? Math.Max(0, count) : 0;

    private static string NormalizeLabel(string? value, int fallbackId)
    {
        var label = string.IsNullOrWhiteSpace(value) ? $"工作组 {fallbackId}" : value.Trim();
        return label.Length <= 36 ? label : label[..35] + "…";
    }
}
