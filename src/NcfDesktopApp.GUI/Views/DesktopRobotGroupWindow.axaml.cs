/*----------------------------------------------------------------
    Copyright (C) 2026 Senparc

    文件名：DesktopRobotGroupWindow.axaml.cs
    文件功能描述：统一桌面宠物标签列表的拖动与常用交互

    创建标识：Senparc - 20260807

----------------------------------------------------------------*/

using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using NcfDesktopApp.GUI.Models;
using NcfDesktopApp.GUI.Services;
using NcfDesktopApp.GUI.ViewModels;

namespace NcfDesktopApp.GUI.Views;

public partial class DesktopRobotGroupWindow : Window
{
    private bool _hasInitialPosition;

    public DesktopRobotGroupWindow()
    {
        InitializeComponent();
        Opened += (_, _) =>
        {
            if (!_hasInitialPosition)
            {
                PositionNearWorkingAreaCorner();
                _hasInitialPosition = true;
            }
        };
    }

    public Action<WorkspaceTabViewModel>? OpenWorkspaceRequested { get; set; }

    public Action<WorkspaceTabViewModel>? VoiceInputRequested { get; set; }

    public Action<WorkspaceTabViewModel>? SettingsRequested { get; set; }

    public Action<WorkspaceTabViewModel>? NeuBellOpenRequested { get; set; }

    public void PromoteAboveOtherAlwaysOnTopWindows()
    {
        if (!IsVisible)
        {
            return;
        }

        Topmost = false;
        Topmost = true;
    }

    private void PositionNearWorkingAreaCorner()
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary ?? Screens.All.FirstOrDefault();
        if (screen == null)
        {
            return;
        }

        var scaling = screen.Scaling > 0 ? screen.Scaling : 1;
        var width = (int)Math.Ceiling(Width * scaling);
        var estimatedHeight = (int)Math.Ceiling(Math.Min(MaxHeight, Math.Max(MinHeight, Bounds.Height)) * scaling);
        Position = DesktopRobotPlacementPolicy.GetDefaultPosition(
            new PixelSize(width, estimatedHeight),
            screen.WorkingArea);
    }

    private void Header_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        PromoteAboveOtherAlwaysOnTopWindows();
        BeginMoveDrag(e);
    }

    private void RobotRow_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        ResolveWorkspace(sender)?.Workspace.Robot.ReactToPointer();
        PromoteAboveOtherAlwaysOnTopWindows();
    }

    private void RobotRow_OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (sender is not Control control || ResolveWorkspace(sender) is not { } workspace)
        {
            return;
        }

        var point = e.GetPosition(control);
        workspace.Workspace.Robot.UpdateGaze(
            (point.X - control.Bounds.Width / 2) / Math.Max(1, control.Bounds.Width * .5),
            (point.Y - control.Bounds.Height / 2) / Math.Max(1, control.Bounds.Height * .5));
    }

    private void RobotRow_OnPointerExited(object? sender, PointerEventArgs e) =>
        ResolveWorkspace(sender)?.Workspace.Robot.ResetGaze();

    private void RobotRow_OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (ResolveWorkspace(sender) is not { } workspace)
        {
            return;
        }

        e.Handled = true;
        workspace.Workspace.Robot.ReactToPointer();
        VoiceInputRequested?.Invoke(workspace);
    }

    private void VoiceButton_OnClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        RequestVoiceInput(sender);
    }

    private void VoiceMenuItem_OnClick(object? sender, RoutedEventArgs e) => RequestVoiceInput(sender);

    private void AgentPortalButton_OnClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        ToggleAgentPortal(sender);
    }

    private void AgentPortalMenuItem_OnClick(object? sender, RoutedEventArgs e) => ToggleAgentPortal(sender);

    private static void NeuBellButton_OnPointerPressed(object? sender, PointerPressedEventArgs e) =>
        e.Handled = true;

    private void NeuBellButton_OnClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        RequestOpenNeuBell(sender);
    }

    private void NeuBellMenuItem_OnClick(object? sender, RoutedEventArgs e) => RequestOpenNeuBell(sender);

    private void RequestOpenNeuBell(object? sender)
    {
        if (ResolveWorkspace(sender) is { } workspace)
        {
            NeuBellOpenRequested?.Invoke(workspace);
        }
    }

    private static void ToggleAgentPortal(object? sender) =>
        ResolveWorkspace(sender)?.Workspace.Robot.ToggleAgentPortal();

    private void RequestVoiceInput(object? sender)
    {
        if (ResolveWorkspace(sender) is not { } workspace)
        {
            return;
        }

        workspace.Workspace.Robot.ReactToPointer();
        VoiceInputRequested?.Invoke(workspace);
    }

    private void OpenButton_OnClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        RequestOpenWorkspace(sender);
    }

    private void OpenMenuItem_OnClick(object? sender, RoutedEventArgs e) => RequestOpenWorkspace(sender);

    private void RequestOpenWorkspace(object? sender)
    {
        if (ResolveWorkspace(sender) is { } workspace)
        {
            OpenWorkspaceRequested?.Invoke(workspace);
        }
    }

    private void SettingsMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        if (ResolveWorkspace(sender) is { } workspace)
        {
            SettingsRequested?.Invoke(workspace);
        }
    }

    private void MascotAutoMenuItem_OnClick(object? sender, RoutedEventArgs e) =>
        ResolveWorkspace(sender)?.Workspace.Robot.UseAutomaticMascot();

    private void MascotMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string tag } &&
            ResolveWorkspace(sender) is { } workspace &&
            Enum.TryParse<NcfMascotKind>(tag, ignoreCase: true, out var mascot))
        {
            workspace.Workspace.Robot.UseMascotOverride(mascot);
        }
    }

    private void HideButton_OnClick(object? sender, RoutedEventArgs e) => Hide();

    private static WorkspaceTabViewModel? ResolveWorkspace(object? sender) =>
        (sender as Control)?.DataContext as WorkspaceTabViewModel;
}
