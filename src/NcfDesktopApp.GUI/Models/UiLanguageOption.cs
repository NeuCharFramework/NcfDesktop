/*----------------------------------------------------------------
    Copyright (C) 2026 Senparc

    文件名：UiLanguageOption.cs
    文件功能描述：界面语言下拉选项

    创建标识：Senparc - 20260808

    修改标识：Senparc - 20260812
    修改描述：v0.10.0 完善桌面端唤醒词会话激活与中英文提示

----------------------------------------------------------------*/

using NcfDesktopApp.GUI.Services;

namespace NcfDesktopApp.GUI.Models;

public sealed class UiLanguageOption
{
    public UiLanguageOption(string code, string displayName)
    {
        Code = code;
        DisplayName = displayName;
    }

    public string Code { get; }

    public string DisplayName { get; }

    public override string ToString() => DisplayName;

    public static UiLanguageOption[] CreateAll() =>
    [
        new(LocalizationService.Chinese, "中文"),
        new(LocalizationService.English, "English")
    ];
}
