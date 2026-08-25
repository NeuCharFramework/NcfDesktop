/*----------------------------------------------------------------
    Copyright (C) 2026 Senparc

    文件名：WakeWordConfiguration.cs
    文件功能描述：桌面宠物唤醒词及其 Chat 行为配置

    创建标识：Senparc - 20260822

----------------------------------------------------------------*/

using System;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace NcfDesktopApp.GUI.Models;

/// <summary>
/// 唤醒词命中后的处理方式。
/// </summary>
public enum WakeWordActionKind
{
    ChatSession
}

public sealed record WakeWordChatSessionOption(int Id, string DisplayName);

/// <summary>
/// 单个可自定义唤醒词。中文短语会自动转换为 KWS 模型所需的拼音；
/// 需要纠正多音字时可以填写 Pinyin。
/// </summary>
public sealed partial class WakeWordConfiguration : ObservableObject
{
    public WakeWordConfiguration()
    {
        Id = Guid.NewGuid().ToString("N");
    }

    public WakeWordConfiguration(string id)
    {
        Id = string.IsNullOrWhiteSpace(id)
            ? Guid.NewGuid().ToString("N")
            : id.Trim();
    }

    public string Id { get; set; }

    [ObservableProperty]
    private string _phrase = string.Empty;

    [ObservableProperty]
    private string _pinyin = string.Empty;

    [ObservableProperty]
    private bool _isEnabled = true;

    [ObservableProperty]
    private bool _isDefault;

    [ObservableProperty]
    private WakeWordActionKind _action = WakeWordActionKind.ChatSession;

    [ObservableProperty]
    private int _targetSessionId;

    [ObservableProperty]
    private string _targetSessionTitle = string.Empty;

    /// <summary>
    /// 仅用于设置页 ComboBox，不写入桌面配置文件。
    /// </summary>
    private WakeWordChatSessionOption? _selectedTargetSession;

    [JsonIgnore]
    public WakeWordChatSessionOption? SelectedTargetSession
    {
        get => _selectedTargetSession;
        set
        {
            if (!SetProperty(ref _selectedTargetSession, value))
            {
                return;
            }

            TargetSessionId = value?.Id ?? 0;
            TargetSessionTitle = value?.DisplayName ?? string.Empty;
        }
    }

    [JsonIgnore]
    public string DisplayLabel => string.IsNullOrWhiteSpace(Phrase)
        ? "未命名唤醒词"
        : Phrase.Trim();

    [JsonIgnore]
    public string TargetSessionLabel => TargetSessionId > 0
        ? string.IsNullOrWhiteSpace(TargetSessionTitle)
            ? $"Chat #{TargetSessionId}"
            : TargetSessionTitle
        : "当前选中的 Chat";

    [JsonIgnore]
    [ObservableProperty]
    private bool _isWakeDetectionValid = true;

    [JsonIgnore]
    [ObservableProperty]
    private string _wakeDetectionValidationMessage = string.Empty;

    [JsonIgnore]
    public string WakeDetectionValidationColor => IsWakeDetectionValid ? "#16A34A" : "#DC2626";

    partial void OnPhraseChanged(string value)
    {
        if (!string.IsNullOrWhiteSpace(Pinyin))
        {
            Pinyin = string.Empty;
        }

        OnPropertyChanged(nameof(DisplayLabel));
    }

    partial void OnTargetSessionIdChanged(int value) => OnPropertyChanged(nameof(TargetSessionLabel));

    partial void OnTargetSessionTitleChanged(string value) => OnPropertyChanged(nameof(TargetSessionLabel));

    partial void OnIsWakeDetectionValidChanged(bool value) =>
        OnPropertyChanged(nameof(WakeDetectionValidationColor));

}
