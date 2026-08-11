/*----------------------------------------------------------------
    Copyright (C) 2026 Senparc

    文件名：AgentPortalUsageSummary.cs
    文件功能描述：门户所需的已授权任务用量摘要

    创建标识：Senparc - 20260810
    
    修改标识：Senparc - 20260812
    修改描述：v0.10.0 完善桌面端唤醒词会话激活与中英文提示

----------------------------------------------------------------*/

using System;
using System.Collections.Generic;
using System.Linq;

namespace NcfDesktopApp.GUI.Models;

/// <summary>AgentsManager 单个任务的只读用量统计响应。</summary>
public sealed class AgentTaskUsageAnalytics
{
    public AgentTaskUsageOverview Overview { get; set; } = new();
}

/// <summary>不包含对话正文的 Token 与响应时延汇总。</summary>
public sealed class AgentTaskUsageOverview
{
    public int MessageCount { get; set; }

    public int PromptTokens { get; set; }

    public int CompletionTokens { get; set; }

    public int TotalTokens { get; set; }

    public double AverageResponseMilliseconds { get; set; }

    public int P95ResponseMilliseconds { get; set; }
}

/// <summary>
/// 浮动门户显示的活动任务用量采样。它不是费用统计，也不代表所有历史任务，
/// 因此同时保留运行任务数与实际已查询任务数。
/// </summary>
public sealed record AgentPortalUsageSummary(
    bool IsAvailable,
    int RunningTaskCount,
    int SampledTaskCount,
    int MessageCount,
    long PromptTokens,
    long CompletionTokens,
    long TotalTokens,
    double AverageResponseMilliseconds,
    int P95ResponseMilliseconds,
    DateTimeOffset ObservedAt)
{
    public static AgentPortalUsageSummary Unavailable { get; } = new(
        false, 0, 0, 0, 0, 0, 0, 0, 0, DateTimeOffset.MinValue);

    public static AgentPortalUsageSummary Create(
        int runningTaskCount,
        IReadOnlyCollection<AgentTaskUsageAnalytics> analytics,
        DateTimeOffset observedAt)
    {
        var overviews = analytics.Select(item => item.Overview ?? new AgentTaskUsageOverview()).ToArray();
        var messageCount = overviews.Sum(item => Math.Max(0, item.MessageCount));
        var weightedResponseMilliseconds = messageCount == 0
            ? 0
            : overviews.Sum(item => Math.Max(0, item.MessageCount) * Math.Max(0, item.AverageResponseMilliseconds)) /
              messageCount;

        return new AgentPortalUsageSummary(
            true,
            Math.Max(0, runningTaskCount),
            overviews.Length,
            messageCount,
            overviews.Sum(item => (long)Math.Max(0, item.PromptTokens)),
            overviews.Sum(item => (long)Math.Max(0, item.CompletionTokens)),
            overviews.Sum(item => (long)Math.Max(0, item.TotalTokens)),
            weightedResponseMilliseconds,
            overviews.Length == 0 ? 0 : overviews.Max(item => Math.Max(0, item.P95ResponseMilliseconds)),
            observedAt);
    }
}
