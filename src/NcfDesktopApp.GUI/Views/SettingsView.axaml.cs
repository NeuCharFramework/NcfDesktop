using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using NcfDesktopApp.GUI.Models;
using NcfDesktopApp.GUI.Services;
using NcfDesktopApp.GUI.ViewModels;
using System;
using System.Collections.Specialized;

namespace NcfDesktopApp.GUI.Views;

public partial class SettingsView : UserControl
{
    private bool _isUserScrolling = false;
    private bool _isChatUserScrolling;
    private bool _isChatScrollScheduled;
    private INotifyCollectionChanged? _chatMessages;
    
    public SettingsView()
    {
        InitializeComponent();
    }

    private void SettingsView_OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_chatMessages != null)
        {
            _chatMessages.CollectionChanged -= ChatMessages_OnCollectionChanged;
            _chatMessages = null;
        }

        if (DataContext is MainWindowViewModel viewModel)
        {
            _chatMessages = viewModel.AdminChatMessages;
            _chatMessages.CollectionChanged += ChatMessages_OnCollectionChanged;
            ScheduleChatScrollToEnd();
        }
    }

    private void ChatMessages_OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!_isChatUserScrolling)
        {
            ScheduleChatScrollToEnd();
        }
    }

    private void ChatScrollViewer_OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer scrollViewer)
        {
            return;
        }

        var layoutChanged = e.ExtentDelta.Y != 0 || e.ViewportDelta.Y != 0;
        var isNearBottom = ScrollPositionPolicy.IsNearBottom(
            scrollViewer.Extent.Height,
            scrollViewer.Viewport.Height,
            scrollViewer.Offset.Y);

        if (!layoutChanged && e.OffsetDelta.Y != 0)
        {
            _isChatUserScrolling = !isNearBottom;
        }
        else if (isNearBottom)
        {
            _isChatUserScrolling = false;
        }

        if (layoutChanged &&
            !_isChatUserScrolling &&
            ScrollPositionPolicy.WasNearBottom(
                scrollViewer.Extent.Height,
                scrollViewer.Viewport.Height,
                scrollViewer.Offset.Y,
                e.ExtentDelta.Y,
                e.ViewportDelta.Y,
                e.OffsetDelta.Y))
        {
            ScheduleChatScrollToEnd();
        }
    }

    private void ScheduleChatScrollToEnd()
    {
        if (_isChatScrollScheduled)
        {
            return;
        }

        _isChatScrollScheduled = true;
        Dispatcher.UIThread.Post(() =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    if (!_isChatUserScrolling)
                    {
                        ChatScrollViewer.ScrollToEnd();
                    }
                }
                finally
                {
                    _isChatScrollScheduled = false;
                }
            }, DispatcherPriority.Render);
        }, DispatcherPriority.Background);
    }

    private void ChatInput_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter ||
            (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) == 0 ||
            DataContext is not MainWindowViewModel viewModel ||
            !viewModel.SendAdminChatMessageCommand.CanExecute(null))
        {
            return;
        }

        e.Handled = true;
        viewModel.SendAdminChatMessageCommand.Execute(null);
    }

    private async void AdminChatSpeechButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: AdminChatMessage message } ||
            DataContext is not MainWindowViewModel viewModel ||
            !viewModel.ToggleAdminChatSpeechCommand.CanExecute(message))
        {
            return;
        }

        try
        {
            // DataTemplate 在登录后才实例化。这里直接从 SettingsView 取 ViewModel，
            // 避免运行时使用 $parent 跨模板解析命令导致平台相关的绑定异常。
            await viewModel.ToggleAdminChatSpeechCommand.ExecuteAsync(message);
        }
        catch (Exception ex)
        {
            viewModel.TtsPlaybackStatusText = $"朗读失败：{ex.Message}";
        }
    }
    
    /// <summary>
    /// 当滚动条位置改变时触发
    /// </summary>
    private void LogScrollViewer_OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer scrollViewer) return;
        
        try
        {
            var layoutChanged = e.ExtentDelta.Y != 0 || e.ViewportDelta.Y != 0;
            var isNearBottom = ScrollPositionPolicy.IsNearBottom(
                scrollViewer.Extent.Height,
                scrollViewer.Viewport.Height,
                scrollViewer.Offset.Y);

            if (!layoutChanged && e.OffsetDelta.Y != 0)
            {
                _isUserScrolling = !isNearBottom;
            }
            else if (isNearBottom)
            {
                _isUserScrolling = false;
            }

            if (layoutChanged &&
                !_isUserScrolling &&
                ScrollPositionPolicy.WasNearBottom(
                    scrollViewer.Extent.Height,
                    scrollViewer.Viewport.Height,
                    scrollViewer.Offset.Y,
                    e.ExtentDelta.Y,
                    e.ViewportDelta.Y,
                    e.OffsetDelta.Y))
            {
                Dispatcher.UIThread.Post(scrollViewer.ScrollToEnd, DispatcherPriority.Render);
            }
        }
        catch
        {
            // 忽略滚动检查错误
        }
    }
    
    /// <summary>
    /// 获取是否应该自动滚动到底部
    /// </summary>
    public bool ShouldAutoScroll => !_isUserScrolling;
}
