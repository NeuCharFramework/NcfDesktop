/*----------------------------------------------------------------
    Copyright (C) 2026 Senparc

    文件名：NeuBellProjection.cs
    文件功能描述：将纽铃聚合快照安全投影为桌面宠物徽标和同源处理地址

    创建标识：Senparc - 20260807
----------------------------------------------------------------*/

using System;
using System.Linq;
using NcfDesktopApp.GUI.Models;

namespace NcfDesktopApp.GUI.Services;

internal sealed record NeuBellPresentation(
    int Count,
    string BadgeText,
    string Summary,
    Uri? TargetUri);

internal static class NeuBellProjection
{
    internal static NeuBellPresentation Create(NeuBellState? state, string? siteUrl)
    {
        var providers = state?.Providers ?? [];
        var activeItems = providers
            .SelectMany(provider => provider.Items ?? [])
            .Where(item => item.Count > 0)
            .ToArray();
        var total = activeItems.Aggregate(
            0L,
            (sum, item) => Math.Min(int.MaxValue, sum + Math.Max(0, (long)item.Count)));
        if (total <= 0)
        {
            return new NeuBellPresentation(0, string.Empty, "当前没有纽铃提醒", null);
        }

        var preferredItem = activeItems
            .OrderByDescending(item => GetSeverityPriority(item.Severity))
            .ThenByDescending(item => item.UpdatedAt)
            .ThenByDescending(item => item.Count)
            .FirstOrDefault(item => TryCreateSafeTarget(siteUrl, item.DetailUrl, out _));

        Uri? targetUri = null;
        if (preferredItem != null)
        {
            _ = TryCreateSafeTarget(siteUrl, preferredItem.DetailUrl, out targetUri);
        }
        else
        {
            // Provider 没有给出可用详情地址时，退回同源 Admin 首页；绝不打开跨站 URL。
            _ = SiteEndpointPolicy.TryCreateEndpoint(
                siteUrl,
                "/Admin/Index",
                out targetUri,
                out _);
        }

        var count = (int)total;
        var badgeText = count > 99 ? "99+" : count.ToString();
        var summary = preferredItem == null
            ? $"纽铃有 {count} 条待处理提醒，点击进入后台查看"
            : activeItems.Length == 1
                ? BuildItemSummary(preferredItem)
                : $"纽铃有 {count} 条提醒；优先处理：{BuildItemSummary(preferredItem)}";
        return new NeuBellPresentation(count, badgeText, summary, targetUri);
    }

    private static string BuildItemSummary(NeuBellItemSnapshot item)
    {
        var title = string.IsNullOrWhiteSpace(item.Title) ? "待处理提醒" : item.Title.Trim();
        return string.IsNullOrWhiteSpace(item.Summary)
            ? title
            : $"{title}：{item.Summary.Trim()}";
    }

    private static bool TryCreateSafeTarget(string? siteUrl, string? detailUrl, out Uri? targetUri)
    {
        targetUri = null;
        if (string.IsNullOrWhiteSpace(detailUrl) ||
            !SiteEndpointPolicy.TryCreateEndpoint(siteUrl, detailUrl.Trim(), out var endpoint, out _))
        {
            return false;
        }

        targetUri = endpoint;
        return true;
    }

    private static int GetSeverityPriority(string? severity) => severity?.Trim().ToLowerInvariant() switch
    {
        "error" => 4,
        "warning" => 3,
        "success" => 2,
        "info" => 1,
        _ => 0
    };
}
