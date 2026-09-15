/*----------------------------------------------------------------
    Copyright (C) 2026 Senparc

    文件名：WakeWordModelOption.cs
    文件功能描述：本地唤醒词模型选项

    创建标识：Senparc - 20260828

    修改标识：Senparc - 20260828
    修改描述：v0.12.0 定义中文与中英双语唤醒词模型选项

----------------------------------------------------------------*/

using NcfDesktopApp.GUI.Services;

namespace NcfDesktopApp.GUI.Models;

public enum WakeWordModelLanguage
{
    Chinese,
    ChineseEnglish
}

public sealed record WakeWordModelOption(
    string Id,
    WakeWordModelLanguage Language,
    string ModelDirectoryName,
    string ArchiveFileName,
    string DownloadUrl,
    long ApproximateDownloadBytes,
    string ArchiveSha256,
    string EncoderFileName,
    string DecoderFileName,
    string JoinerFileName,
    string TokensFileName,
    string? EnglishPhoneFileName)
{
    public string DisplayName => Language == WakeWordModelLanguage.ChineseEnglish
        ? LocalizationService.T("Settings.WakeModelChineseEnglish")
        : LocalizationService.T("Settings.WakeModelChinese");

    public string Description => Language == WakeWordModelLanguage.ChineseEnglish
        ? LocalizationService.T("Settings.WakeModelChineseEnglishHint")
        : LocalizationService.T("Settings.WakeModelChineseHint");

    public bool SupportsEnglish => Language == WakeWordModelLanguage.ChineseEnglish;
}
