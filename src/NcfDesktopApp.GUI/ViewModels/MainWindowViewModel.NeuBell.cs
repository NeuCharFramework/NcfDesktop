/*----------------------------------------------------------------
    Copyright (C) 2026 Senparc

    文件名：MainWindowViewModel.NeuBell.cs
    文件功能描述：将 Admin 纽铃提醒低开销同步到桌面宠物徽标

    创建标识：Senparc - 20260807
----------------------------------------------------------------*/

using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using NcfDesktopApp.GUI.Models;
using NcfDesktopApp.GUI.Services;

namespace NcfDesktopApp.GUI.ViewModels;

public partial class MainWindowViewModel
{
    private static readonly TimeSpan NeuBellFallbackRefreshInterval = TimeSpan.FromSeconds(30);
    private CancellationTokenSource? _neuBellSyncCancellation;
    private Task? _neuBellSyncTask;
    private Uri? _neuBellTargetUri;

    private void StartNeuBellSynchronization()
    {
        StopNeuBellSynchronization(resetBadge: false);
        var parentToken = _cancellationTokenSource?.Token ?? CancellationToken.None;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(parentToken);
        _neuBellSyncCancellation = cancellation;
        _neuBellSyncTask = SynchronizeNeuBellLoopAsync(cancellation.Token);
    }

    private void StopNeuBellSynchronization(bool resetBadge = true)
    {
        var cancellation = Interlocked.Exchange(ref _neuBellSyncCancellation, null);
        _neuBellSyncTask = null;
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

        if (!resetBadge)
        {
            return;
        }

        _neuBellTargetUri = null;
        Robot.ClearNeuBellNotifications();
    }

    public void OpenNeuBellInDefaultBrowser()
    {
        var targetUri = _neuBellTargetUri;
        if (targetUri == null || Robot.NeuBellCount <= 0)
        {
            return;
        }

        OpenBrowser(targetUri.AbsoluteUri);
    }

    private async Task SynchronizeNeuBellLoopAsync(CancellationToken cancellationToken)
    {
        var consecutiveFailures = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var state = await _adminChatClient
                    .GetNeuBellStateAsync(SiteUrl, cancellationToken)
                    .ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                ApplyNeuBellState(state);
                consecutiveFailures = 0;
                await WaitForNeuBellChangeOrFallbackAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (AdminChatApiException ex) when (ex.IsAuthenticationFailure)
            {
                Robot.ClearNeuBellNotifications();
                _neuBellTargetUri = null;
                Dispatcher.UIThread.Post(() =>
                {
                    if (!IsAdminAuthenticated)
                    {
                        return;
                    }

                    AdminChatStatusText = "纽铃服务未接受当前令牌，已暂停纽铃同步；AdminChat 登录保持可用。";
                    AddLog($"ℹ️ 纽铃同步未获授权，未影响 AdminChat：{ex.Message}");
                });
                return;
            }
            catch (AdminChatApiException)
            {
                consecutiveFailures++;
            }
            catch
            {
                consecutiveFailures++;
            }

            var retryDelay = TimeSpan.FromSeconds(Math.Min(30, 5 + consecutiveFailures * 5));
            try
            {
                await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void ApplyNeuBellState(NeuBellState state)
    {
        var presentation = NeuBellProjection.Create(state, SiteUrl);
        _neuBellTargetUri = presentation.TargetUri;
        Robot.ApplyNeuBellPresentation(
            presentation.Count,
            presentation.BadgeText,
            presentation.Summary);
    }

    private async Task WaitForNeuBellChangeOrFallbackAsync(CancellationToken cancellationToken)
    {
        using var refreshCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var eventTask = _adminChatClient.WaitForNeuBellChangeAsync(SiteUrl, refreshCancellation.Token);
        var fallbackTask = Task.Delay(NeuBellFallbackRefreshInterval, refreshCancellation.Token);
        try
        {
            var completed = await Task.WhenAny(eventTask, fallbackTask).ConfigureAwait(false);
            if (ReferenceEquals(completed, eventTask))
            {
                var providerAvailable = await eventTask.ConfigureAwait(false);
                if (!providerAvailable)
                {
                    await fallbackTask.ConfigureAwait(false);
                }
            }
            else
            {
                await fallbackTask.ConfigureAwait(false);
            }
        }
        finally
        {
            refreshCancellation.Cancel();
            try
            {
                await eventTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (refreshCancellation.IsCancellationRequested)
            {
            }
        }
    }
}
