/*----------------------------------------------------------------
    Copyright (C) 2026 Senparc

    文件名：MainWindowViewModel.AgentPortal.cs
    文件功能描述：按需同步 AgentsManager 聚合快照到桌面宠物空间门户

    创建标识：Senparc - 20260807
----------------------------------------------------------------*/

using System;
using System.Threading;
using System.Threading.Tasks;
using NcfDesktopApp.GUI.Models;
using NcfDesktopApp.GUI.Services;

namespace NcfDesktopApp.GUI.ViewModels;

public partial class MainWindowViewModel
{
    private CancellationTokenSource? _agentPortalSyncCancellation;
    private Task? _agentPortalSyncTask;

    private void StartAgentPortalSynchronization()
    {
        StopAgentPortalSynchronization(resetPortal: false);
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
}
