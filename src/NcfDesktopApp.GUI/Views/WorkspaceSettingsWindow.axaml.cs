using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
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
            string.Equals(viewModel.DesktopAppLatestVersion, "尚未检查", StringComparison.Ordinal))
        {
            await viewModel.CheckDesktopAppUpdateCommand.ExecuteAsync(null);
        }
    }

    private void CloseButton_OnClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
