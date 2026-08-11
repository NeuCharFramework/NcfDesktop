/*----------------------------------------------------------------
    Copyright (C) 2026 Senparc

    文件名：WorkspaceSettingsWindow.axaml.cs
    文件功能描述：显示工作区设置窗口并触发桌面应用更新检查

    创建标识：Senparc - 20260802

    修改标识：Senparc - 20260812
    修改描述：v0.10.0 完善桌面端唤醒词会话激活与中英文提示

----------------------------------------------------------------*/

using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using NcfDesktopApp.GUI.Services;
using NcfDesktopApp.GUI.ViewModels;

namespace NcfDesktopApp.GUI.Views;

public partial class WorkspaceSettingsWindow : Window
{
    public WorkspaceSettingsWindow()
    {
        InitializeComponent();
    }

    private async void WorkspaceSettingsWindow_OnOpened(object? sender, EventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel &&
            (string.Equals(viewModel.DesktopAppLatestVersion, LocalizationService.T("Common.NotChecked"), StringComparison.Ordinal) ||
             string.Equals(viewModel.DesktopAppLatestVersion, "尚未检查", StringComparison.Ordinal) ||
             string.Equals(viewModel.DesktopAppLatestVersion, "Not checked yet", StringComparison.Ordinal)))
        {
            await viewModel.CheckDesktopAppUpdateCommand.ExecuteAsync(null);
        }
    }

    private void CloseButton_OnClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
