/*----------------------------------------------------------------
    Copyright (C) 2026 Senparc

    文件名：TtsModelOption.cs
    文件功能描述：桌面端离线文本转语音模型与音色选项

    创建标识：Senparc - 20260803

    修改标识：Senparc - 20260804
    修改描述：v0.6.0 增加本地 TTS 模型选项与下载状态

    修改标识：Senparc - 20260808
    修改描述：v0.9.0 展示名随界面语言切换

----------------------------------------------------------------*/

using NcfDesktopApp.GUI.Services;

namespace NcfDesktopApp.GUI.Models;

public enum LocalTtsModelKind
{
    Kokoro,
    Custom
}

public sealed record TtsModelOption(
    string Id,
    LocalTtsModelKind Kind,
    string ArchiveFileName,
    string DownloadUrl,
    long ApproximateBytes,
    bool CanDownload,
    int ApproximateSizeMiB = 0)
{
    public string DisplayName => LocalizationService.T($"Tts.Model.{Id}.Name");

    public string Description => LocalizationService.T($"Tts.Model.{Id}.Desc");

    public string ApproximateSizeText => CanDownload
        ? LocalizationService.T("Voice.Size.ApproxMiB", ApproximateSizeMiB)
        : LocalizationService.T("Voice.Size.UserProvided");

    public string DisplayLabel => $"{DisplayName} · {ApproximateSizeText}";

    public override string ToString() => DisplayLabel;
}

public sealed record TtsVoiceOption(int SpeakerId)
{
    public string DisplayName => LocalizationService.T($"Tts.Voice.{SpeakerId}.Name");

    public string Description => LocalizationService.T($"Tts.Voice.{SpeakerId}.Desc");

    public override string ToString() => DisplayName;
}

internal enum TtsModelReadinessState
{
    NotSelected,
    Missing,
    Incomplete,
    Ready
}

internal sealed record TtsModelFiles(
    string RootDirectory,
    string Model,
    string Voices,
    string Tokens,
    string DataDirectory,
    string Lexicons,
    string RuleFsts);

internal sealed record TtsModelReadiness(
    TtsModelReadinessState State,
    string ModelDirectory,
    string Message,
    TtsModelFiles? Files = null)
{
    public bool IsReady => State == TtsModelReadinessState.Ready && Files != null;
}
