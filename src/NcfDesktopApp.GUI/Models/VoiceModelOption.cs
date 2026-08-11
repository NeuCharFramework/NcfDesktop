/*----------------------------------------------------------------
    Copyright (C) 2026 Senparc

    文件名：VoiceModelOption.cs
    文件功能描述：桌面端本地语音识别模型选项

    创建标识：Senparc - 20260801

    修改标识：Senparc - 20260804
    修改描述：v0.6.0 扩展语音模型选项并适配 .NET 10

    修改标识：Senparc - 20260808
    修改描述：v0.9.0 展示名随界面语言切换

    修改标识：Senparc - 20260812
    修改描述：v0.10.0 完善桌面端唤醒词会话激活与中英文提示

----------------------------------------------------------------*/

using NcfDesktopApp.GUI.Services;

namespace NcfDesktopApp.GUI.Models;

public enum LocalVoiceModelKind
{
    Tiny,
    Base,
    Small,
    Custom
}

/// <summary>
/// 可在桌面端配置中选择的离线 Whisper 模型。
/// </summary>
public sealed record VoiceModelOption(
    string Id,
    LocalVoiceModelKind Kind,
    string FileName,
    long ApproximateBytes,
    long MinimumExpectedBytes,
    bool CanDownload,
    int ApproximateSizeMiB = 0)
{
    public string DisplayName => LocalizationService.T($"Voice.Model.{Id}.Name");

    public string Description => LocalizationService.T($"Voice.Model.{Id}.Desc");

    public string ApproximateSizeText => CanDownload
        ? LocalizationService.T("Voice.Size.ApproxMiB", ApproximateSizeMiB)
        : LocalizationService.T("Voice.Size.UserProvided");

    public string DisplayLabel => $"{DisplayName} · {ApproximateSizeText}";

    public override string ToString() => DisplayLabel;
}

internal enum VoiceModelReadinessState
{
    NotSelected,
    Missing,
    Incomplete,
    Ready
}

internal sealed record VoiceModelReadiness(
    VoiceModelReadinessState State,
    string ModelPath,
    string Message)
{
    public bool IsReady => State == VoiceModelReadinessState.Ready;
}
