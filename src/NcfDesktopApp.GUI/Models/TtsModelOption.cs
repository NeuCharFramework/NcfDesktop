/*----------------------------------------------------------------
    Copyright (C) 2026 Senparc

    文件名：TtsModelOption.cs
    文件功能描述：桌面端离线文本转语音模型与音色选项

    创建标识：Senparc - 20260803
----------------------------------------------------------------*/

namespace NcfDesktopApp.GUI.Models;

public enum LocalTtsModelKind
{
    Kokoro,
    Custom
}

public sealed record TtsModelOption(
    string Id,
    string DisplayName,
    string Description,
    string ApproximateSizeText,
    LocalTtsModelKind Kind,
    string ArchiveFileName,
    string DownloadUrl,
    long ApproximateBytes,
    bool CanDownload)
{
    public string DisplayLabel => $"{DisplayName} · {ApproximateSizeText}";

    public override string ToString() => DisplayLabel;
}

public sealed record TtsVoiceOption(int SpeakerId, string DisplayName, string Description)
{
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
