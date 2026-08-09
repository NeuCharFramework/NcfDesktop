/*----------------------------------------------------------------
    Copyright (C) 2026 Senparc

    文件名：AdminChatProtocol.cs
    文件功能描述：桌面 Admin Chat API 与界面协议模型

    创建标识：Senparc - 20260726
----------------------------------------------------------------*/

using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using NcfDesktopApp.GUI.Services;

namespace NcfDesktopApp.GUI.Models;

public sealed record AdminChatAuthentication(
    string UserName,
    string AccessToken,
    DateTimeOffset? ExpiresUtc);

public sealed record AdminChatSessionSummary(
    int Id,
    string Title,
    DateTime LastMessageTime)
{
    public string DisplayName => string.IsNullOrWhiteSpace(Title)
        ? LocalizationService.T("Chat.SessionFallback", Id)
        : Title;
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
    public bool IsUser => RoleType == 0;

    public bool IsAgent => !IsUser;

    public string SenderName => IsUser ? LocalizationService.T("Chat.SenderMe") : "NCF Agent";

    public string SenderColor => IsUser ? "#2563EB" : "#7C3AED";

    public string DisplayTime => AddTime == default ? string.Empty : AddTime.ToLocalTime().ToString("HH:mm");

    public bool CanDelete => Id > 0;
}

public sealed record AdminChatAiModelOption(
    int Id,
    string Name,
    string Description,
    bool IsDefault)
{
    public string DisplayName => string.IsNullOrWhiteSpace(Name)
        ? LocalizationService.T("Chat.ModelFallback", Id)
        : Name;
}

public sealed record AdminChatAvailableModule(
    string Uid,
    string Name,
    string DisplayName,
    string Version,
    string Description,
    string Icon,
    bool IsRequired);

public sealed record AdminChatSessionModule(
    int SessionId,
    string XncfModuleUid,
    string ModuleName,
    string ModuleVersion,
    string DisplayName,
    string? MenuName,
    string? ModuleDescription);

public sealed partial class AdminChatModuleOption : ObservableObject
{
    public AdminChatModuleOption(AdminChatAvailableModule module)
    {
        Module = module;
    }

    public AdminChatAvailableModule Module { get; }

    public string Uid => Module.Uid;

    public string Name => Module.Name;

    public string Version => Module.Version;

    public string Description => Module.Description;

    public string DisplayName => string.IsNullOrWhiteSpace(Module.DisplayName) ? Module.Name : Module.DisplayName;

    public string DisplayLabel => string.IsNullOrWhiteSpace(Version)
        ? DisplayName
        : $"{DisplayName} · {Version}";

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isAssociated;

    public bool IsSelectionEnabled => !Module.IsRequired;
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
    public List<AdminChatSessionSummary> Sessions { get; set; } = new();
}

internal sealed class AdminChatSessionDetailData
{
    public AdminChatSessionDetail? Session { get; set; }
}

public sealed class AdminChatSessionDetail
{
    public int Id { get; set; }

    public string Title { get; set; } = string.Empty;

    public List<AdminChatMessage> Messages { get; set; } = new();

    public List<AdminChatSessionModule> Modules { get; set; } = new();
}

internal sealed class AdminChatCreateSessionData
{
    public int SessionId { get; set; }

    public string Title { get; set; } = string.Empty;
}

internal sealed class AdminChatSendMessageData
{
    public AdminChatMessage? UserMessage { get; set; }

    public AdminChatMessage? AssistantMessage { get; set; }
}

internal sealed class AdminChatAiModelOptionsData
{
    public bool AiKernelAvailable { get; set; }

    public List<AdminChatAiModelOption> Models { get; set; } = new();
}

internal sealed class AdminChatAvailableModulesData
{
    public List<AdminChatAvailableModule> Modules { get; set; } = new();
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
