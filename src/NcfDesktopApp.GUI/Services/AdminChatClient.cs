/*----------------------------------------------------------------
    Copyright (C) 2026 Senparc

    文件名：AdminChatClient.cs
    文件功能描述：受安全地址策略保护的 Admin JWT 登录与聊天 API 客户端

    创建标识：Senparc - 20260726
    
    修改标识：Senparc - 20260812
    修改描述：v0.10.0 完善桌面端唤醒词会话激活与中英文提示

    修改标识：Senparc - 20260826
    修改描述：v0.11.0 支持唤醒词绑定目标 Chat 会话

----------------------------------------------------------------*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NcfDesktopApp.GUI.Models;

namespace NcfDesktopApp.GUI.Services;

public sealed class AdminChatClient
{
    private const string AdminUserApi = "/api/Senparc.Areas.Admin/AdminUserInfoAppService/Areas.Admin_AdminUserInfoAppService";
    private const string AdminChatApi = "/api/Senparc.Areas.Admin/AdminChatAppService/Areas.Admin_AdminChatAppService";
    private const string AdminChatStreamApi = "/api/Senparc.Areas.Admin/AdminChatStream/send";
    private const string NeuBellStateApi = "/api/Senparc.Areas.Admin/neubell/state";
    private const string NeuBellEventsApi = "/api/Senparc.Areas.Admin/neubell/events";
    private const string AgentGraphSnapshotApi =
        "/api/Senparc.Xncf.AgentsManager/ChatGroupAppService/Xncf.AgentsManager_ChatGroupAppService.GetAgentGraphSnapshot";
    private const string AgentTaskUsageAnalyticsApi =
        "/api/Senparc.Xncf.AgentsManager/ChatGroupHistoryAppService/Xncf.AgentsManager_ChatGroupHistoryAppService.GetUsageAnalytics";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private AdminChatAuthentication? _authentication;

    public AdminChatClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public AdminChatAuthentication? Authentication => _authentication;

    public bool IsAuthenticated => _authentication is { AccessToken.Length: > 0 } authentication &&
                                   (authentication.ExpiresUtc == null ||
                                    authentication.ExpiresUtc > DateTimeOffset.UtcNow.AddSeconds(10));

    public async Task<AdminChatAuthentication> AuthenticateAsync(
        string siteUrl,
        string userName,
        string password,
        CancellationToken cancellationToken = default)
    {
        ClearAuthentication();
        if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrEmpty(password))
        {
            throw new AdminChatApiException("请输入管理员账号和密码。", true);
        }

        var login = await SendAsync<AdminLoginData>(
            siteUrl,
            HttpMethod.Post,
            $"{AdminUserApi}.LoginAsync",
            new { userName = userName.Trim(), password },
            accessToken: null,
            timeout: TimeSpan.FromSeconds(20),
            cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(login.Token) || string.IsNullOrWhiteSpace(login.UserName))
        {
            throw new AdminChatApiException("登录响应中没有有效的管理员令牌。", true);
        }

        return await AuthenticateWithAccessTokenAsync(
                siteUrl,
                login.UserName,
                login.Token,
                login.TokenExpiresUtc,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 接受由 DesktopBridge 一次性换票返回的 JWT。令牌仍需通过 AdminChat AdminOnly API 二次验证。
    /// </summary>
    public async Task<AdminChatAuthentication> AuthenticateWithAccessTokenAsync(
        string siteUrl,
        string userName,
        string accessToken,
        DateTimeOffset? expiresUtc,
        CancellationToken cancellationToken = default)
    {
        ClearAuthentication();
        if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrWhiteSpace(accessToken) ||
            expiresUtc is not { } tokenExpiresUtc || tokenExpiresUtc <= DateTimeOffset.UtcNow.AddSeconds(10))
        {
            throw new AdminChatApiException("WebView 自动授权返回了无效或已过期的管理员令牌。", true);
        }

        var candidate = new AdminChatAuthentication(userName.Trim(), accessToken, expiresUtc);
        try
        {
            // 由 AdminChat 的 AdminOnly 策略做最终授权判断，而不是信任登录响应中的角色文本。
            await GetSessionsCoreAsync(siteUrl, candidate.AccessToken, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            ClearAuthentication();
            throw;
        }

        _authentication = candidate;
        return candidate;
    }

    public void ClearAuthentication()
    {
        _authentication = null;
    }

    /// <summary>
    /// 使用仅驻留内存的 Admin JWT 读取 AgentsManager 页面同源聚合快照。
    /// 响应模型刻意忽略 PromptCode、头像等门户绘制不需要的数据。
    /// </summary>
    public Task<AgentGraphSnapshot> GetAgentGraphSnapshotAsync(
        string siteUrl,
        CancellationToken cancellationToken = default)
    {
        return SendAsync<AgentGraphSnapshot>(
            siteUrl,
            HttpMethod.Get,
            AgentGraphSnapshotApi,
            body: null,
            accessToken: GetRequiredAccessToken(),
            timeout: TimeSpan.FromSeconds(12),
            cancellationToken: cancellationToken,
            clearAuthenticationOnAuthorizationFailure: false);
    }

    /// <summary>
    /// 读取单个活动任务的已授权用量摘要。该请求不包含消息正文、提示词或费用推算；
    /// 门户在展开时低频采样，避免为后台空闲状态额外产生请求。
    /// </summary>
    public Task<AgentTaskUsageAnalytics> GetAgentTaskUsageAnalyticsAsync(
        string siteUrl,
        int chatTaskId,
        CancellationToken cancellationToken = default)
    {
        if (chatTaskId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(chatTaskId));
        }

        return SendAsync<AgentTaskUsageAnalytics>(
            siteUrl,
            HttpMethod.Get,
            $"{AgentTaskUsageAnalyticsApi}?chatTaskId={chatTaskId}",
            body: null,
            accessToken: GetRequiredAccessToken(),
            timeout: TimeSpan.FromSeconds(12),
            cancellationToken: cancellationToken,
            clearAuthenticationOnAuthorizationFailure: false);
    }

    /// <summary>
    /// 使用内存中的 Admin JWT 读取同源纽铃快照。该 Controller 在不同 NCF Host 中可能返回
    /// 直接 JSON 或标准 AppResponse 包装，因此客户端同时兼容两种安全响应形态。
    /// </summary>
    public async Task<NeuBellState> GetNeuBellStateAsync(
        string siteUrl,
        CancellationToken cancellationToken = default)
    {
        if (!SiteEndpointPolicy.TryCreateEndpoint(siteUrl, NeuBellStateApi, out var endpoint, out var endpointError))
        {
            throw new AdminChatApiException(endpointError);
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", GetRequiredAccessToken());
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(TimeSpan.FromSeconds(12));

        HttpResponseMessage response;
        try
        {
            response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseContentRead, timeoutSource.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AdminChatApiException("纽铃状态请求超时，请稍后重试。");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            throw new AdminChatApiException($"无法连接纽铃服务：{ex.Message}");
        }

        using (response)
        {
            EnsureAuthorizedResponse(response, clearAuthenticationOnAuthorizationFailure: false);
            if (!response.IsSuccessStatusCode)
            {
                throw new AdminChatApiException($"纽铃服务返回 HTTP {(int)response.StatusCode}。");
            }

            try
            {
                var json = await response.Content.ReadAsStringAsync(timeoutSource.Token).ConfigureAwait(false);
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;
                if (root.TryGetProperty("success", out var successElement) &&
                    successElement.ValueKind == JsonValueKind.False)
                {
                    var errorMessage = root.TryGetProperty("errorMessage", out var errorElement)
                        ? errorElement.GetString()
                        : null;
                    throw new AdminChatApiException(errorMessage ?? "纽铃状态读取失败。");
                }

                var payload = root.TryGetProperty("data", out var dataElement) &&
                              dataElement.ValueKind == JsonValueKind.Object
                    ? dataElement
                    : root;
                return payload.Deserialize<NeuBellState>(JsonOptions)
                       ?? throw new AdminChatApiException("纽铃服务没有返回有效状态。");
            }
            catch (AdminChatApiException)
            {
                throw;
            }
            catch (JsonException)
            {
                throw new AdminChatApiException("纽铃服务返回了无法识别的数据。");
            }
        }
    }

    /// <summary>
    /// 等待一次纽铃 SSE 变更。返回 false 表示当前没有可用 Provider 或连接自然结束；调用方以
    /// 低频轮询兜底。Bearer JWT 仅放在请求头中，不进入 URL、Cookie 或磁盘。
    /// </summary>
    public async Task<bool> WaitForNeuBellChangeAsync(
        string siteUrl,
        CancellationToken cancellationToken = default)
    {
        if (!SiteEndpointPolicy.TryCreateEndpoint(siteUrl, NeuBellEventsApi, out var endpoint, out var endpointError))
        {
            throw new AdminChatApiException(endpointError);
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", GetRequiredAccessToken());
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        HttpResponseMessage response;
        try
        {
            response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            throw new AdminChatApiException($"纽铃实时连接失败：{ex.Message}");
        }

        using (response)
        {
            EnsureAuthorizedResponse(response, clearAuthenticationOnAuthorizationFailure: false);
            if (response.StatusCode == HttpStatusCode.NoContent)
            {
                return false;
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new AdminChatApiException($"纽铃实时服务返回 HTTP {(int)response.StatusCode}。");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            string? eventName = null;
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line == null)
                {
                    return false;
                }

                if (line.StartsWith("event:", StringComparison.OrdinalIgnoreCase))
                {
                    eventName = line[6..].Trim();
                }
                else if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase) &&
                         string.Equals(eventName, "neubell-changed", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
                else if (line.Length == 0)
                {
                    if (string.Equals(eventName, "neubell-changed", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }

                    eventName = null;
                }
            }

            return false;
        }
    }

    public Task<IReadOnlyList<AdminChatSessionSummary>> GetSessionsAsync(
        string siteUrl,
        CancellationToken cancellationToken = default)
    {
        return GetSessionsCoreAsync(siteUrl, GetRequiredAccessToken(), cancellationToken);
    }

    public async Task<int> CreateSessionAsync(
        string siteUrl,
        int aiModelId,
        IReadOnlyCollection<string>? moduleUids,
        CancellationToken cancellationToken = default)
    {
        var data = await SendAsync<AdminChatCreateSessionData>(
            siteUrl,
            HttpMethod.Post,
            $"{AdminChatApi}.CreateSessionAsync",
            new
            {
                initialMessage = string.Empty,
                aiModelId = Math.Max(0, aiModelId),
                moduleUids = moduleUids?
                    .Where(uid => !string.IsNullOrWhiteSpace(uid))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray() ?? Array.Empty<string>()
            },
            GetRequiredAccessToken(),
            TimeSpan.FromSeconds(30),
            cancellationToken).ConfigureAwait(false);

        if (data.SessionId <= 0)
        {
            throw new AdminChatApiException("Admin Chat 未返回有效的会话 ID。");
        }

        return data.SessionId;
    }

    public Task<int> CreateSessionAsync(
        string siteUrl,
        CancellationToken cancellationToken = default)
    {
        return CreateSessionAsync(siteUrl, 0, Array.Empty<string>(), cancellationToken);
    }

    public async Task<AdminChatSessionDetail?> GetSessionDetailAsync(
        string siteUrl,
        int sessionId,
        CancellationToken cancellationToken = default)
    {
        var data = await SendAsync<AdminChatSessionDetailData>(
            siteUrl,
            HttpMethod.Get,
            $"{AdminChatApi}.GetSessionDetailAsync?sessionId={sessionId}",
            body: null,
            accessToken: GetRequiredAccessToken(),
            timeout: TimeSpan.FromSeconds(30),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return data.Session;
    }

    public async Task<IReadOnlyList<AdminChatMessage>> GetSessionMessagesAsync(
        string siteUrl,
        int sessionId,
        CancellationToken cancellationToken = default)
    {
        var session = await GetSessionDetailAsync(siteUrl, sessionId, cancellationToken).ConfigureAwait(false);
        return session?.Messages
                   .OrderBy(message => message.Sequence)
                   .ThenBy(message => message.Id)
                   .ToArray()
               ?? Array.Empty<AdminChatMessage>();
    }

    public async Task<IReadOnlyList<AdminChatAiModelOption>> GetAiModelOptionsAsync(
        string siteUrl,
        CancellationToken cancellationToken = default)
    {
        var data = await SendAsync<AdminChatAiModelOptionsData>(
            siteUrl,
            HttpMethod.Get,
            $"{AdminChatApi}.GetAiModelOptionsAsync",
            body: null,
            accessToken: GetRequiredAccessToken(),
            timeout: TimeSpan.FromSeconds(30),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return data.Models
            .OrderByDescending(model => model.IsDefault)
            .ThenBy(model => model.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public async Task<IReadOnlyList<AdminChatAvailableModule>> GetAvailableModulesAsync(
        string siteUrl,
        CancellationToken cancellationToken = default)
    {
        var data = await SendAsync<AdminChatAvailableModulesData>(
            siteUrl,
            HttpMethod.Get,
            $"{AdminChatApi}.GetAvailableModulesAsync",
            body: null,
            accessToken: GetRequiredAccessToken(),
            timeout: TimeSpan.FromSeconds(30),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return data.Modules
            .OrderBy(module => module.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public async Task DeleteSessionAsync(
        string siteUrl,
        int sessionId,
        CancellationToken cancellationToken = default)
    {
        _ = await SendAsync<string>(
            siteUrl,
            HttpMethod.Delete,
            $"{AdminChatApi}.DeleteSessionAsync?sessionId={sessionId}",
            body: null,
            accessToken: GetRequiredAccessToken(),
            timeout: TimeSpan.FromSeconds(30),
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteMessagesAsync(
        string siteUrl,
        int sessionId,
        IReadOnlyCollection<int> messageIds,
        CancellationToken cancellationToken = default)
    {
        var ids = messageIds.Where(id => id > 0).Distinct().ToArray();
        if (ids.Length == 0)
        {
            throw new AdminChatApiException("请至少选择一条可删除的消息。");
        }

        _ = await SendAsync<string>(
            siteUrl,
            HttpMethod.Delete,
            $"{AdminChatApi}.DeleteMessagesAsync?sessionId={sessionId}&messageIds={string.Join(',', ids)}",
            body: null,
            accessToken: GetRequiredAccessToken(),
            timeout: TimeSpan.FromSeconds(30),
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task SetModulesForSessionAsync(
        string siteUrl,
        int sessionId,
        IReadOnlyCollection<AdminChatAvailableModule> modules,
        CancellationToken cancellationToken = default)
    {
        var requestModules = modules
            .Where(module => !string.IsNullOrWhiteSpace(module.Uid))
            .GroupBy(module => module.Uid, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Select(module => new { uid = module.Uid, name = module.Name, version = module.Version })
            .ToArray();
        _ = await SendAsync<string>(
            siteUrl,
            HttpMethod.Post,
            $"{AdminChatApi}.SetSessionModulesAsync",
            new { sessionId, modules = requestModules },
            GetRequiredAccessToken(),
            TimeSpan.FromSeconds(30),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AdminChatMessage>> SendMessageAsync(
        string siteUrl,
        int sessionId,
        string content,
        int aiModelId = 0,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new AdminChatApiException("请输入消息内容。");
        }

        var data = await SendAsync<AdminChatSendMessageData>(
            siteUrl,
            HttpMethod.Post,
            $"{AdminChatApi}.SendMessageAsync",
            new { sessionId, aiModelId = Math.Max(0, aiModelId), content = content.Trim() },
            GetRequiredAccessToken(),
            TimeSpan.FromMinutes(3),
            cancellationToken).ConfigureAwait(false);

        return new[] { data.UserMessage, data.AssistantMessage }
            .OfType<AdminChatMessage>()
            .ToArray();
    }

    public async Task<AdminChatStreamResult> SendMessageStreamingAsync(
        string siteUrl,
        int sessionId,
        string content,
        int aiModelId = 0,
        Action<AdminChatMessage>? onUserMessage = null,
        Action<string>? onToken = null,
        Action<AdminChatMessage>? onAssistantMessage = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new AdminChatApiException("请输入消息内容。");
        }

        if (!SiteEndpointPolicy.TryCreateEndpoint(siteUrl, AdminChatStreamApi, out var endpoint, out var endpointError))
        {
            throw new AdminChatApiException(endpointError);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", GetRequiredAccessToken());
        request.Content = JsonContent.Create(
            new { sessionId, aiModelId = Math.Max(0, aiModelId), content = content.Trim() },
            options: JsonOptions);

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(TimeSpan.FromMinutes(3));

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeoutSource.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AdminChatApiException("Admin Chat 流式请求超时，请稍后重试。");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            throw new AdminChatApiException($"无法连接 Admin Chat：{ex.Message}");
        }

        using (response)
        {
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                ClearAuthentication();
                throw new AdminChatApiException("管理员身份无效、已过期或不具备 AdminOnly 权限。", true);
            }

            // 兼容尚未部署流式接口的旧站点：桌面端仍保留即时本地回显，但回复退回原有整包 API。
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
            {
                var fallbackMessages = await SendMessageAsync(siteUrl, sessionId, content, aiModelId, cancellationToken)
                    .ConfigureAwait(false);
                var fallbackUserMessage = fallbackMessages.FirstOrDefault(message => message.IsUser);
                var fallbackAssistantMessage = fallbackMessages.FirstOrDefault(message => message.IsAgent);
                if (fallbackUserMessage != null)
                {
                    onUserMessage?.Invoke(fallbackUserMessage);
                }

                if (fallbackAssistantMessage != null)
                {
                    onAssistantMessage?.Invoke(fallbackAssistantMessage);
                }

                return new AdminChatStreamResult(fallbackUserMessage, fallbackAssistantMessage);
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new AdminChatApiException($"Admin Chat 流式接口返回 HTTP {(int)response.StatusCode}。");
            }

            AdminChatMessage? userMessage = null;
            AdminChatMessage? assistantMessage = null;
            var eventName = string.Empty;
            var eventData = new StringBuilder();

            await using var responseStream = await response.Content
                .ReadAsStreamAsync(timeoutSource.Token)
                .ConfigureAwait(false);
            using var reader = new StreamReader(responseStream);

            while (await reader.ReadLineAsync(timeoutSource.Token).ConfigureAwait(false) is { } line)
            {
                if (line.Length == 0)
                {
                    HandleStreamEvent(
                        eventName,
                        eventData.ToString(),
                        ref userMessage,
                        ref assistantMessage,
                        onUserMessage,
                        onToken,
                        onAssistantMessage);
                    eventName = string.Empty;
                    eventData.Clear();
                    continue;
                }

                if (line.StartsWith("event:", StringComparison.OrdinalIgnoreCase))
                {
                    eventName = line["event:".Length..].Trim();
                }
                else if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    if (eventData.Length > 0)
                    {
                        eventData.Append('\n');
                    }

                    eventData.Append(line["data:".Length..].TrimStart());
                }
            }

            if (eventData.Length > 0)
            {
                HandleStreamEvent(
                    eventName,
                    eventData.ToString(),
                    ref userMessage,
                    ref assistantMessage,
                    onUserMessage,
                    onToken,
                    onAssistantMessage);
            }

            if (userMessage == null || assistantMessage == null)
            {
                throw new AdminChatApiException("Admin Chat 流式连接提前结束，未收到完整回复。");
            }

            return new AdminChatStreamResult(userMessage, assistantMessage);
        }
    }

    private static void HandleStreamEvent(
        string eventName,
        string eventData,
        ref AdminChatMessage? userMessage,
        ref AdminChatMessage? assistantMessage,
        Action<AdminChatMessage>? onUserMessage,
        Action<string>? onToken,
        Action<AdminChatMessage>? onAssistantMessage)
    {
        if (string.IsNullOrWhiteSpace(eventName) || string.IsNullOrWhiteSpace(eventData))
        {
            return;
        }

        try
        {
            switch (eventName)
            {
                case "user-message":
                    userMessage = JsonSerializer.Deserialize<AdminChatMessage>(eventData, JsonOptions);
                    if (userMessage != null)
                    {
                        onUserMessage?.Invoke(userMessage);
                    }
                    break;
                case "token":
                    using (var tokenDocument = JsonDocument.Parse(eventData))
                    {
                        if (tokenDocument.RootElement.TryGetProperty("text", out var textElement))
                        {
                            var text = textElement.GetString();
                            if (!string.IsNullOrEmpty(text))
                            {
                                onToken?.Invoke(text);
                            }
                        }
                    }
                    break;
                case "assistant-message":
                    assistantMessage = JsonSerializer.Deserialize<AdminChatMessage>(eventData, JsonOptions);
                    if (assistantMessage != null)
                    {
                        onAssistantMessage?.Invoke(assistantMessage);
                    }
                    break;
                case "error":
                    using (var errorDocument = JsonDocument.Parse(eventData))
                    {
                        var message = errorDocument.RootElement.TryGetProperty("message", out var messageElement)
                            ? messageElement.GetString()
                            : null;
                        throw new AdminChatApiException(
                            string.IsNullOrWhiteSpace(message) ? "Agent 回复失败。" : message);
                    }
            }
        }
        catch (AdminChatApiException)
        {
            throw;
        }
        catch (JsonException)
        {
            throw new AdminChatApiException("Admin Chat 流式接口返回了无法识别的数据。");
        }
    }

    private async Task<IReadOnlyList<AdminChatSessionSummary>> GetSessionsCoreAsync(
        string siteUrl,
        string accessToken,
        CancellationToken cancellationToken)
    {
        var data = await SendAsync<AdminChatSessionListData>(
            siteUrl,
            HttpMethod.Get,
            $"{AdminChatApi}.GetSessionListAsync?pageIndex=1&pageSize=50",
            body: null,
            accessToken: accessToken,
            timeout: TimeSpan.FromSeconds(30),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return data.Sessions
            .OrderByDescending(session => session.LastMessageTime)
            .ToArray();
    }

    private async Task<T> SendAsync<T>(
        string siteUrl,
        HttpMethod method,
        string relativePath,
        object? body,
        string? accessToken,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        bool clearAuthenticationOnAuthorizationFailure = true)
    {
        if (!SiteEndpointPolicy.TryCreateEndpoint(siteUrl, relativePath, out var endpoint, out var endpointError))
        {
            throw new AdminChatApiException(endpointError);
        }

        using var request = new HttpRequestMessage(method, endpoint);
        if (!string.IsNullOrWhiteSpace(accessToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        if (body != null)
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeoutSource.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AdminChatApiException("Admin Chat 请求超时，请稍后重试。");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            throw new AdminChatApiException($"无法连接 Admin Chat：{ex.Message}");
        }

        using (response)
        {
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                if (clearAuthenticationOnAuthorizationFailure)
                {
                    ClearAuthentication();
                }

                throw new AdminChatApiException("管理员身份无效、已过期或不具备 AdminOnly 权限。", true);
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new AdminChatApiException($"Admin Chat 返回 HTTP {(int)response.StatusCode}。");
            }

            AppResponseEnvelope<T>? envelope;
            try
            {
                var json = await response.Content.ReadAsStringAsync(timeoutSource.Token).ConfigureAwait(false);
                envelope = JsonSerializer.Deserialize<AppResponseEnvelope<T>>(json, JsonOptions);
            }
            catch (JsonException)
            {
                throw new AdminChatApiException("Admin Chat 返回了无法识别的数据。");
            }

            if (envelope?.Success != true || envelope.Data == null)
            {
                throw new AdminChatApiException(envelope?.ErrorMessage ?? "Admin Chat 操作失败。", accessToken == null);
            }

            return envelope.Data;
        }
    }

    private string GetRequiredAccessToken()
    {
        if (_authentication is { ExpiresUtc: { } expiresUtc } &&
            expiresUtc <= DateTimeOffset.UtcNow.AddSeconds(10))
        {
            ClearAuthentication();
            throw new AdminChatApiException(
                $"管理员 JWT 已于 {expiresUtc.LocalDateTime:yyyy-MM-dd HH:mm:ss} 过期，请重新登录。",
                true);
        }

        if (!IsAuthenticated || _authentication == null)
        {
            ClearAuthentication();
            throw new AdminChatApiException("管理员登录已失效，请重新登录。", true);
        }

        return _authentication.AccessToken;
    }

    private void EnsureAuthorizedResponse(
        HttpResponseMessage response,
        bool clearAuthenticationOnAuthorizationFailure = true)
    {
        if (response.StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden))
        {
            return;
        }

        if (clearAuthenticationOnAuthorizationFailure)
        {
            ClearAuthentication();
        }

        throw new AdminChatApiException("管理员身份无效、已过期或不具备 AdminOnly 权限。", true);
    }

    internal static bool TryCreateEndpoint(string siteUrl, string relativePath, out Uri endpoint)
    {
        return SiteEndpointPolicy.TryCreateEndpoint(siteUrl, relativePath, out endpoint, out _);
    }
}
