using Avalonia.Controls;
using System;

namespace NcfDesktopApp.GUI.Views;

public partial class WorkspaceContentView : UserControl, IDisposable
{
    private bool _disposed;

    public WorkspaceContentView()
    {
        InitializeComponent();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        WorkspaceBrowserView.Dispose();
        DataContext = null;
    }
}
