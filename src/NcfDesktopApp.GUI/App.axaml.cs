/*----------------------------------------------------------------
    Copyright (C) 2026 Senparc

    文件名：App.axaml.cs
    文件功能描述：配置 Avalonia 桌面应用、统一工作区主窗口和共享服务生命周期

    创建标识：Senparc - 20260802

    修改标识：Senparc - 20260806
    修改描述：v0.8.0 将多个工作台窗口整合为单主窗口侧栏标签

----------------------------------------------------------------*/

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core;
using Avalonia.Data.Core.Plugins;
using Avalonia.Markup.Xaml;
using AvaloniaWebView;
using NcfDesktopApp.GUI.Models;
using NcfDesktopApp.GUI.Services;
using NcfDesktopApp.GUI.ViewModels;
using NcfDesktopApp.GUI.Views;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NcfDesktopApp.GUI;

public partial class App : Application
{
    private readonly Dictionary<WorkspaceTabViewModel, WorkspaceUiContext> _workspaceContexts = new();
    private WorkspaceShellViewModel? _shell;
    private MainWindow? _mainWindow;
    private DesktopRobotGroupWindow? _robotGroupWindow;
    private DesktopRobotLayoutMode _robotLayoutMode = DesktopRobotLayoutMode.FreeFloating;
    private int _nextWorkspaceNumber;
    private int _isShuttingDown;

    public override void RegisterServices()
    {
        base.RegisterServices();
        AvaloniaWebViewBuilder.Initialize(default);
    }

    public override void Initialize()
    {
        CrashDiagnosticService.Register();
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // 宠物和设置窗口仍是独立顶层窗口，因此应用退出必须由主窗口统一协调。
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            desktop.Exit += (_, _) =>
            {
                LocalWakeWordService.DisposeShared();
                LocalVoiceInputService.DisposeShared();
                LocalTextToSpeechService.DisposeShared();
            };

            DisableAvaloniaDataAnnotationValidation();
            _robotLayoutMode = DesktopRobotLayoutModePolicy.Normalize(
                DesktopSettingsStore.Load().DesktopRobotLayoutMode);

            _shell = new WorkspaceShellViewModel();
            _mainWindow = new MainWindow
            {
                DataContext = _shell
            };
            _shell.CreateWorkspaceRequested = () => CreateWorkspace(_mainWindow, select: true);
            _mainWindow.Opened += (_, _) => ActivateWorkspaceResourcesAfterMainWindowOpened();
            _mainWindow.Closed += (_, _) => _ = HandleMainWindowClosedAsync(desktop);

            desktop.MainWindow = _mainWindow;
            CreateWorkspace(_mainWindow, select: true);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void CreateWorkspace(
        MainWindow mainWindow,
        bool select)
    {
        if (_shell == null || Volatile.Read(ref _isShuttingDown) != 0)
        {
            return;
        }

        var viewModel = new MainWindowViewModel();
        var contentView = new WorkspaceContentView
        {
            DataContext = viewModel
        };
        var workspace = new WorkspaceTabViewModel(
            Interlocked.Increment(ref _nextWorkspaceNumber),
            viewModel,
            contentView);
        var robotWindow = new DesktopRobotWindow
        {
            DataContext = viewModel.Robot,
            WorkspaceViewModel = viewModel,
            PlacementOffsetIndex = Math.Max(0, workspace.Number - 1)
        };
        var context = new WorkspaceUiContext(robotWindow);
        _workspaceContexts.Add(workspace, context);

        workspace.CloseRequested = RequestCloseWorkspaceAsync;
        viewModel.ApplyDesktopRobotLayoutModeFromShell(_robotLayoutMode);
        viewModel.DesktopRobotLayoutModeChanged = mode => ApplyDesktopRobotLayoutMode(mode);
        viewModel.CreateWorkspaceWindowRequested = () => CreateWorkspace(mainWindow, select: true);
        viewModel.ShowDesktopRobotRequested = () => ShowDesktopRobot(mainWindow, workspace, context);
        viewModel.ShowWorkspaceSettingsRequested = () => ShowWorkspaceSettings(mainWindow, workspace, context);
        viewModel.ShowTemplateWorkspaceRequested = () => ShowTemplateWorkspace(mainWindow, workspace, context);
        viewModel.TemplateWorkspaceCreationSucceeded = () =>
        {
            context.TemplateWorkspaceWindow?.Close();
            ActivateWorkspace(mainWindow, workspace);
        };

        robotWindow.OpenMainWindowRequested = () => ActivateWorkspace(mainWindow, workspace);
        robotWindow.NeuBellOpenRequested = viewModel.OpenNeuBellInDefaultBrowser;
        robotWindow.VoiceInputRequested = () =>
        {
            ActivateWorkspace(mainWindow, workspace);
            _ = viewModel.ToggleVoiceInputCommand.ExecuteAsync(null);
        };

        _shell.AddWorkspace(workspace, select);

        if (mainWindow.IsVisible)
        {
            UpdateDesktopRobotPresentation();
            viewModel.StartDesktopAppUpdateMonitoring();
        }
    }

    private void ActivateWorkspace(MainWindow mainWindow, WorkspaceTabViewModel workspace)
    {
        if (_shell == null || !_shell.Workspaces.Contains(workspace))
        {
            return;
        }

        _shell.SelectedWorkspace = workspace;
        if (!mainWindow.IsVisible)
        {
            mainWindow.Show();
        }
        mainWindow.WindowState = WindowState.Normal;
        mainWindow.Activate();
    }

    private void ShowDesktopRobot(
        MainWindow mainWindow,
        WorkspaceTabViewModel workspace,
        WorkspaceUiContext context)
    {
        ActivateWorkspace(mainWindow, workspace);
        if (_robotLayoutMode == DesktopRobotLayoutMode.GroupedList)
        {
            var groupWindow = EnsureRobotGroupWindow();
            if (!groupWindow.IsVisible)
            {
                groupWindow.Show();
            }
            groupWindow.PromoteAboveOtherAlwaysOnTopWindows();
            groupWindow.Activate();
            return;
        }

        if (!context.RobotWindow.IsVisible)
        {
            context.RobotWindow.Show();
        }
        context.RobotWindow.Activate();
    }

    private void ApplyDesktopRobotLayoutMode(DesktopRobotLayoutMode mode)
    {
        _robotLayoutMode = DesktopRobotLayoutModePolicy.Normalize(mode);
        foreach (var workspace in _workspaceContexts.Keys)
        {
            workspace.Workspace.ApplyDesktopRobotLayoutModeFromShell(_robotLayoutMode);
        }

        UpdateDesktopRobotPresentation();
    }

    private void UpdateDesktopRobotPresentation()
    {
        if (_mainWindow?.IsVisible != true || _shell == null)
        {
            return;
        }

        if (_robotLayoutMode == DesktopRobotLayoutMode.GroupedList)
        {
            foreach (var context in _workspaceContexts.Values)
            {
                if (context.RobotWindow.IsVisible)
                {
                    context.RobotWindow.Hide();
                }
            }

            if (_shell.Workspaces.Count == 0)
            {
                _robotGroupWindow?.Hide();
                return;
            }

            var groupWindow = EnsureRobotGroupWindow();
            if (!groupWindow.IsVisible)
            {
                groupWindow.Show();
            }
            groupWindow.PromoteAboveOtherAlwaysOnTopWindows();
            return;
        }

        _robotGroupWindow?.Hide();
        foreach (var context in _workspaceContexts.Values)
        {
            if (!context.RobotWindow.IsVisible)
            {
                context.RobotWindow.Show();
            }
        }
    }

    private DesktopRobotGroupWindow EnsureRobotGroupWindow()
    {
        if (_robotGroupWindow != null)
        {
            return _robotGroupWindow;
        }

        _robotGroupWindow = new DesktopRobotGroupWindow
        {
            DataContext = _shell,
            OpenWorkspaceRequested = workspace =>
            {
                if (_mainWindow != null)
                {
                    ActivateWorkspace(_mainWindow, workspace);
                }
            },
            VoiceInputRequested = workspace =>
            {
                if (_mainWindow != null)
                {
                    ActivateWorkspace(_mainWindow, workspace);
                }
                _ = workspace.Workspace.ToggleVoiceInputCommand.ExecuteAsync(null);
            },
            SettingsRequested = workspace =>
            {
                if (_mainWindow != null && _workspaceContexts.TryGetValue(workspace, out var context))
                {
                    ActivateWorkspace(_mainWindow, workspace);
                    ShowWorkspaceSettings(_mainWindow, workspace, context);
                }
            },
            NeuBellOpenRequested = workspace =>
            {
                workspace.Workspace.OpenNeuBellInDefaultBrowser();
            }
        };
        return _robotGroupWindow;
    }

    private static void ShowWorkspaceSettings(
        MainWindow mainWindow,
        WorkspaceTabViewModel workspace,
        WorkspaceUiContext context)
    {
        if (context.WorkspaceSettingsWindow?.IsVisible == true)
        {
            context.WorkspaceSettingsWindow.Activate();
            return;
        }

        var settingsWindow = new WorkspaceSettingsWindow
        {
            DataContext = workspace.Workspace
        };
        context.WorkspaceSettingsWindow = settingsWindow;
        settingsWindow.Closed += (_, _) =>
        {
            if (ReferenceEquals(context.WorkspaceSettingsWindow, settingsWindow))
            {
                context.WorkspaceSettingsWindow = null;
            }
        };
        settingsWindow.Show(mainWindow);
    }

    private static void ShowTemplateWorkspace(
        MainWindow mainWindow,
        WorkspaceTabViewModel workspace,
        WorkspaceUiContext context)
    {
        if (context.TemplateWorkspaceWindow?.IsVisible == true)
        {
            context.TemplateWorkspaceWindow.Activate();
            return;
        }

        var templateWindow = new TemplateWorkspaceWindow
        {
            DataContext = workspace.Workspace
        };
        context.TemplateWorkspaceWindow = templateWindow;
        templateWindow.Closed += (_, _) =>
        {
            if (ReferenceEquals(context.TemplateWorkspaceWindow, templateWindow))
            {
                context.TemplateWorkspaceWindow = null;
            }
        };
        templateWindow.Show(mainWindow);
    }

    private void ActivateWorkspaceResourcesAfterMainWindowOpened()
    {
        UpdateDesktopRobotPresentation();
        foreach (var workspace in _workspaceContexts.Keys.ToArray())
        {
            workspace.Workspace.StartDesktopAppUpdateMonitoring();
        }
    }

    private async Task RequestCloseWorkspaceAsync(WorkspaceTabViewModel workspace)
    {
        if (_shell == null ||
            !_workspaceContexts.TryGetValue(workspace, out var context) ||
            context.IsClosing ||
            Volatile.Read(ref _isShuttingDown) != 0)
        {
            return;
        }

        context.IsClosing = true;
        try
        {
            if (workspace.Workspace.IsNcfRunning)
            {
                var confirmed = await workspace.Workspace
                    .ConfirmCloseAsync(
                        $"关闭 {workspace.Title}",
                        $"{workspace.Title} 中的 NCF 正在运行。\n关闭此标签将停止对应 NCF 进程和宠物，其他工作区不受影响。\n是否继续？")
                    .ConfigureAwait(true);
                if (!confirmed)
                {
                    return;
                }

                await workspace.Workspace.StopForWorkspaceCloseAsync().ConfigureAwait(true);
            }

            _shell.RemoveWorkspace(workspace);
            await CleanupWorkspaceAsync(workspace, context).ConfigureAwait(true);
            if (_shell.Workspaces.Count == 0)
            {
                _robotGroupWindow?.Hide();
            }
        }
        catch (Exception ex)
        {
            CrashDiagnosticService.ReportHandledException($"关闭工作区 {workspace.Title}", ex);
        }
        finally
        {
            context.IsClosing = false;
        }
    }

    private async Task HandleMainWindowClosedAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        if (Interlocked.Exchange(ref _isShuttingDown, 1) != 0)
        {
            return;
        }

        try
        {
            foreach (var pair in _workspaceContexts.ToArray())
            {
                _shell?.RemoveWorkspace(pair.Key);
                try
                {
                    await CleanupWorkspaceAsync(pair.Key, pair.Value).ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    CrashDiagnosticService.ReportHandledException($"退出时清理工作区 {pair.Key.Title}", ex);
                }
            }
        }
        finally
        {
            TryCloseWindow(_robotGroupWindow, "关闭统一宠物列表窗口");
            _robotGroupWindow = null;
            desktop.Shutdown();
        }
    }

    private async Task CleanupWorkspaceAsync(
        WorkspaceTabViewModel workspace,
        WorkspaceUiContext context)
    {
        _workspaceContexts.Remove(workspace);

        TryCloseWindow(context.WorkspaceSettingsWindow, "关闭工作区设置窗口");
        TryCloseWindow(context.TemplateWorkspaceWindow, "关闭模板工作区窗口");
        TryCloseWindow(context.RobotWindow, "关闭当前工作区宠物窗口");

        await workspace.Workspace.DisposeWorkspaceResourcesAsync().ConfigureAwait(true);

        workspace.Dispose();
    }

    private static void TryCloseWindow(Window? window, string operation)
    {
        if (window == null)
        {
            return;
        }

        try
        {
            window.Close();
        }
        catch (Exception ex)
        {
            CrashDiagnosticService.ReportHandledException(operation, ex);
        }
    }

    private static void DisableAvaloniaDataAnnotationValidation()
    {
        var pluginsToRemove = BindingPlugins.DataValidators
            .OfType<DataAnnotationsValidationPlugin>()
            .ToArray();
        foreach (var plugin in pluginsToRemove)
        {
            BindingPlugins.DataValidators.Remove(plugin);
        }
    }

    private sealed class WorkspaceUiContext(DesktopRobotWindow robotWindow)
    {
        public DesktopRobotWindow RobotWindow { get; } = robotWindow;

        public WorkspaceSettingsWindow? WorkspaceSettingsWindow { get; set; }

        public TemplateWorkspaceWindow? TemplateWorkspaceWindow { get; set; }

        public bool IsClosing { get; set; }
    }
}
