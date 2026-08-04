using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using NcfMobileApp.Core.Models;

namespace NcfMobileApp.Core.Services;

public sealed class AdminChatClient
{
    private const string AdminUserApi = "/api/Senparc.Areas.Admin/AdminUserInfoAppService/Areas.Admin_AdminUserInfoAppService";
    private const string AdminChatApi = "/api/Senparc.Areas.Admin/AdminChatAppService/Areas.Admin_AdminChatAppService";
    private const string AdminChatStreamApi = "/api/Senparc.Areas.Admin/AdminChatStream/send";
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

        var candidate = new AdminChatAuthentication(login.UserName, login.Token, login.TokenExpiresUtc);
        try
        {
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

    public Task<IReadOnlyList<AdminChatSessionSummary>> GetSessionsAsync(
        string siteUrl,
        CancellationToken cancellationToken = default)
    {
        return GetSessionsCoreAsync(siteUrl, GetRequiredAccessToken(), cancellationToken);
    }

    public async Task<int> CreateSessionAsync(
        string siteUrl,
        CancellationToken cancellationToken = default)
    {
        var data = await SendAsync<AdminChatCreateSessionData>(
            siteUrl,
            HttpMethod.Post,
            $"{AdminChatApi}.CreateSessionAsync",
            new { initialMessage = string.Empty, aiModelId = 0, moduleUids = Array.Empty<string>() },
            GetRequiredAccessToken(),
            TimeSpan.FromSeconds(30),
            cancellationToken).ConfigureAwait(false);

        if (data.SessionId <= 0)
        {
            throw new AdminChatApiException("Admin Chat 未返回有效的会话 ID。");
        }

        return data.SessionId;
    }

    public async Task<IReadOnlyList<AdminChatMessage>> GetSessionMessagesAsync(
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
            cancellationToken).ConfigureAwait(false);

        return data.Session?.Messages
                   .OrderBy(message => message.Sequence)
                   .ThenBy(message => message.Id)
                   .ToArray()
               ?? [];
    }

    public async Task<IReadOnlyList<AdminChatMessage>> SendMessageAsync(
        string siteUrl,
        int sessionId,
        string content,
        CancellationToken cancellationToken = default)
    {
        EnsureMessage(content);
        var data = await SendAsync<AdminChatSendMessageData>(
            siteUrl,
            HttpMethod.Post,
            $"{AdminChatApi}.SendMessageAsync",
            new { sessionId, aiModelId = 0, content = content.Trim() },
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
        Action<AdminChatMessage>? onUserMessage = null,
        Action<string>? onToken = null,
        Action<AdminChatMessage>? onAssistantMessage = null,
        CancellationToken cancellationToken = default)
    {
        EnsureMessage(content);
        if (!SiteEndpointPolicy.TryCreateEndpoint(siteUrl, AdminChatStreamApi, out var endpoint, out var error))
        {
            throw new AdminChatApiException(error);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", GetRequiredAccessToken());
        request.Content = JsonContent.Create(
            new { sessionId, aiModelId = 0, content = content.Trim() },
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
            HandleAuthenticationFailure(response.StatusCode);
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
            {
                var fallback = await SendMessageAsync(siteUrl, sessionId, content, cancellationToken)
                    .ConfigureAwait(false);
                var user = fallback.FirstOrDefault(message => message.IsUser);
                var assistant = fallback.FirstOrDefault(message => message.IsAgent);
                if (user != null)
                {
                    onUserMessage?.Invoke(user);
                }

                if (assistant != null)
                {
                    onAssistantMessage?.Invoke(assistant);
                }

                return new AdminChatStreamResult(user, assistant);
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new AdminChatApiException($"Admin Chat 流式接口返回 HTTP {(int)response.StatusCode}。");
            }

            AdminChatMessage? userMessage = null;
            AdminChatMessage? assistantMessage = null;
            var eventName = string.Empty;
            var eventData = new StringBuilder();
            await using var stream = await response.Content.ReadAsStreamAsync(timeoutSource.Token).ConfigureAwait(false);
            using var reader = new StreamReader(stream);

            while (await reader.ReadLineAsync(timeoutSource.Token).ConfigureAwait(false) is { } line)
            {
                if (line.Length == 0)
                {
                    HandleStreamEvent(eventName, eventData.ToString(), ref userMessage, ref assistantMessage,
                        onUserMessage, onToken, onAssistantMessage);
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
                HandleStreamEvent(eventName, eventData.ToString(), ref userMessage, ref assistantMessage,
                    onUserMessage, onToken, onAssistantMessage);
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
                    using (var document = JsonDocument.Parse(eventData))
                    {
                        if (document.RootElement.TryGetProperty("text", out var element) &&
                            element.GetString() is { Length: > 0 } text)
                        {
                            onToken?.Invoke(text);
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
                    using (var document = JsonDocument.Parse(eventData))
                    {
                        var message = document.RootElement.TryGetProperty("message", out var element)
                            ? element.GetString()
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
            accessToken,
            TimeSpan.FromSeconds(30),
            cancellationToken).ConfigureAwait(false);

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
        CancellationToken cancellationToken)
    {
        if (!SiteEndpointPolicy.TryCreateEndpoint(siteUrl, relativePath, out var endpoint, out var error))
        {
            throw new AdminChatApiException(error);
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
            HandleAuthenticationFailure(response.StatusCode);
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

    private void HandleAuthenticationFailure(HttpStatusCode statusCode)
    {
        if (statusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden))
        {
            return;
        }

        ClearAuthentication();
        throw new AdminChatApiException("管理员身份无效、已过期或不具备 AdminOnly 权限。", true);
    }

    private string GetRequiredAccessToken()
    {
        if (!IsAuthenticated || _authentication == null)
        {
            ClearAuthentication();
            throw new AdminChatApiException("管理员登录已失效，请重新登录。", true);
        }

        return _authentication.AccessToken;
    }

    private static void EnsureMessage(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new AdminChatApiException("请输入消息内容。");
        }
    }
}
