/*----------------------------------------------------------------
    Copyright (C) 2026 Senparc

    文件名：VoiceModelOption.cs
    文件功能描述：桌面端本地语音识别模型选项

    创建标识：Senparc - 20260801

    修改标识：Senparc - 20260804
    修改描述：v0.6.0 扩展语音模型选项并适配 .NET 10

----------------------------------------------------------------*/

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
    string DisplayName,
    string Description,
    string ApproximateSizeText,
    LocalVoiceModelKind Kind,
    string FileName,
    long ApproximateBytes,
    long MinimumExpectedBytes,
    bool CanDownload)
{
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
