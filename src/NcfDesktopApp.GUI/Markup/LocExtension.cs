/*----------------------------------------------------------------
    Copyright (C) 2026 Senparc

    文件名：LocExtension.cs
    文件功能描述：XAML 本地化标记扩展，支持语言切换后刷新绑定

    创建标识：Senparc - 20260808

    修改标识：Senparc - 20260812
    修改描述：v0.10.0 完善桌面端唤醒词会话激活与中英文提示

----------------------------------------------------------------*/

using System;
using Avalonia.Data;
using Avalonia.Markup.Xaml;
using NcfDesktopApp.GUI.Services;

namespace NcfDesktopApp.GUI.Markup;

/// <summary>
/// 用法：Text="{loc:Loc Main.NewWorkspace}"
/// </summary>
public sealed class LocExtension : MarkupExtension
{
    public LocExtension()
    {
    }

    public LocExtension(string key)
    {
        Key = key;
    }

    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        return new Binding($"[{Key}]")
        {
            Source = LocalizationService.Instance,
            Mode = BindingMode.OneWay
        };
    }
}
