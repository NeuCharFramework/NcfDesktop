using System.Text.Json.Serialization;

namespace NcfMobileApp.Core.Models;

public sealed record AdminChatAuthentication(
    string UserName,
    string AccessToken,
    DateTimeOffset? ExpiresUtc);

public sealed record AdminChatSessionSummary(
    int Id,
    string Title,
    DateTime LastMessageTime)
{
    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Title) ? $"会话 {Id}" : Title;
}

public sealed record AdminChatMessage(
    int Id,
    int SessionId,
    int RoleType,
    string Content,
    int Sequence,
    DateTime AddTime,
    string? ModelIdentifier)
{
    [JsonIgnore]
    public bool IsUser => RoleType == 0;

    [JsonIgnore]
    public bool IsAgent => !IsUser;

    [JsonIgnore]
    public string SenderName => IsUser ? "我" : "NCF Agent";
}

public sealed record AdminChatStreamResult(
    AdminChatMessage? UserMessage,
    AdminChatMessage? AssistantMessage);

public sealed class AdminChatApiException : Exception
{
    public AdminChatApiException(string message, bool isAuthenticationFailure = false)
        : base(message)
    {
        IsAuthenticationFailure = isAuthenticationFailure;
    }

    public bool IsAuthenticationFailure { get; }
}

internal sealed class AppResponseEnvelope<T>
{
    public bool? Success { get; set; }

    public string? ErrorMessage { get; set; }

    public T? Data { get; set; }
}

internal sealed class AdminLoginData
{
    public string? UserName { get; set; }

    public string? Token { get; set; }

    public DateTimeOffset? TokenExpiresUtc { get; set; }
}

internal sealed class AdminChatSessionListData
{
    public List<AdminChatSessionSummary> Sessions { get; set; } = [];
}

internal sealed class AdminChatSessionDetailData
{
    public AdminChatSessionDetail? Session { get; set; }
}

internal sealed class AdminChatSessionDetail
{
    public int Id { get; set; }

    public string Title { get; set; } = string.Empty;

    public List<AdminChatMessage> Messages { get; set; } = [];
}

internal sealed class AdminChatCreateSessionData
{
    public int SessionId { get; set; }
}

internal sealed class AdminChatSendMessageData
{
    public AdminChatMessage? UserMessage { get; set; }

    public AdminChatMessage? AssistantMessage { get; set; }
}
