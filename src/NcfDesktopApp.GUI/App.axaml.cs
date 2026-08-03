/*----------------------------------------------------------------
    Copyright (C) 2026 Senparc

    文件名：App.axaml.cs
    文件功能描述：配置 Avalonia 桌面应用、工作台窗口和共享服务生命周期

    创建标识：Senparc - 20260802

    修改标识：Senparc - 20260804
    修改描述：v0.6.0 协调工作台窗口及本地语音服务的启动与释放

----------------------------------------------------------------*/

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core;
using Avalonia.Data.Core.Plugins;
using System.Linq;
using Avalonia.Markup.Xaml;
using NcfDesktopApp.GUI.ViewModels;
using NcfDesktopApp.GUI.Views;
using AvaloniaWebView;
using System.Collections.Generic;
using NcfDesktopApp.GUI.Services;

namespace NcfDesktopApp.GUI;

public partial class App : Application
{
    private readonly List<MainWindow> _workspaceWindows = new();

    public override void RegisterServices()
    {
        base.RegisterServices();
        // Initialize WebView.Avalonia; if only WebView is needed, this is sufficient
        AvaloniaWebViewBuilder.Initialize(default);
        // If in the future BlazorWebView is used, uncomment and configure below
        // using AvaloniaBlazorWebView; AvaloniaBlazorWebViewBuilder.Initialize(default);
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
            // 每个窗口拥有独立的进程/端口/Bridge 会话；最后一个工作台关闭后才退出。
            desktop.ShutdownMode = ShutdownMode.OnLastWindowClose;
            desktop.Exit += (_, _) =>
            {
                LocalWakeWordService.DisposeShared();
                LocalVoiceInputService.DisposeShared();
                LocalTextToSpeechService.DisposeShared();
            };

            // Avoid duplicate validations from both Avalonia and the CommunityToolkit. 
            // More info: https://docs.avaloniaui.net/docs/guides/development-guides/data-validation#manage-validationplugins
            DisableAvaloniaDataAnnotationValidation();
            desktop.MainWindow = CreateWorkspaceWindow(desktop, showImmediately: false);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private MainWindow CreateWorkspaceWindow(
        IClassicDesktopStyleApplicationLifetime desktop,
        bool showImmediately)
    {
        var viewModel = new MainWindowViewModel();
        var workspaceNumber = _workspaceWindows.Count + 1;
        var mainWindow = new MainWindow
        {
            DataContext = viewModel,
            Title = $"NCF Agent Workspace #{workspaceNumber}"
        };
        var robotWindow = new DesktopRobotWindow
        {
            DataContext = viewModel.Robot,
            WorkspaceViewModel = viewModel,
            OpenMainWindowRequested = () =>
            {
                if (!mainWindow.IsVisible)
                {
                    mainWindow.Show();
                }
                mainWindow.WindowState = WindowState.Normal;
                mainWindow.Activate();
            },
            VoiceInputRequested = () =>
            {
                if (!mainWindow.IsVisible)
                {
                    mainWindow.Show();
                }
                mainWindow.WindowState = WindowState.Normal;
                mainWindow.Activate();
                _ = viewModel.ToggleVoiceInputCommand.ExecuteAsync(null);
            }
        };
        WorkspaceSettingsWindow? settingsWindow = null;
        TemplateWorkspaceWindow? templateWorkspaceWindow = null;

        viewModel.CreateWorkspaceWindowRequested = () =>
        {
            var newWindow = CreateWorkspaceWindow(desktop, showImmediately: true);
            newWindow.Activate();
        };
        viewModel.ShowDesktopRobotRequested = () =>
        {
            if (!robotWindow.IsVisible)
            {
                robotWindow.Show();
            }
            robotWindow.Activate();
        };
        viewModel.ShowWorkspaceSettingsRequested = () =>
        {
            if (settingsWindow?.IsVisible == true)
            {
                settingsWindow.Activate();
                return;
            }

            settingsWindow = new WorkspaceSettingsWindow
            {
                DataContext = viewModel
            };
            settingsWindow.Closed += (_, _) => settingsWindow = null;
            settingsWindow.Show(mainWindow);
        };
        viewModel.ShowTemplateWorkspaceRequested = () =>
        {
            if (templateWorkspaceWindow?.IsVisible == true)
            {
                templateWorkspaceWindow.Activate();
                return;
            }

            templateWorkspaceWindow = new TemplateWorkspaceWindow
            {
                DataContext = viewModel
            };
            templateWorkspaceWindow.Closed += (_, _) => templateWorkspaceWindow = null;
            templateWorkspaceWindow.Show(mainWindow);
        };
        mainWindow.Opened += (_, _) => robotWindow.Show();
        mainWindow.Closed += async (_, _) =>
        {
            // 即使用户先隐藏了宠物，也要关闭其窗口，让 Closed 统一保存最后位置与大小。
            robotWindow.Close();
            await viewModel.CancelVoiceInputForShutdownAsync();
            _workspaceWindows.Remove(mainWindow);
        };
        _workspaceWindows.Add(mainWindow);

        if (showImmediately)
        {
            mainWindow.Show();
        }

        return mainWindow;
    }

    private void DisableAvaloniaDataAnnotationValidation()
    {
        // Get an array of plugins to remove
        var dataValidationPluginsToRemove =
            BindingPlugins.DataValidators.OfType<DataAnnotationsValidationPlugin>().ToArray();

        // remove each entry found
        foreach (var plugin in dataValidationPluginsToRemove)
        {
            BindingPlugins.DataValidators.Remove(plugin);
        }
    }
}
