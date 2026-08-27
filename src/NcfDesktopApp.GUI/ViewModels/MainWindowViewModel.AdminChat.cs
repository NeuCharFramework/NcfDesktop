/*----------------------------------------------------------------
    Copyright (C) 2026 Senparc

    文件名：MainWindowViewModel.AdminChat.cs
    文件功能描述：DesktopBridge + Admin JWT 保护的快捷聊天状态与命令

    创建标识：Senparc - 20260726

    修改标识：Senparc - 20260804
    修改描述：v0.6.0 接入 AdminChat SSE 回复与本地流式朗读

    修改标识：Senparc - 20260812
    修改描述：v0.10.0 完善桌面端唤醒词会话激活与中英文提示

    修改标识：Senparc - 20260826
    修改描述：v0.11.0 唤醒词命中后切换并发送到指定会话

----------------------------------------------------------------*/

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NcfDesktopApp.GUI.Models;
using NcfDesktopApp.GUI.Services;

namespace NcfDesktopApp.GUI.ViewModels;

public partial class MainWindowViewModel
{
    [ObservableProperty]
    private string _adminUserName = string.Empty;

    [ObservableProperty]
    private string _adminPassword = string.Empty;

    [ObservableProperty]
    private bool _isAdminAuthenticated;

    [ObservableProperty]
    private bool _isAdminChatBusy;

    [ObservableProperty]
    private bool _isDesktopBridgeAvailableForChat;

    [ObservableProperty]
    private string _adminChatStatusText = "启动 NCF 并连接 DesktopBridge 后可登录。";

    [ObservableProperty]
    private string _chatInput = string.Empty;

    [ObservableProperty]
    private AdminChatSessionSummary? _selectedAdminChatSession;

    [ObservableProperty]
    private AdminChatAiModelOption? _selectedAdminChatAiModel;

    private DesktopBridgeCapabilities? _desktopBridgeCapabilities;
    private readonly SemaphoreSlim _adminChatRefreshLock = new(1, 1);
    private int _adminChatSessionRefreshInProgress;
    private int _optimisticMessageId;
    private int _activeStreamingAssistantId;
    private readonly object _streamingChunkLock = new();
    private readonly StringBuilder _pendingStreamingChunks = new();
    private int _pendingStreamingSessionId;
    private int _pendingStreamingGeneration;
    private int _streamingGeneration;
    private int _streamingChunkFlushScheduled;
    private long _adminChatSessionSelectionVersion;
    private int _suppressAdminChatSessionSelectionLoad;
    private readonly Dictionary<int, int> _adminChatSessionModelIds = new();
    private int _adminChatSessionMutationInProgress;
    private int _adminWebHandoffInProgress;
    private CancellationTokenSource? _adminWebHandoffCancellation;
    private bool _suppressAdminWebHandoffUntilWebLogin;
    private DateTimeOffset _nextAdminWebHandoffAttemptUtc;

    public ObservableCollection<AdminChatSessionSummary> AdminChatSessions { get; } = new();

    public ObservableCollection<AdminChatMessage> AdminChatMessages { get; } = new();

    public ObservableCollection<AdminChatAiModelOption> AdminChatAiModelOptions { get; } = new();

    public ObservableCollection<AdminChatModuleOption> AdminChatModuleOptions { get; } = new();

    public bool HasAdminChatModuleOptions => AdminChatModuleOptions.Count > 0;

    public bool HasSelectedAdminChatSession => SelectedAdminChatSession != null;

    public string AdminChatSelectedModelDescription =>
        SelectedAdminChatAiModel?.Description ?? "使用系统默认 SenparcAiSetting。";

    public string AdminChatModuleSelectionText
    {
        get
        {
            var associatedCount = AdminChatModuleOptions.Count(option => option.IsAssociated);
            var changedCount = AdminChatModuleOptions.Count(option => option.IsSelected != option.IsAssociated);
            return SelectedAdminChatSession == null
                ? $"新会话 XNCF 模块（{AdminChatModuleOptions.Count(option => option.IsSelected)}）"
                : $"XNCF 模块（已关联 {associatedCount}，待应用 {changedCount}）";
        }
    }

    public bool IsAdminLoginVisible => IsDesktopBridgeAvailableForChat && !IsAdminAuthenticated;

    public bool IsAdminChatActive => IsDesktopBridgeAvailableForChat && IsAdminAuthenticated;

    public bool IsAdminChatUnavailable => !IsDesktopBridgeAvailableForChat;

    public NcfMascotPose AdminChatMascotPose => IsAdminChatBusy
        ? NcfMascotPose.Thinking
        : !IsDesktopBridgeAvailableForChat
            ? NcfMascotPose.Warning
            : IsAdminAuthenticated
                ? NcfMascotPose.Idle
                : NcfMascotPose.Wave;

    public string AdminChatAccountText => _adminChatClient.Authentication?.UserName ?? string.Empty;

    public string AdminChatDisabledReason
    {
        get
        {
            if (!_isNcfRunning || string.IsNullOrWhiteSpace(SiteUrl) || SiteUrl == "未启动")
            {
                return "请先启动 NCF 站点。";
            }

            if (_desktopBridgeCapabilities is { SupportsAuthorizedSync: false })
            {
                return "当前 DesktopBridge 版本没有授权同步能力，请更新模块后重启站点。";
            }

            return "快捷聊天只在 DesktopBridge 实时连接时启用；NCF 本身仍可正常使用。";
        }
    }

    partial void OnAdminUserNameChanged(string value)
    {
        AdminLoginCommand.NotifyCanExecuteChanged();
    }

    partial void OnAdminPasswordChanged(string value)
    {
        AdminLoginCommand.NotifyCanExecuteChanged();
    }

    partial void OnChatInputChanged(string value)
    {
        SendAdminChatMessageCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsAdminAuthenticatedChanged(bool value)
    {
        NotifyAdminChatStateChanged();
    }

    partial void OnIsAdminChatBusyChanged(bool value)
    {
        NotifyAdminChatStateChanged();
        if (!value)
        {
            SchedulePendingWakeWordTranscriptSend();
        }
    }

    partial void OnIsDesktopBridgeAvailableForChatChanged(bool value)
    {
        NotifyAdminChatStateChanged();
    }

    partial void OnSelectedAdminChatSessionChanged(AdminChatSessionSummary? value)
    {
        var selectionVersion = Interlocked.Increment(ref _adminChatSessionSelectionVersion);
        OnPropertyChanged(nameof(HasSelectedAdminChatSession));
        OnPropertyChanged(nameof(AdminChatModuleSelectionText));
        DeleteAdminChatSessionCommand.NotifyCanExecuteChanged();
        ApplyAdminChatModulesCommand.NotifyCanExecuteChanged();

        var modelId = value != null && _adminChatSessionModelIds.TryGetValue(value.Id, out var savedModelId)
            ? savedModelId
            : 0;
        SelectedAdminChatAiModel = AdminChatAiModelOptions.FirstOrDefault(model => model.Id == modelId) ??
                                   AdminChatAiModelOptions.FirstOrDefault();

        // 删除/刷新期间由显式流程负责读取会话详情。这里不再启动一个无法取消的
        // 后台读取，避免删除后的旧 session detail 响应覆盖新状态。
        if (value != null &&
            IsAdminChatActive &&
            Volatile.Read(ref _adminChatSessionMutationInProgress) == 0 &&
            Volatile.Read(ref _suppressAdminChatSessionSelectionLoad) == 0)
        {
            _ = LoadAdminChatSessionAsync(value.Id, selectionVersion);
        }
        else
        {
            AdminChatMessages.Clear();
            SetAssociatedAdminChatModules(Array.Empty<AdminChatSessionModule>());
        }
    }

    partial void OnSelectedAdminChatAiModelChanged(AdminChatAiModelOption? value)
    {
        if (value != null && SelectedAdminChatSession != null)
        {
            _adminChatSessionModelIds[SelectedAdminChatSession.Id] = value.Id;
        }

        OnPropertyChanged(nameof(AdminChatSelectedModelDescription));
    }

    [RelayCommand(CanExecute = nameof(CanAdminLogin))]
    private async Task AdminLogin()
    {
        if (_desktopBridgeSessionToken == null || _desktopBridgeCapabilities?.AuthorizedSyncEndpoint == null)
        {
            AdminChatStatusText = "DesktopBridge 授权同步接口尚未就绪。";
            return;
        }

        IsAdminChatBusy = true;
        AdminChatStatusText = "正在验证 AdminOnly 身份…";
        try
        {
            var authentication = await _adminChatClient.AuthenticateAsync(
                SiteUrl,
                AdminUserName,
                AdminPassword,
                _cancellationTokenSource?.Token ?? CancellationToken.None);

            // 密码只用于本次请求，成功后立即从 ViewModel 清除。
            AdminPassword = string.Empty;
            await CompleteAdminChatAuthenticationAsync(authentication, "显式登录");
        }
        catch (AdminChatApiException ex)
        {
            await _desktopBridgeClient.StopAuthorizedSyncAsync();
            _adminChatClient.ClearAuthentication();
            IsAdminAuthenticated = false;
            AdminChatStatusText = ex.Message;
            AddLog($"🔒 Admin Chat 登录失败: {ex.Message}");
        }
        catch (OperationCanceledException)
        {
            await _desktopBridgeClient.StopAuthorizedSyncAsync();
            _adminChatClient.ClearAuthentication();
            IsAdminAuthenticated = false;
            AdminChatStatusText = "登录已取消。";
        }
        catch (Exception ex)
        {
            await _desktopBridgeClient.StopAuthorizedSyncAsync();
            _adminChatClient.ClearAuthentication();
            IsAdminAuthenticated = false;
            AdminChatStatusText = $"登录失败：{ex.Message}";
        }
        finally
        {
            // 无论成功或失败都不在可绑定的界面状态中保留密码。
            AdminPassword = string.Empty;
            IsAdminChatBusy = false;
        }
    }

    private bool CanAdminLogin()
    {
        return IsDesktopBridgeAvailableForChat &&
               !IsAdminAuthenticated &&
               !IsAdminChatBusy &&
               !string.IsNullOrWhiteSpace(AdminUserName) &&
               !string.IsNullOrEmpty(AdminPassword);
    }

    [RelayCommand]
    private async Task AdminLogout()
    {
        _suppressAdminWebHandoffUntilWebLogin = true;
        CancelAdminWebHandoff();
        StopAgentPortalSynchronization();
        StopNeuBellSynchronization();
        await _desktopBridgeClient.StopAuthorizedSyncAsync();
        _adminChatClient.ClearAuthentication();
        IsAdminAuthenticated = false;
        AdminPassword = string.Empty;
        AdminChatSessions.Clear();
        AdminChatMessages.Clear();
        RefreshWakeWordChatSessionOptions();
        ResetAdminChatOptions();
        SelectedAdminChatSession = null;
        AdminChatStatusText = "已退出；JWT 已从内存中清除。";
        OnPropertyChanged(nameof(AdminChatAccountText));
    }

    /// <summary>
    /// 浏览器完成导航后仅根据同源 Admin 页面判断 Cookie 登录是否已成功。
    /// Cookie 本身始终留在 WebView，GUI 只发起 DesktopBridge + PKCE 一次性换票。
    /// </summary>
    public void HandleAdminWebViewNavigation(string url)
    {
        if (!TryGetSameOriginAdminNavigation(url, out var navigationUri, out var isLoginPage))
        {
            return;
        }

        if (isLoginPage)
        {
            if (IsAdminAuthenticated)
            {
                AdminChatStatusText =
                    "WebView 管理员 Cookie 当前未通过校验；桌面端 AdminChat 登录仍保留，" +
                    "请检查 NCF DataProtection-Keys 是否持久化。";
                AddLog(
                    "⚠️ WebView 已回到管理员登录页，但未清除桌面端 AdminChat JWT。" +
                    "若同时出现 AdminChat 退出，请检查 NCF 的 DataProtection key ring。");
            }

            _suppressAdminWebHandoffUntilWebLogin = false;
            CancelAdminWebHandoff();
            return;
        }

        if (navigationUri.AbsolutePath.StartsWith(
                "/Admin/DesktopBridge/AuthHandoff",
                StringComparison.OrdinalIgnoreCase) ||
            IsAdminAuthenticated || _suppressAdminWebHandoffUntilWebLogin ||
            DateTimeOffset.UtcNow < _nextAdminWebHandoffAttemptUtc ||
            !CanUseAdminWebHandoff())
        {
            return;
        }

        _ = AuthenticateAdminChatFromWebViewAsync(navigationUri);
    }

    private async Task AuthenticateAdminChatFromWebViewAsync(Uri navigationUri)
    {
        if (Interlocked.CompareExchange(ref _adminWebHandoffInProgress, 1, 0) != 0)
        {
            return;
        }

        var siteCancellation = _cancellationTokenSource?.Token ?? CancellationToken.None;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(siteCancellation);
        cancellation.CancelAfter(TimeSpan.FromSeconds(75));
        _adminWebHandoffCancellation = cancellation;
        IsAdminChatBusy = true;
        AdminChatStatusText = "检测到 WebView 管理员登录，正在安全授权 AdminChat…";
        try
        {
            var capabilities = _desktopBridgeCapabilities!;
            var desktopSessionToken = _desktopBridgeSessionToken!;
            var returnPath = string.IsNullOrWhiteSpace(navigationUri.PathAndQuery)
                ? "/Admin/Index"
                : navigationUri.PathAndQuery;
            var handoff = await _desktopBridgeClient.CreateAdminAuthHandoffAsync(
                SiteUrl,
                desktopSessionToken,
                returnPath,
                capabilities.AdminAuthHandoffRequestEndpoint,
                cancellation.Token);

            if (!SiteEndpointPolicy.TryCreateEndpoint(
                    SiteUrl,
                    handoff.ApprovalPath,
                    out var approvalUri,
                    out var approvalError))
            {
                throw new InvalidOperationException(approvalError);
            }

            if (BrowserViewReference is not NcfDesktopApp.GUI.Views.BrowserView browserView)
            {
                throw new InvalidOperationException("内置浏览器尚未就绪。");
            }

            await browserView.NavigateToUrl(approvalUri.ToString());
            AdminChatStatusText = "请在 WebView 确认“授权此 GUI”；无需再次输入管理员密码。";
            while (DateTimeOffset.UtcNow < handoff.ExpiresAt)
            {
                await Task.Delay(handoff.PollIntervalMilliseconds, cancellation.Token);
                var result = await _desktopBridgeClient.RedeemAdminAuthHandoffAsync(
                    SiteUrl,
                    desktopSessionToken,
                    handoff,
                    capabilities.AdminAuthHandoffRedeemEndpoint,
                    cancellation.Token);
                if (string.Equals(result.Status, "pending", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!string.Equals(result.Status, "approved", StringComparison.OrdinalIgnoreCase))
                {
                    _nextAdminWebHandoffAttemptUtc = DateTimeOffset.UtcNow.AddSeconds(30);
                    AdminChatStatusText = result.Message ?? "WebView 自动授权未完成，请使用显式登录。";
                    return;
                }

                var authentication = await _adminChatClient.AuthenticateWithAccessTokenAsync(
                    SiteUrl,
                    result.UserName!,
                    result.AccessToken!,
                    result.ExpiresUtc,
                    cancellation.Token);
                await CompleteAdminChatAuthenticationAsync(authentication, "WebView 自动授权");
                return;
            }

            _nextAdminWebHandoffAttemptUtc = DateTimeOffset.UtcNow.AddSeconds(30);
            AdminChatStatusText = "WebView 自动授权已过期，请重新进入后台页面或使用显式登录。";
        }
        catch (OperationCanceledException)
        {
            // 站点停止、显式注销或重新进入登录页时静默取消，不覆盖新的界面状态。
        }
        catch (AdminChatApiException ex)
        {
            await _desktopBridgeClient.StopAuthorizedSyncAsync();
            _adminChatClient.ClearAuthentication();
            IsAdminAuthenticated = false;
            _nextAdminWebHandoffAttemptUtc = DateTimeOffset.UtcNow.AddSeconds(30);
            AdminChatStatusText = ex.Message;
            AddLog($"🔒 WebView 自动授权失败: {ex.Message}");
        }
        catch (Exception ex)
        {
            await _desktopBridgeClient.StopAuthorizedSyncAsync();
            _adminChatClient.ClearAuthentication();
            IsAdminAuthenticated = false;
            _nextAdminWebHandoffAttemptUtc = DateTimeOffset.UtcNow.AddSeconds(30);
            AdminChatStatusText = $"WebView 自动授权不可用，请使用显式登录：{ex.Message}";
            AddLog($"⚠️ WebView 自动授权已安全降级: {ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_adminWebHandoffCancellation, cancellation))
            {
                _adminWebHandoffCancellation = null;
            }

            Interlocked.Exchange(ref _adminWebHandoffInProgress, 0);
            IsAdminChatBusy = false;
        }
    }

    private async Task CompleteAdminChatAuthenticationAsync(
        AdminChatAuthentication authentication,
        string authenticationSource)
    {
        if (_desktopBridgeSessionToken == null ||
            string.IsNullOrWhiteSpace(_desktopBridgeCapabilities?.AuthorizedSyncEndpoint))
        {
            throw new InvalidOperationException("DesktopBridge 授权同步接口尚未就绪。");
        }

        AdminUserName = authentication.UserName;
        AdminPassword = string.Empty;
        IsAdminAuthenticated = true;
        AdminChatStatusText = $"{authentication.UserName} 已通过 AdminOnly 验证";
        OnPropertyChanged(nameof(AdminChatAccountText));

        await _desktopBridgeClient.StartAuthorizedSyncAsync(
            SiteUrl,
            _desktopBridgeSessionToken,
            authentication.AccessToken,
            _desktopBridgeCapabilities.AuthorizedSyncEndpoint,
            _cancellationTokenSource?.Token ?? CancellationToken.None);
        await LoadAdminChatOptionsAsync();
        await RefreshAdminChatSessionsAsync(loadSelectedMessages: true);
        StartAgentPortalSynchronization();
        StartNeuBellSynchronization();
        AddLog($"🔐 管理员 {authentication.UserName} 已通过{authenticationSource}连接快捷聊天（JWT 仅保存在内存中）");
    }

    private bool CanUseAdminWebHandoff()
    {
        return IsDesktopBridgeAvailableForChat &&
               !string.IsNullOrWhiteSpace(_desktopBridgeSessionToken) &&
               _desktopBridgeCapabilities is
               {
                   SupportsAdminAuthHandoff: true,
                   AdminAuthHandoffRequestEndpoint.Length: > 0,
                   AdminAuthHandoffRedeemEndpoint.Length: > 0
               };
    }

    private bool TryGetSameOriginAdminNavigation(
        string url,
        out Uri navigationUri,
        out bool isLoginPage)
    {
        navigationUri = null!;
        isLoginPage = false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var candidate) || candidate == null ||
            !SiteEndpointPolicy.TryNormalizeSiteUrl(SiteUrl, out var siteUri, out _) ||
            !string.Equals(candidate.Scheme, siteUri.Scheme, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(candidate.Host, siteUri.Host, StringComparison.OrdinalIgnoreCase) ||
            candidate.Port != siteUri.Port)
        {
            return false;
        }

        var path = candidate.AbsolutePath.TrimEnd('/');
        if (!path.Equals("/Admin", StringComparison.OrdinalIgnoreCase) &&
            !path.StartsWith("/Admin/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        navigationUri = candidate;
        isLoginPage = path.Equals("/Admin/Login", StringComparison.OrdinalIgnoreCase);
        return true;
    }

    private void CancelAdminWebHandoff()
    {
        try
        {
            _adminWebHandoffCancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    [RelayCommand(CanExecute = nameof(CanUseAdminChat))]
    private async Task NewAdminChatSession()
    {
        IsAdminChatBusy = true;
        Interlocked.Increment(ref _adminChatSessionMutationInProgress);
        try
        {
            var sessionId = await _adminChatClient.CreateSessionAsync(
                SiteUrl,
                SelectedAdminChatAiModel?.Id ?? 0,
                GetSelectedAdminChatModuleUids(),
                _cancellationTokenSource?.Token ?? CancellationToken.None);
            _adminChatSessionModelIds[sessionId] = SelectedAdminChatAiModel?.Id ?? 0;
            await RefreshAdminChatSessionsAsync(loadSelectedMessages: false, preferredSessionId: sessionId);
            AdminChatStatusText = "新会话已创建。";
        }
        catch (AdminChatApiException ex)
        {
            HandleAdminChatApiFailure(ex);
        }
        finally
        {
            Interlocked.Decrement(ref _adminChatSessionMutationInProgress);
            IsAdminChatBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanDeleteAdminChatSession))]
    private async Task DeleteAdminChatSession()
    {
        var session = SelectedAdminChatSession;
        if (session == null)
        {
            return;
        }

        var confirmed = await ShowConfirmDialogAsync(
            LocalizationService.T("Chat.DeleteSessionTitle"),
            LocalizationService.T("Chat.DeleteSessionConfirm", session.DisplayName),
            LocalizationService.T("Chat.DeleteSessionAction"),
            LocalizationService.T("Action.Cancel"));
        if (!confirmed)
        {
            return;
        }

        IsAdminChatBusy = true;
        Interlocked.Increment(ref _adminChatSessionMutationInProgress);
        try
        {
            await _adminChatClient.DeleteSessionAsync(
                SiteUrl,
                session.Id,
                _cancellationTokenSource?.Token ?? CancellationToken.None);
            _adminChatSessionModelIds.Remove(session.Id);
            SelectedAdminChatSession = null;
            await RefreshAdminChatSessionsAsync(loadSelectedMessages: true);
            AdminChatStatusText = LocalizationService.T("Chat.SessionDeleted");
        }
        catch (AdminChatApiException ex)
        {
            HandleAdminChatApiFailure(ex);
        }
        finally
        {
            Interlocked.Decrement(ref _adminChatSessionMutationInProgress);
            IsAdminChatBusy = false;
        }
    }

    private bool CanDeleteAdminChatSession()
    {
        return CanUseAdminChat() && SelectedAdminChatSession != null;
    }

    [RelayCommand(CanExecute = nameof(CanApplyAdminChatModules))]
    private async Task ApplyAdminChatModules()
    {
        var session = SelectedAdminChatSession;
        var modules = AdminChatModuleOptions
            .Where(option => option.IsSelected)
            .Select(option => option.Module)
            .ToArray();
        if (session == null)
        {
            return;
        }

        IsAdminChatBusy = true;
        Interlocked.Increment(ref _adminChatSessionMutationInProgress);
        try
        {
            await _adminChatClient.SetModulesForSessionAsync(
                SiteUrl,
                session.Id,
                modules,
                _cancellationTokenSource?.Token ?? CancellationToken.None);
            await LoadAdminChatSessionCoreAsync(session.Id);
            AdminChatStatusText = $"已更新 XNCF 模块关联，共选择 {modules.Length} 个模块。";
        }
        catch (AdminChatApiException ex)
        {
            HandleAdminChatApiFailure(ex);
        }
        finally
        {
            Interlocked.Decrement(ref _adminChatSessionMutationInProgress);
            IsAdminChatBusy = false;
        }
    }

    private bool CanApplyAdminChatModules()
    {
        return CanUseAdminChat() &&
               SelectedAdminChatSession != null &&
               AdminChatModuleOptions.Any(option => option.IsSelected != option.IsAssociated);
    }

    [RelayCommand(CanExecute = nameof(CanSendAdminChatMessage))]
    private Task SendAdminChatMessage() => SendAdminChatMessageCoreAsync();

    /// <summary>
    /// 手动发送和 STT 自动发送共用的唯一入口，保证鉴权、流式响应和失败恢复行为一致。
    /// </summary>
    /// <returns>消息是否完成发送；由输入框发起时失败会恢复原文字，显式内容则交由调用方保留。</returns>
    private async Task<bool> SendAdminChatMessageCoreAsync(
        string? contentOverride = null,
        int? targetSessionId = null)
    {
        var useChatInput = contentOverride == null;
        var content = (contentOverride ?? ChatInput).Trim();
        if (content.Length == 0)
        {
            return false;
        }

        IsAdminChatBusy = true;
        AdminChatStatusText = "Agent 正在处理…";
        try
        {
            var sessionId = targetSessionId ?? SelectedAdminChatSession?.Id ?? 0;
            var aiModelId = targetSessionId is > 0 &&
                            _adminChatSessionModelIds.TryGetValue(targetSessionId.Value, out var targetModelId)
                ? targetModelId
                : SelectedAdminChatAiModel?.Id ?? 0;
            if (sessionId <= 0)
            {
                sessionId = await _adminChatClient.CreateSessionAsync(
                    SiteUrl,
                    aiModelId,
                    GetSelectedAdminChatModuleUids(),
                    _cancellationTokenSource?.Token ?? CancellationToken.None);
                _adminChatSessionModelIds[sessionId] = aiModelId;
            }

            if (useChatInput)
            {
                ChatInput = string.Empty;
            }
            ClearPendingStreamingChunks();
            var optimisticUserId = await Dispatcher.UIThread.InvokeAsync(() =>
                AddOptimisticUserMessage(sessionId, content));
            _activeStreamingAssistantId = 0;
            BeginStreamingAutoRead(sessionId);

            await _adminChatClient.SendMessageStreamingAsync(
                SiteUrl,
                sessionId,
                content,
                aiModelId: aiModelId,
                onUserMessage: message => ReconcileUserMessage(optimisticUserId, message),
                onToken: chunk => HandleStreamingAssistantChunk(sessionId, chunk),
                onAssistantMessage: message => CompleteStreamingAssistantMessage(message),
                cancellationToken: _cancellationTokenSource?.Token ?? CancellationToken.None);

            await RefreshAdminChatSessionsAsync(loadSelectedMessages: true, preferredSessionId: sessionId);
            AdminChatStatusText = "消息已通过 Admin Chat API 完成，并由 EventBus 通知同步。";
            return true;
        }
        catch (AdminChatApiException ex)
        {
            RemovePendingStreamingMessages();
            if (useChatInput)
            {
                ChatInput = content;
            }
            HandleAdminChatApiFailure(ex);
            return false;
        }
        catch (OperationCanceledException)
        {
            RemovePendingStreamingMessages();
            if (useChatInput)
            {
                ChatInput = content;
            }
            AdminChatStatusText = "发送已取消。";
            return false;
        }
        catch (Exception ex)
        {
            RemovePendingStreamingMessages();
            if (useChatInput)
            {
                ChatInput = content;
            }
            AdminChatStatusText = $"发送失败：{ex.Message}";
            AddLog($"❌ Admin Chat 发送失败: {ex.Message}");
            return false;
        }
        finally
        {
            IsAdminChatBusy = false;
        }
    }

    private bool CanSendAdminChatMessage()
    {
        return CanUseAdminChat() && !string.IsNullOrWhiteSpace(ChatInput);
    }

    private bool CanUseAdminChat()
    {
        return IsAdminChatActive && !IsAdminChatBusy;
    }

    internal async Task DeleteAdminChatMessageAsync(AdminChatMessage message)
    {
        var session = SelectedAdminChatSession;
        if (!CanUseAdminChat() || session == null || !message.CanDelete || message.SessionId != session.Id)
        {
            return;
        }

        var preview = message.Content.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (preview.Length > 80)
        {
            preview = preview[..80] + "…";
        }

        var confirmed = await ShowConfirmDialogAsync(
            LocalizationService.T("Chat.DeleteMessageTitle"),
            LocalizationService.T(
                "Chat.DeleteMessageConfirm",
                message.IsUser ? LocalizationService.T("Chat.RoleUser") : LocalizationService.T("Chat.RoleAgent"),
                preview),
            LocalizationService.T("Chat.DeleteMessageAction"),
            LocalizationService.T("Action.Cancel"));
        if (!confirmed)
        {
            return;
        }

        IsAdminChatBusy = true;
        try
        {
            await _adminChatClient.DeleteMessagesAsync(
                SiteUrl,
                session.Id,
                new[] { message.Id },
                _cancellationTokenSource?.Token ?? CancellationToken.None);
            await LoadAdminChatSessionCoreAsync(session.Id);
            AdminChatStatusText = "聊天消息已删除。";
        }
        catch (AdminChatApiException ex)
        {
            HandleAdminChatApiFailure(ex);
        }
        finally
        {
            IsAdminChatBusy = false;
        }
    }

    private async Task LoadAdminChatOptionsAsync()
    {
        ResetAdminChatOptions();
        var cancellationToken = _cancellationTokenSource?.Token ?? CancellationToken.None;

        try
        {
            var models = await _adminChatClient.GetAiModelOptionsAsync(SiteUrl, cancellationToken);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                AdminChatAiModelOptions.Clear();
                foreach (var model in models)
                {
                    AdminChatAiModelOptions.Add(model);
                }

                EnsureDefaultAdminChatModel();
                SelectedAdminChatAiModel = AdminChatAiModelOptions.FirstOrDefault(model => model.IsDefault) ??
                                           AdminChatAiModelOptions.FirstOrDefault();
            });
        }
        catch (AdminChatApiException ex) when (!ex.IsAuthenticationFailure)
        {
            AddLog($"⚠️ Admin Chat 模型列表不可用，将使用系统默认模型：{ex.Message}");
        }

        try
        {
            var modules = await _adminChatClient.GetAvailableModulesAsync(SiteUrl, cancellationToken);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                ClearAdminChatModuleOptions();
                foreach (var module in modules)
                {
                    AddAdminChatModuleOption(new AdminChatModuleOption(module));
                }

                OnPropertyChanged(nameof(HasAdminChatModuleOptions));
                OnPropertyChanged(nameof(AdminChatModuleSelectionText));
            });
        }
        catch (AdminChatApiException ex) when (!ex.IsAuthenticationFailure)
        {
            AddLog($"⚠️ Admin Chat XNCF 模块列表不可用：{ex.Message}");
        }
    }

    private void ResetAdminChatOptions()
    {
        _adminChatSessionModelIds.Clear();
        AdminChatAiModelOptions.Clear();
        EnsureDefaultAdminChatModel();
        SelectedAdminChatAiModel = AdminChatAiModelOptions[0];
        ClearAdminChatModuleOptions();
        OnPropertyChanged(nameof(HasAdminChatModuleOptions));
        OnPropertyChanged(nameof(AdminChatModuleSelectionText));
    }

    private void EnsureDefaultAdminChatModel()
    {
        if (AdminChatAiModelOptions.All(model => model.Id != 0))
        {
            AdminChatAiModelOptions.Insert(0, new AdminChatAiModelOption(
                0,
                "系统默认模型",
                "使用站点 SenparcAiSetting 中配置的聊天模型。",
                true));
        }
    }

    private void ClearAdminChatModuleOptions()
    {
        foreach (var option in AdminChatModuleOptions)
        {
            option.PropertyChanged -= AdminChatModuleOptionOnPropertyChanged;
        }

        AdminChatModuleOptions.Clear();
    }

    private void AddAdminChatModuleOption(AdminChatModuleOption option)
    {
        option.PropertyChanged += AdminChatModuleOptionOnPropertyChanged;
        AdminChatModuleOptions.Add(option);
    }

    private void AdminChatModuleOptionOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AdminChatModuleOption.IsSelected))
        {
            return;
        }

        OnPropertyChanged(nameof(AdminChatModuleSelectionText));
        ApplyAdminChatModulesCommand.NotifyCanExecuteChanged();
    }

    private string[] GetSelectedAdminChatModuleUids()
    {
        return AdminChatModuleOptions
            .Where(option => option.IsSelected)
            .Select(option => option.Uid)
            .Where(uid => !string.IsNullOrWhiteSpace(uid))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private void SetAssociatedAdminChatModules(IReadOnlyCollection<AdminChatSessionModule> modules)
    {
        var associatedByUid = modules
            .Where(module => !string.IsNullOrWhiteSpace(module.XncfModuleUid))
            .GroupBy(module => module.XncfModuleUid, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var option in AdminChatModuleOptions)
        {
            option.IsAssociated = associatedByUid.ContainsKey(option.Uid);
            option.IsSelected = option.IsAssociated;
        }

        foreach (var module in associatedByUid.Values)
        {
            if (AdminChatModuleOptions.Any(option =>
                    string.Equals(option.Uid, module.XncfModuleUid, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var option = new AdminChatModuleOption(new AdminChatAvailableModule(
                module.XncfModuleUid,
                module.ModuleName,
                string.IsNullOrWhiteSpace(module.DisplayName) ? module.ModuleName : module.DisplayName,
                module.ModuleVersion,
                module.ModuleDescription ?? string.Empty,
                string.Empty,
                false))
            {
                IsAssociated = true,
                IsSelected = true
            };
            AddAdminChatModuleOption(option);
        }

        OnPropertyChanged(nameof(HasAdminChatModuleOptions));
        OnPropertyChanged(nameof(AdminChatModuleSelectionText));
        ApplyAdminChatModulesCommand.NotifyCanExecuteChanged();
    }

    private async Task RefreshAdminChatSessionsAsync(
        bool loadSelectedMessages,
        int? preferredSessionId = null)
    {
        if (!IsAdminChatActive)
        {
            return;
        }

        Interlocked.Increment(ref _adminChatSessionRefreshInProgress);
        try
        {
            await _adminChatRefreshLock.WaitAsync();
            try
            {
                var selectedId = preferredSessionId ?? SelectedAdminChatSession?.Id;
                var sessions = await _adminChatClient.GetSessionsAsync(
                    SiteUrl,
                    _cancellationTokenSource?.Token ?? CancellationToken.None);

                AdminChatSessionSummary? selected = null;
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    AdminChatSessions.Clear();
                    foreach (var session in sessions)
                    {
                        AdminChatSessions.Add(session);
                    }

                    RefreshWakeWordChatSessionOptions();
                    selected = AdminChatSessions.FirstOrDefault(item => item.Id == selectedId) ??
                               AdminChatSessions.FirstOrDefault();
                    Interlocked.Increment(ref _suppressAdminChatSessionSelectionLoad);
                    try
                    {
                        SelectedAdminChatSession = selected;
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _suppressAdminChatSessionSelectionLoad);
                    }
                });

                if (loadSelectedMessages && selected != null)
                {
                    await LoadAdminChatSessionCoreAsync(selected.Id);
                }
                else if (selected == null)
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        AdminChatMessages.Clear();
                        SetAssociatedAdminChatModules(Array.Empty<AdminChatSessionModule>());
                    });
                }
            }
            finally
            {
                _adminChatRefreshLock.Release();
            }
        }
        finally
        {
            Interlocked.Decrement(ref _adminChatSessionRefreshInProgress);
        }
    }

    private async Task LoadAdminChatSessionAsync(int sessionId, long selectionVersion)
    {
        await _adminChatRefreshLock.WaitAsync();
        try
        {
            AdminChatStatusText = "正在加载所选 Chat 的历史记录…";
            await LoadAdminChatSessionCoreAsync(sessionId, selectionVersion);
        }
        catch (AdminChatApiException ex)
        {
            HandleAdminChatApiFailure(ex);
        }
        catch (OperationCanceledException)
        {
            // 工作台关闭或站点停止时的正常取消。
        }
        catch (Exception ex)
        {
            AdminChatStatusText = $"会话刷新失败：{ex.Message}";
            AddLog($"⚠️ AdminChat 会话刷新失败: {ex.Message}");
            CrashDiagnosticService.ReportHandledException("AdminChat 会话刷新", ex);
        }
        finally
        {
            _adminChatRefreshLock.Release();
        }
    }

    private async Task LoadAdminChatSessionCoreAsync(
        int sessionId,
        long? expectedSelectionVersion = null)
    {
        if (!IsAdminChatActive)
        {
            return;
        }

        var session = await _adminChatClient.GetSessionDetailAsync(
            SiteUrl,
            sessionId,
            _cancellationTokenSource?.Token ?? CancellationToken.None);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (SelectedAdminChatSession?.Id != sessionId ||
                expectedSelectionVersion is { } version &&
                Interlocked.Read(ref _adminChatSessionSelectionVersion) != version)
            {
                return;
            }

            AdminChatMessages.Clear();
            var messages = session?.Messages ?? new List<AdminChatMessage>();
            foreach (var message in messages
                         .OrderBy(message => message.Sequence)
                         .ThenBy(message => message.Id))
            {
                AdminChatMessages.Add(message);
            }

            SetAssociatedAdminChatModules(session?.Modules ?? new List<AdminChatSessionModule>());
            AdminChatStatusText = session == null
                ? "所选 Chat 未返回历史记录。"
                : $"已加载 Chat「{SelectedAdminChatSession.DisplayName}」的历史记录。";
        });
    }

    private void UpdateAdminChatBridgeState(DesktopBridgeProbeResult result)
    {
        if (result.Capabilities != null)
        {
            _desktopBridgeCapabilities = result.Capabilities;
        }

        IsDesktopBridgeAvailableForChat = result.IsAvailable &&
                                          _desktopBridgeCapabilities?.SupportsAuthorizedSync == true &&
                                          !string.IsNullOrWhiteSpace(_desktopBridgeCapabilities.AuthorizedSyncEndpoint);

        if (!IsDesktopBridgeAvailableForChat)
        {
            AdminChatStatusText = AdminChatDisabledReason;
        }
        else if (!IsAdminAuthenticated)
        {
            AdminChatStatusText = _desktopBridgeCapabilities?.SupportsAdminAuthHandoff == true
                ? "DesktopBridge 已连接；可在内置 WebView 登录后台，或在此显式登录。"
                : "DesktopBridge 已连接，请使用后台管理员账号登录。";
        }

        OnPropertyChanged(nameof(AdminChatDisabledReason));
    }

    private void OnDesktopAuthorizedSyncReceived(DesktopAuthorizedSyncMessage message)
    {
        if (!string.Equals(message.Channel, "admin-chat", StringComparison.OrdinalIgnoreCase) ||
            !IsAdminChatActive ||
            Volatile.Read(ref _adminChatSessionMutationInProgress) != 0)
        {
            return;
        }

        Dispatcher.UIThread.Post(() => _ = HandleAuthorizedSyncReceivedAsync(message));
    }

    private async Task HandleAuthorizedSyncReceivedAsync(DesktopAuthorizedSyncMessage message)
    {
        try
        {
            if (!IsAdminChatActive ||
                Volatile.Read(ref _adminChatSessionMutationInProgress) != 0)
            {
                return;
            }

            var sessionId = int.TryParse(message.ResourceId, out var parsed) ? parsed : (int?)null;
            await RefreshAdminChatSessionsAsync(
                loadSelectedMessages: sessionId == SelectedAdminChatSession?.Id,
                preferredSessionId: SelectedAdminChatSession?.Id);
            AdminChatStatusText = "已收到 EventBus 同步通知。";
        }
        catch (AdminChatApiException ex)
        {
            HandleAdminChatApiFailure(ex);
        }
        catch (OperationCanceledException)
        {
            // 工作台关闭或站点停止时的正常取消。
        }
        catch (Exception ex)
        {
            AdminChatStatusText = $"实时同步刷新失败：{ex.Message}";
            AddLog($"⚠️ AdminChat 实时同步刷新失败: {ex.Message}");
            CrashDiagnosticService.ReportHandledException("AdminChat 实时同步刷新", ex);
        }
    }

    private void OnDesktopAuthorizedSyncAuthorizationFailed(string message, string accessToken)
    {
        try
        {
            // 授权同步是附加能力。0.9.0 曾在这个 SSE 回调中停止流并再次请求会话列表，
            // 恰好与刚完成登录时的会话加载、选择项变更并发，macOS 上可能导致主窗口
            // 生命周期被异常打断。同步流已经在通知后自行结束，因此这里只做安全降级，
            // 不再从回调中发起网络请求、停止流或改变已验证的 AdminChat 登录状态。
            Dispatcher.UIThread.Post(() => HandleAuthorizedSyncAuthorizationFailure(message, accessToken));
        }
        catch (Exception ex)
        {
            CrashDiagnosticService.ReportHandledException("投递 AdminChat 实时同步授权失败状态", ex);
        }
    }

    private void HandleAuthorizedSyncAuthorizationFailure(string message, string accessToken)
    {
        try
        {
            // StartAuthorizedSyncAsync 会替换旧的 SSE 连接。旧连接可能在取消后才把
            // 401/403 通知送达；它不能注销已经使用新 JWT 完成的登录。
            if (!IsAdminAuthenticated ||
                Volatile.Read(ref _adminChatSessionMutationInProgress) != 0 ||
                !string.Equals(_adminChatClient.Authentication?.AccessToken, accessToken, StringComparison.Ordinal))
            {
                return;
            }

            AdminChatStatusText = $"{message} AdminChat 当前登录已保留；实时同步已暂停。";
            AddLog($"⚠️ {message}；为避免影响已完成的登录，实时同步已安全降级。业务 API 后续若返回 401/403，才会回到登录界面。");
        }
        catch (Exception ex)
        {
            // 状态提示本身也不能影响主窗口生命周期。
            CrashDiagnosticService.ReportHandledException("更新 AdminChat 实时同步授权失败状态", ex);
        }
    }

    private void HandleAdminChatApiFailure(AdminChatApiException ex)
    {
        AdminChatStatusText = ex.Message;
        if (!ex.IsAuthenticationFailure)
        {
            return;
        }

        AddLog($"🔒 Admin Chat 鉴权已失效，已返回登录界面: {ex.Message}");
        _adminChatClient.ClearAuthentication();
        StopAgentPortalSynchronization();
        StopNeuBellSynchronization();
        IsAdminAuthenticated = false;
        AdminChatSessions.Clear();
        AdminChatMessages.Clear();
        RefreshWakeWordChatSessionOptions();
        ResetAdminChatOptions();
        SelectedAdminChatSession = null;
        OnPropertyChanged(nameof(AdminChatAccountText));
    }

    private void ResetAdminChatState()
    {
        CancelAdminWebHandoff();
        StopAgentPortalSynchronization();
        StopNeuBellSynchronization();
        _suppressAdminWebHandoffUntilWebLogin = false;
        _nextAdminWebHandoffAttemptUtc = default;
        _adminChatClient.ClearAuthentication();
        _desktopBridgeCapabilities = null;
        Dispatcher.UIThread.Post(() =>
        {
            IsAdminAuthenticated = false;
            IsDesktopBridgeAvailableForChat = false;
            AdminPassword = string.Empty;
            AdminChatSessions.Clear();
            AdminChatMessages.Clear();
            RefreshWakeWordChatSessionOptions();
            ResetAdminChatOptions();
            SelectedAdminChatSession = null;
            AdminChatStatusText = "启动 NCF 并连接 DesktopBridge 后可登录。";
            OnPropertyChanged(nameof(AdminChatAccountText));
            OnPropertyChanged(nameof(AdminChatDisabledReason));
        });
    }

    private void NotifyAdminChatStateChanged()
    {
        OnPropertyChanged(nameof(AdminChatMascotPose));
        OnPropertyChanged(nameof(IsAdminLoginVisible));
        OnPropertyChanged(nameof(IsAdminChatActive));
        OnPropertyChanged(nameof(IsAdminChatUnavailable));
        OnPropertyChanged(nameof(AdminChatDisabledReason));
        AdminLoginCommand.NotifyCanExecuteChanged();
        NewAdminChatSessionCommand.NotifyCanExecuteChanged();
        DeleteAdminChatSessionCommand.NotifyCanExecuteChanged();
        ApplyAdminChatModulesCommand.NotifyCanExecuteChanged();
        SendAdminChatMessageCommand.NotifyCanExecuteChanged();
        ScheduleWakeWordListeningRefresh();
    }

    private int AddOptimisticUserMessage(int sessionId, string content)
    {
        var id = -Interlocked.Increment(ref _optimisticMessageId);
        var sequence = AdminChatMessages.Count == 0
            ? 1
            : AdminChatMessages.Max(message => message.Sequence) + 1;
        AdminChatMessages.Add(new AdminChatMessage(
            id,
            sessionId,
            0,
            content,
            sequence,
            DateTime.Now,
            null));
        return id;
    }

    private void ReconcileUserMessage(int optimisticId, AdminChatMessage message)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var optimisticIndex = FindMessageIndex(optimisticId);
            if (optimisticIndex >= 0 && optimisticIndex < AdminChatMessages.Count)
            {
                AdminChatMessages.RemoveAt(optimisticIndex);
            }

            AddOrReplaceAdminChatMessage(message);
        });
    }

    private void AppendStreamingAssistantChunk(int sessionId, string chunk)
    {
        if (string.IsNullOrEmpty(chunk))
        {
            return;
        }

        var generation = Volatile.Read(ref _streamingGeneration);
        lock (_streamingChunkLock)
        {
            if (_pendingStreamingSessionId != sessionId ||
                _pendingStreamingGeneration != generation)
            {
                _pendingStreamingChunks.Clear();
                _pendingStreamingSessionId = sessionId;
                _pendingStreamingGeneration = generation;
            }

            _pendingStreamingChunks.Append(chunk);
        }

        ScheduleStreamingChunkFlush();
    }

    private void HandleStreamingAssistantChunk(int sessionId, string chunk)
    {
        AppendStreamingAssistantChunk(sessionId, chunk);
        AppendStreamingAutoReadChunk(sessionId, chunk);
    }

    private void ScheduleStreamingChunkFlush()
    {
        if (Interlocked.CompareExchange(ref _streamingChunkFlushScheduled, 1, 0) != 0)
        {
            return;
        }

        _ = FlushStreamingChunksAsync();
    }

    private async Task FlushStreamingChunksAsync()
    {
        try
        {
            // 合并高频 SSE token，避免每个 token 都触发布局、重绘和自动滚动。
            await Task.Delay(50).ConfigureAwait(false);
            string content;
            int sessionId;
            int generation;
            lock (_streamingChunkLock)
            {
                content = _pendingStreamingChunks.ToString();
                _pendingStreamingChunks.Clear();
                sessionId = _pendingStreamingSessionId;
                generation = _pendingStreamingGeneration;
            }

            if (content.Length != 0)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                    AppendStreamingAssistantChunkOnUi(sessionId, generation, content));
            }
        }
        catch (Exception ex)
        {
            AddLog($"⚠️ 流式回复界面刷新失败: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _streamingChunkFlushScheduled, 0);
            bool hasPendingChunks;
            lock (_streamingChunkLock)
            {
                hasPendingChunks = _pendingStreamingChunks.Length != 0;
            }

            if (hasPendingChunks)
            {
                ScheduleStreamingChunkFlush();
            }
        }
    }

    private void AppendStreamingAssistantChunkOnUi(int sessionId, int generation, string chunk)
    {
        // 完整消息可能已先到达并替换临时消息，此时丢弃上一代迟到的界面刷新。
        if (generation != Volatile.Read(ref _streamingGeneration))
        {
            return;
        }

        if (_activeStreamingAssistantId == 0)
        {
            _activeStreamingAssistantId = -Interlocked.Increment(ref _optimisticMessageId);
            var sequence = AdminChatMessages.Count == 0
                ? 1
                : AdminChatMessages.Max(message => message.Sequence) + 1;
            AdminChatMessages.Add(new AdminChatMessage(
                _activeStreamingAssistantId,
                sessionId,
                1,
                chunk,
                sequence,
                DateTime.Now,
                null));
            return;
        }

        var index = FindMessageIndex(_activeStreamingAssistantId);
        if (index < 0 || index >= AdminChatMessages.Count)
        {
            return;
        }

        var existing = AdminChatMessages[index];
        AdminChatMessages[index] = existing with { Content = existing.Content + chunk };
    }

    private void CompleteStreamingAssistantMessage(AdminChatMessage message)
    {
        ClearPendingStreamingChunks();
        Dispatcher.UIThread.Post(() =>
        {
            RemoveMessageById(_activeStreamingAssistantId);
            AddOrReplaceAdminChatMessage(message);
            _activeStreamingAssistantId = 0;
            if (!CompleteStreamingAutoRead(message))
            {
                _ = TryAutoReadAssistantMessageAsync(message);
            }
        });
    }

    private void RemovePendingStreamingMessages()
    {
        CancelActiveStreamingAutoRead();
        ClearPendingStreamingChunks();
        Dispatcher.UIThread.Post(() =>
        {
            RemoveMessageById(_activeStreamingAssistantId);
            _activeStreamingAssistantId = 0;
            for (var index = AdminChatMessages.Count - 1; index >= 0; index--)
            {
                if (AdminChatMessages[index].Id < 0 && AdminChatMessages[index].IsUser)
                {
                    AdminChatMessages.RemoveAt(index);
                }
            }
        });
    }

    private void ClearPendingStreamingChunks()
    {
        var generation = Interlocked.Increment(ref _streamingGeneration);
        lock (_streamingChunkLock)
        {
            _pendingStreamingChunks.Clear();
            _pendingStreamingSessionId = 0;
            _pendingStreamingGeneration = generation;
        }
    }

    private void AddOrReplaceAdminChatMessage(AdminChatMessage message)
    {
        var index = FindMessageIndex(message.Id);
        if (index >= 0 && index < AdminChatMessages.Count)
        {
            AdminChatMessages[index] = message;
        }
        else
        {
            AdminChatMessages.Add(message);
        }
    }

    private void RemoveMessageById(int id)
    {
        if (id == 0)
        {
            return;
        }

        var index = FindMessageIndex(id);
        if (index >= 0 && index < AdminChatMessages.Count)
        {
            AdminChatMessages.RemoveAt(index);
        }
    }

    private int FindMessageIndex(int id)
    {
        for (var index = 0; index < AdminChatMessages.Count; index++)
        {
            if (AdminChatMessages[index].Id == id)
            {
                return index;
            }
        }

        return -1;
    }
}
