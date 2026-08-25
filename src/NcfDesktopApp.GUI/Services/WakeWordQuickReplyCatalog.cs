/*----------------------------------------------------------------
    Copyright (C) 2026 Senparc

    文件名：WakeWordQuickReplyCatalog.cs
    文件功能描述：唤醒词两阶段快速语音确认词

    创建标识：Senparc - 20260822

----------------------------------------------------------------*/

using System;

namespace NcfDesktopApp.GUI.Services;

internal static class WakeWordQuickReplyCatalog
{
    private static readonly string[] ChineseListening =
    [
        "我在",
        "在的",
        "请说",
        "收到，我在"
    ];

    private static readonly string[] ChineseReceived =
    [
        "收到",
        "好的",
        "明白",
        "了解"
    ];

    private static readonly string[] EnglishListening =
    [
        "Acknowledged",
        "Roger that",
        "Affirmative",
        "Yes, comrade"
    ];

    private static readonly string[] EnglishReceived =
    [
        "Got it",
        "Acknowledged",
        "Roger that",
        "Affirmative"
    ];

    public static string Pick(string stage, bool english)
    {
        var options = english
            ? string.Equals(stage, "received", StringComparison.OrdinalIgnoreCase)
                ? EnglishReceived
                : EnglishListening
            : string.Equals(stage, "received", StringComparison.OrdinalIgnoreCase)
                ? ChineseReceived
                : ChineseListening;
        return options[Random.Shared.Next(options.Length)];
    }
}
