/*----------------------------------------------------------------
    Copyright (C) 2026 Senparc

    文件名：MainWindowViewModel.AgentPortal.cs
    文件功能描述：按需同步 AgentsManager 聚合快照到桌面宠物空间门户

    创建标识：Senparc - 20260807
----------------------------------------------------------------*/

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NcfDesktopApp.GUI.Models;
using NcfDesktopApp.GUI.Services;

namespace NcfDesktopApp.GUI.ViewModels;

public partial class MainWindowViewModel
{
    private CancellationTokenSource? _agentPortalSyncCancellation;
    private Task? _agentPortalSyncTask;
    private DateTimeOffset _nextAgentPortalUsageRefreshUtc = DateTimeOffset.MinValue;

    private void StartAgentPortalSynchronization()
    {
        StopAgentPortalSynchronization(resetPortal: false);
        _nextAgentPortalUsageRefreshUtc = DateTimeOffset.MinValue;
        var parentToken = _cancellationTokenSource?.Token ?? CancellationToken.None;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(parentToken);
        _agentPortalSyncCancellation = cancellation;
        _agentPortalSyncTask = SynchronizeAgentPortalLoopAsync(cancellation.Token);
    }

    private void StopAgentPortalSynchronization(bool resetPortal = true)
    {
        var cancellation = Interlocked.Exchange(ref _agentPortalSyncCancellation, null);
        _agentPortalSyncTask = null;
        if (cancellation != null)
        {
            try
            {
                cancellation.Cancel();
            }
            finally
            {
                cancellation.Dispose();
            }
        }

        if (resetPortal)
        {
            _nextAgentPortalUsageRefreshUtc = DateTimeOffset.MinValue;
            Robot.SetAgentPortalUnavailable("等待 AgentsManager 活动");
        }
    }

    private async Task SynchronizeAgentPortalLoopAsync(CancellationToken cancellationToken)
    {
        var consecutiveFailures = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var snapshot = await _adminChatClient
                    .GetAgentGraphSnapshotAsync(SiteUrl, cancellationToken)
                    .ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                Robot.ApplyAgentGraphSnapshot(snapshot);
                if (Robot.IsAgentPortalOpen && DateTimeOffset.UtcNow >= _nextAgentPortalUsageRefreshUtc)
                {
                    var usage = await TryGetAgentPortalUsageAsync(snapshot, cancellationToken).ConfigureAwait(false);
                    Robot.ApplyAgentPortalUsage(usage ?? AgentPortalUsageSummary.Unavailable);
                    _nextAgentPortalUsageRefreshUtc = DateTimeOffset.UtcNow.AddSeconds(15);
                }
                consecutiveFailures = 0;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (AdminChatApiException ex)
            {
                consecutiveFailures++;
                Robot.SetAgentPortalUnavailable(
                    ex.IsAuthenticationFailure
                        ? "管理员授权已失效，Agents 空间已关闭"
                        : "未检测到已安装并启用的 AgentsManager");
            }
            catch
            {
                consecutiveFailures++;
                Robot.SetAgentPortalUnavailable("AgentsManager 状态暂不可用");
            }

            var delay = consecutiveFailures > 0
                ? TimeSpan.FromSeconds(Math.Min(15, 4 + consecutiveFailures * 2))
                : Robot.IsAgentPortalOpen
                    ? TimeSpan.FromSeconds(2)
                    : TimeSpan.FromSeconds(10);
            try
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// 门户展开后低频读取至多五个正在执行任务的历史用量聚合。这样既能显示真实 Token/时延，
    /// 又不会把每两秒的图快照轮询放大为对所有历史任务的扫描。
    /// </summary>
    private async Task<AgentPortalUsageSummary?> TryGetAgentPortalUsageAsync(
        AgentGraphSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        try
        {
            var runningTaskCount = GetRunningTaskCount(snapshot);
            var taskIds = snapshot.Collaborations
                .Where(item => item.Status == 1 && item.TaskId > 0)
                .Select(item => item.TaskId)
                .Distinct()
                .Take(5)
                .ToArray();
            if (runningTaskCount == 0 || taskIds.Length == 0)
            {
                return AgentPortalUsageSummary.Create(runningTaskCount, [], DateTimeOffset.UtcNow);
            }

            var requests = taskIds
                .Select(taskId => _adminChatClient.GetAgentTaskUsageAnalyticsAsync(SiteUrl, taskId, cancellationToken))
                .ToArray();
            var analytics = await Task.WhenAll(requests).ConfigureAwait(false);
            return AgentPortalUsageSummary.Create(runningTaskCount, analytics, DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // 用量端点在旧版 AgentsManager 中可能不存在；这不应影响实时工作态门户。
            return null;
        }
    }

    private static int GetRunningTaskCount(AgentGraphSnapshot snapshot)
    {
        var reportedTaskCount = snapshot.Groups.Sum(group =>
            group.TaskStatusCounts.TryGetValue(1, out var runningCount) ? Math.Max(0, runningCount) : 0);
        return Math.Max(reportedTaskCount, snapshot.Collaborations.Count(item => item.Status == 1));
    }
}
