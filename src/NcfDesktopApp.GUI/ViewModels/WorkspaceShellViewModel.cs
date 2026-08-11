/*----------------------------------------------------------------
    Copyright (C) 2026 Senparc

    文件名：WorkspaceShellViewModel.cs
    文件功能描述：管理单一主窗口中的多个 NCF 工作区标签

    创建标识：Senparc - 20260806

    修改标识：Senparc - 20260812
    修改描述：v0.10.0 完善桌面端唤醒词会话激活与中英文提示

----------------------------------------------------------------*/

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NcfDesktopApp.GUI.Models;
using NcfDesktopApp.GUI.Services;
using NcfDesktopApp.GUI.Views;
using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Threading.Tasks;

namespace NcfDesktopApp.GUI.ViewModels;

public partial class WorkspaceShellViewModel : ViewModelBase
{
    [ObservableProperty]
    private WorkspaceTabViewModel? _selectedWorkspace;

    [ObservableProperty]
    private bool _isSidebarExpanded = true;

    public ObservableCollection<WorkspaceTabViewModel> Workspaces { get; } = new();

    public Action? CreateWorkspaceRequested { get; set; }

    public bool HasWorkspaces => Workspaces.Count > 0;

    public bool HasNoWorkspaces => !HasWorkspaces;

    public string WorkspaceCountText => LocalizationService.T("Shell.WorkspaceCount", Workspaces.Count);

    public double SidebarWidth => IsSidebarExpanded ? 244 : 76;

    public string SidebarToggleGlyph => IsSidebarExpanded ? "‹" : "›";

    public string SidebarTitle => IsSidebarExpanded
        ? LocalizationService.T("Shell.SidebarTitle")
        : LocalizationService.T("Shell.SidebarTitleCompact");

    public WorkspaceShellViewModel()
    {
        Workspaces.CollectionChanged += Workspaces_OnCollectionChanged;
        LocalizationService.Instance.LanguageChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(WorkspaceCountText));
            OnPropertyChanged(nameof(SidebarTitle));
        };
    }

    public void AddWorkspace(WorkspaceTabViewModel workspace, bool select = true)
    {
        workspace.IsSidebarExpanded = IsSidebarExpanded;
        Workspaces.Add(workspace);
        if (select)
        {
            SelectedWorkspace = workspace;
        }
    }

    public void RemoveWorkspace(WorkspaceTabViewModel workspace)
    {
        var removedIndex = Workspaces.IndexOf(workspace);
        if (removedIndex < 0)
        {
            return;
        }

        var wasSelected = ReferenceEquals(SelectedWorkspace, workspace);
        Workspaces.RemoveAt(removedIndex);
        if (wasSelected)
        {
            SelectedWorkspace = Workspaces.Count == 0
                ? null
                : Workspaces[WorkspaceTabSelectionPolicy.GetNextSelectedIndex(removedIndex, Workspaces.Count)];
        }
    }

    [RelayCommand]
    private void CreateWorkspace() => CreateWorkspaceRequested?.Invoke();

    [RelayCommand]
    private void ToggleSidebar() => IsSidebarExpanded = !IsSidebarExpanded;

    partial void OnIsSidebarExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(SidebarWidth));
        OnPropertyChanged(nameof(SidebarToggleGlyph));
        OnPropertyChanged(nameof(SidebarTitle));
        foreach (var workspace in Workspaces)
        {
            workspace.IsSidebarExpanded = value;
        }
    }

    private void Workspaces_OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasWorkspaces));
        OnPropertyChanged(nameof(HasNoWorkspaces));
        OnPropertyChanged(nameof(WorkspaceCountText));
    }
}

public partial class WorkspaceTabViewModel : ViewModelBase, IDisposable
{
    private string _fallbackTitle;
    private bool _disposed;

    [ObservableProperty]
    private bool _isSidebarExpanded = true;

    public int Number { get; }

    public MainWindowViewModel Workspace { get; }

    public WorkspaceContentView ContentView { get; }

    public Func<WorkspaceTabViewModel, Task>? CloseRequested { get; set; }

    public string Title => ResolveTitle();

    public string CompactTitle => Number.ToString();

    public string StatusText => Workspace.CurrentStatus;

    public string StatusColor => Workspace.StatusColor;

    public string TargetText => Workspace.TargetKindText;

    public bool IsRunning => Workspace.IsNcfRunning;

    public WorkspaceTabViewModel(
        int number,
        MainWindowViewModel workspace,
        WorkspaceContentView contentView)
    {
        Number = number;
        _fallbackTitle = LocalizationService.T("Shell.WorkspaceFallback", number);
        Workspace = workspace;
        ContentView = contentView;
        Workspace.PropertyChanged += Workspace_OnPropertyChanged;
        LocalizationService.Instance.LanguageChanged += OnUiLanguageChanged;
    }

    private void OnUiLanguageChanged(object? sender, EventArgs e)
    {
        _fallbackTitle = LocalizationService.T("Shell.WorkspaceFallback", Number);
        OnPropertyChanged(nameof(Title));
    }

    [RelayCommand]
    private Task Close() => CloseRequested?.Invoke(this) ?? Task.CompletedTask;

    private void Workspace_OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.ExternalNcfPath)
            or nameof(MainWindowViewModel.RemoteSiteUrl)
            or nameof(MainWindowViewModel.LaunchTargetKind))
        {
            OnPropertyChanged(nameof(Title));
        }

        if (e.PropertyName == nameof(MainWindowViewModel.CurrentStatus))
        {
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(IsRunning));
        }

        if (e.PropertyName == nameof(MainWindowViewModel.StatusColor))
        {
            OnPropertyChanged(nameof(StatusColor));
        }

        if (e.PropertyName == nameof(MainWindowViewModel.TargetKindText))
        {
            OnPropertyChanged(nameof(TargetText));
        }
    }

    private string ResolveTitle()
    {
        if (Workspace.LaunchTargetKind == NcfLaunchTargetKind.RemoteSite &&
            Uri.TryCreate(Workspace.RemoteSiteUrl, UriKind.Absolute, out var remoteUri))
        {
            return remoteUri.IsDefaultPort
                ? remoteUri.Host
                : $"{remoteUri.Host}:{remoteUri.Port}";
        }

        if (Workspace.LaunchTargetKind is NcfLaunchTargetKind.ExternalPublished or NcfLaunchTargetKind.SourceProject)
        {
            var path = Workspace.ExternalNcfPath?.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!string.IsNullOrWhiteSpace(path))
            {
                var name = Path.GetFileName(path);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    return name;
                }
            }
        }

        return _fallbackTitle;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        LocalizationService.Instance.LanguageChanged -= OnUiLanguageChanged;
        Workspace.PropertyChanged -= Workspace_OnPropertyChanged;
        ContentView.Dispose();
    }
}
