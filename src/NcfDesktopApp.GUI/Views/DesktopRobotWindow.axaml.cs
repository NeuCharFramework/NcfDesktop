/*----------------------------------------------------------------
    Copyright (C) 2026 Senparc

    文件名：DesktopRobotWindow.axaml.cs
    文件功能描述：桌面机器人窗口定位、拖动和主窗口唤起

    创建标识：Senparc - 20260725

    修改标识：Senparc - 20260804
    修改描述：v0.6.0 增加桌面机器人缩放、位置恢复与多屏约束

    修改标识：Senparc - 20260804
    修改描述：v0.7.0 在原生窗口释放前保存位置，避免关闭工作台时访问已释放窗口

----------------------------------------------------------------*/

using System;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using NcfDesktopApp.GUI.Models;
using NcfDesktopApp.GUI.Services;
using NcfDesktopApp.GUI.ViewModels;

namespace NcfDesktopApp.GUI.Views;

public partial class DesktopRobotWindow : Window
{
    private const double MascotSurfaceSize = 112;
    private const double MascotCircleRight = 88;
    private const double CardWidth = 302;
    private const double CardHeight = 138;
    private const double CardOverlap = 46;
    private const double WindowPadding = 16;
    private const double CompactStatusMaxWidth = 110;
    private const double WheelScaleStep = .1;

    private readonly DispatcherTimer _globalPointerTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(50)
    };
    private readonly DispatcherTimer _placementSaveTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(500)
    };
    private MainWindowViewModel? _workspaceViewModel;
    private DesktopRobotViewModel? _subscribedRobot;
    private bool _isOpened;
    private bool _isApplyingScale;
    private bool _isCardExpanded;
    private double _currentScale = DesktopRobotPlacementPolicy.MinimumScale;

    public DesktopRobotWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        _globalPointerTimer.Tick += (_, _) => UpdateGlobalGaze();
        _placementSaveTimer.Tick += (_, _) =>
        {
            _placementSaveTimer.Stop();
            SavePlacement();
        };
        Opened += (_, _) =>
        {
            _isOpened = true;
            RestorePlacementOrUseDefault();
            Robot?.ResetGaze();
            _globalPointerTimer.Start();
        };
        Closing += OnWindowClosing;
        Closed += OnWindowClosed;
    }

    public Action? OpenMainWindowRequested { get; set; }

    public Action? VoiceInputRequested { get; set; }

    public MainWindowViewModel? WorkspaceViewModel
    {
        get => _workspaceViewModel;
        set
        {
            if (ReferenceEquals(_workspaceViewModel, value))
            {
                return;
            }

            if (_workspaceViewModel != null)
            {
                _workspaceViewModel.PropertyChanged -= WorkspaceViewModel_OnPropertyChanged;
            }

            _workspaceViewModel = value;
            if (_workspaceViewModel != null)
            {
                _workspaceViewModel.PropertyChanged += WorkspaceViewModel_OnPropertyChanged;
            }
        }
    }

    private void RestorePlacementOrUseDefault()
    {
        var requestedScale = DesktopRobotPlacementPolicy.NormalizeScale(
            WorkspaceViewModel?.DesktopRobotScale ?? DesktopRobotPlacementPolicy.MinimumScale,
            WorkspaceViewModel?.DesktopRobotMaximumScale ?? DesktopRobotPlacementPolicy.DefaultMaximumScale);
        var requestedPosition = WorkspaceViewModel is
            {
                DesktopRobotPositionX: int x,
                DesktopRobotPositionY: int y
            }
            ? new PixelPoint(x, y)
            : (PixelPoint?)null;

        if (requestedPosition.HasValue)
        {
            foreach (var screen in Screens.All)
            {
                var size = GetWindowPixelSize(screen, requestedScale);
                if (!DesktopRobotPlacementPolicy.IsFullyVisible(
                        requestedPosition.Value,
                        size,
                        screen.WorkingArea))
                {
                    continue;
                }

                ApplyScale(requestedScale, screen, preserveScreenAnchor: false);
                Position = requestedPosition.Value;
                return;
            }
        }

        var defaultScreen = Screens.ScreenFromWindow(this) ?? Screens.Primary ?? Screens.All.FirstOrDefault();
        ApplyScale(requestedScale, defaultScreen, preserveScreenAnchor: false);
        PositionNearWorkingAreaCorner(defaultScreen);
    }

    private void PositionNearWorkingAreaCorner(Screen? screen = null)
    {
        screen ??= Screens.ScreenFromWindow(this) ?? Screens.Primary ?? Screens.All.FirstOrDefault();
        if (screen == null)
        {
            return;
        }

        Position = DesktopRobotPlacementPolicy.GetDefaultPosition(
            GetWindowPixelSize(screen, _currentScale),
            screen.WorkingArea);
    }

    private void ApplyScale(double requestedScale, Screen? preferredScreen = null, bool preserveScreenAnchor = true)
    {
        if (_isApplyingScale)
        {
            return;
        }

        _isApplyingScale = true;
        try
        {
            var screen = preferredScreen ?? Screens.ScreenFromWindow(this) ?? Screens.Primary ?? Screens.All.FirstOrDefault();
            var normalizedScale = DesktopRobotPlacementPolicy.NormalizeScale(
                requestedScale,
                WorkspaceViewModel?.DesktopRobotMaximumScale ?? DesktopRobotPlacementPolicy.DefaultMaximumScale);
            if (screen != null)
            {
                normalizedScale = Math.Min(
                    normalizedScale,
                    DesktopRobotPlacementPolicy.GetMaximumScaleThatFits(
                        screen.WorkingArea,
                        screen.Scaling,
                        MascotSurfaceSize,
                        MascotCircleRight,
                        CardWidth - CardOverlap,
                        MascotSurfaceSize,
                        CardHeight,
                        WindowPadding));
            }

            var oldSize = screen == null ? default : GetWindowPixelSize(screen, _currentScale);
            var oldRightGap = screen == null ? 0 : screen.WorkingArea.Right - (Position.X + oldSize.Width);
            var oldBottomGap = screen == null ? 0 : screen.WorkingArea.Bottom - (Position.Y + oldSize.Height);

            _currentScale = normalizedScale;
            var layout = CalculateLayout(normalizedScale, _isCardExpanded, GetCompactStatusWidth());
            Width = layout.WindowWidth;
            Height = layout.WindowHeight;
            RootSurface.Width = layout.RootWidth;
            RootSurface.Height = layout.RootHeight;
            MascotSurface.RenderTransform = new ScaleTransform(normalizedScale, normalizedScale);
            Canvas.SetLeft(MascotSurface, 0);
            Canvas.SetTop(MascotSurface, layout.MascotTop);
            Canvas.SetLeft(ExpandedCard, layout.CardLeft);
            Canvas.SetTop(ExpandedCard, layout.CardTop);
            Canvas.SetLeft(CompactStatus, layout.StatusLeft);
            Canvas.SetTop(CompactStatus, layout.StatusTop);
            Canvas.SetLeft(HideRobotButton, layout.CardLeft + CardWidth - 34);
            Canvas.SetTop(HideRobotButton, layout.CardTop + 10);
            Canvas.SetLeft(HoverBridge, layout.BridgeLeft);
            Canvas.SetTop(HoverBridge, layout.BridgeTop);
            if (WorkspaceViewModel != null &&
                Math.Abs(WorkspaceViewModel.DesktopRobotScale - normalizedScale) > .001)
            {
                WorkspaceViewModel.UpdateDesktopRobotScaleFromWindow(normalizedScale);
                if (_isOpened)
                {
                    _placementSaveTimer.Stop();
                    _placementSaveTimer.Start();
                }
            }

            if (!_isOpened || screen == null || !preserveScreenAnchor)
            {
                return;
            }

            var newSize = GetWindowPixelSize(screen, normalizedScale);
            var anchoredPosition = new PixelPoint(
                screen.WorkingArea.Right - oldRightGap - newSize.Width,
                screen.WorkingArea.Bottom - oldBottomGap - newSize.Height);
            Position = DesktopRobotPlacementPolicy.IsFullyVisible(
                anchoredPosition,
                newSize,
                screen.WorkingArea)
                ? anchoredPosition
                : DesktopRobotPlacementPolicy.GetDefaultPosition(newSize, screen.WorkingArea);
        }
        finally
        {
            _isApplyingScale = false;
        }
    }

    private static PixelSize GetWindowPixelSize(Screen screen, double scale)
    {
        var scaling = screen.Scaling > 0 ? screen.Scaling : 1;
        // 位置恢复和屏幕边界始终按完整信息卡计算，确保收起后再展开不会越出屏幕。
        var layout = CalculateLayout(scale, isCardExpanded: true, compactStatusWidth: 0);
        return new PixelSize(
            (int)Math.Ceiling(layout.WindowWidth * scaling),
            (int)Math.Ceiling(layout.WindowHeight * scaling));
    }

    internal static DesktopRobotLayout CalculateLayout(
        double scale,
        bool isCardExpanded,
        double compactStatusWidth)
    {
        var mascotSize = MascotSurfaceSize * scale;
        var cardLeft = MascotCircleRight * scale - CardOverlap;
        var circleRight = MascotCircleRight * scale;
        var statusLeft = circleRight + 6;
        var safeStatusWidth = double.IsFinite(compactStatusWidth)
            ? Math.Max(0, compactStatusWidth)
            : 0;
        var expandedRootWidth = Math.Max(mascotSize, cardLeft + CardWidth);
        var collapsedRootWidth = Math.Max(mascotSize, statusLeft + safeStatusWidth);
        var rootWidth = isCardExpanded ? expandedRootWidth : collapsedRootWidth;
        var rootHeight = Math.Max(mascotSize, CardHeight);
        var mascotTop = (rootHeight - mascotSize) / 2;
        var cardTop = (rootHeight - CardHeight) / 2;

        return new DesktopRobotLayout(
            rootWidth,
            rootHeight,
            rootWidth + WindowPadding,
            rootHeight + WindowPadding,
            mascotTop,
            cardLeft,
            cardTop,
            statusLeft,
            cardTop + 10,
            circleRight - 10,
            cardTop + 4);
    }

    private double GetCompactStatusWidth()
    {
        CompactStatus.Measure(new Size(CompactStatusMaxWidth, double.PositiveInfinity));
        var desiredWidth = CompactStatus.DesiredSize.Width;
        return double.IsFinite(desiredWidth) && desiredWidth > 0
            ? Math.Min(desiredWidth, CompactStatusMaxWidth)
            : CompactStatusMaxWidth;
    }

    private void UpdateWindowExtent()
    {
        var layout = CalculateLayout(_currentScale, _isCardExpanded, GetCompactStatusWidth());
        Width = layout.WindowWidth;
        Height = layout.WindowHeight;
        RootSurface.Width = layout.RootWidth;
        RootSurface.Height = layout.RootHeight;
    }

    private void SetCardExpanded(bool isExpanded)
    {
        if (_isCardExpanded == isExpanded)
        {
            return;
        }

        _isCardExpanded = isExpanded;
        ExpandedCard.IsVisible = isExpanded;
        HideRobotButton.IsVisible = isExpanded;
        HoverBridge.IsVisible = isExpanded;
        UpdateWindowExtent();
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_subscribedRobot != null)
        {
            _subscribedRobot.PropertyChanged -= Robot_OnPropertyChanged;
        }

        _subscribedRobot = Robot;
        if (_subscribedRobot != null)
        {
            _subscribedRobot.PropertyChanged += Robot_OnPropertyChanged;
        }
    }

    private void Robot_OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_isOpened || _isCardExpanded || e.PropertyName != nameof(DesktopRobotViewModel.StateText))
        {
            return;
        }

        // 等绑定文本完成布局后再缩放原生窗口，避免新状态被旧宽度裁切。
        Dispatcher.UIThread.Post(() =>
        {
            if (_isOpened && !_isCardExpanded)
            {
                UpdateWindowExtent();
            }
        }, DispatcherPriority.Background);
    }

    private void WorkspaceViewModel_OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_isOpened || WorkspaceViewModel == null)
        {
            return;
        }

        if (e.PropertyName is nameof(MainWindowViewModel.DesktopRobotScale) or
            nameof(MainWindowViewModel.DesktopRobotMaximumScale))
        {
            ApplyScale(WorkspaceViewModel.DesktopRobotScale);
        }
        else if (e.PropertyName == nameof(MainWindowViewModel.DesktopRobotPositionY))
        {
            RestorePlacementOrUseDefault();
        }
    }

    private void SavePlacement()
    {
        if (_isOpened && WorkspaceViewModel != null)
        {
            WorkspaceViewModel.SaveDesktopRobotPlacement(Position, _currentScale);
        }
    }

    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (!_isOpened)
        {
            return;
        }

        // Closing 发生在原生窗口实现释放之前。先保存仍然有效的位置，再把窗口标记为
        // 非活动并解除属性订阅，避免保存位置触发 RestorePlacementOrUseDefault，进而在
        // Closed 阶段通过 Screens.ScreenFromWindow 访问已释放的平台窗口。
        var position = Position;
        var workspaceViewModel = _workspaceViewModel;

        _isOpened = false;
        StopWindowTracking(workspaceViewModel);

        if (workspaceViewModel == null)
        {
            return;
        }

        try
        {
            workspaceViewModel.SaveDesktopRobotPlacement(position, _currentScale);
        }
        catch (Exception ex)
        {
            CrashDiagnosticService.ReportHandledException("保存桌面机器人关闭位置", ex);
        }
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        _isOpened = false;
        StopWindowTracking(_workspaceViewModel);
    }

    private void StopWindowTracking(MainWindowViewModel? workspaceViewModel)
    {
        _globalPointerTimer.Stop();
        _placementSaveTimer.Stop();
        if (workspaceViewModel != null)
        {
            workspaceViewModel.PropertyChanged -= WorkspaceViewModel_OnPropertyChanged;
        }

        if (_subscribedRobot != null)
        {
            _subscribedRobot.PropertyChanged -= Robot_OnPropertyChanged;
            _subscribedRobot = null;
        }
    }

    private void RootBorder_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            PromoteAboveOtherAlwaysOnTopWindows();
            Robot?.ReactToPointer();
            BeginMoveDrag(e);
        }
    }

    private void RevealSurface_OnPointerEntered(object? sender, PointerEventArgs e)
    {
        SetCardExpanded(true);
        PromoteAboveOtherAlwaysOnTopWindows();
    }

    private void PromoteAboveOtherAlwaysOnTopWindows()
    {
        if (!IsVisible)
        {
            return;
        }

        // 其他应用也可能创建 Topmost 桌面宠物。重新加入系统 Topmost 层可把当前展开的
        // 信息卡提升到同级置顶窗口之上；不调用 Activate()，避免仅悬停就抢走键盘焦点。
        Topmost = false;
        Topmost = true;
    }

    private void RootSurface_OnPointerExited(object? sender, PointerEventArgs e)
    {
        // PointerExited 可能在子元素之间切换时触发；延迟到本轮输入结束后再确认。
        Dispatcher.UIThread.Post(() =>
        {
            if (!RootSurface.IsPointerOver)
            {
                SetCardExpanded(false);
                Robot?.ResetGaze();
            }
        }, DispatcherPriority.Input);
    }

    private void MascotSurface_OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        e.Handled = true;
        Robot?.ReactToPointer();
        VoiceInputRequested?.Invoke();
    }

    private void MascotSurface_OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (WorkspaceViewModel?.DesktopRobotWheelZoomEnabled != true || Math.Abs(e.Delta.Y) < .001)
        {
            return;
        }

        var nextScale = _currentScale + Math.Sign(e.Delta.Y) * WheelScaleStep;
        var normalizedScale = DesktopRobotPlacementPolicy.NormalizeScale(
            nextScale,
            WorkspaceViewModel.DesktopRobotMaximumScale);
        if (Math.Abs(normalizedScale - _currentScale) < .001)
        {
            e.Handled = true;
            return;
        }

        WorkspaceViewModel.UpdateDesktopRobotScaleFromWindow(normalizedScale);
        _placementSaveTimer.Stop();
        _placementSaveTimer.Start();
        e.Handled = true;
    }

    private void RootBorder_OnPointerMoved(object? sender, PointerEventArgs e)
    {
        var point = e.GetPosition(this);
        var mascotCenter = MascotView.TranslatePoint(
            new Point(MascotView.Bounds.Width / 2, MascotView.Bounds.Height / 2), this);
        if (!mascotCenter.HasValue)
        {
            return;
        }

        var horizontalRange = Math.Max(1, Bounds.Width * .65);
        var verticalRange = Math.Max(1, Bounds.Height * .65);
        Robot?.UpdateGaze(
            (point.X - mascotCenter.Value.X) / horizontalRange,
            (point.Y - mascotCenter.Value.Y) / verticalRange);
    }

    private void RootBorder_OnPointerExited(object? sender, PointerEventArgs e)
    {
        Robot?.ResetGaze();
    }

    private void UpdateGlobalGaze()
    {
        if (!IsVisible || !GlobalPointerTracker.TryGetScreenPosition(out var pointerPosition))
        {
            return;
        }

        var mascotCenter = MascotView.TranslatePoint(
            new Point(MascotView.Bounds.Width / 2, MascotView.Bounds.Height / 2), this);
        if (!mascotCenter.HasValue)
        {
            return;
        }

        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        var scaling = screen?.Scaling > 0 ? screen.Scaling : 1;
        var mascotCenterOnScreen = new PixelPoint(
            Position.X + (int)Math.Round(mascotCenter.Value.X * scaling),
            Position.Y + (int)Math.Round(mascotCenter.Value.Y * scaling));
        var horizontalRange = Math.Max(1, Bounds.Width * scaling * .65);
        var verticalRange = Math.Max(1, Bounds.Height * scaling * .65);

        Robot?.UpdateGaze(
            (pointerPosition.X - mascotCenterOnScreen.X) / horizontalRange,
            (pointerPosition.Y - mascotCenterOnScreen.Y) / verticalRange);
    }

    private void HideButton_OnClick(object? sender, RoutedEventArgs e)
    {
        Hide();
    }

    private void OpenMainButton_OnClick(object? sender, RoutedEventArgs e)
    {
        OpenMainWindowRequested?.Invoke();
    }

    private void MascotAutoMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        Robot?.UseAutomaticMascot();
    }

    private void VoiceInputButton_OnClick(object? sender, RoutedEventArgs e)
    {
        Robot?.ReactToPointer();
        VoiceInputRequested?.Invoke();
    }

    private void VoiceInputMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        Robot?.ReactToPointer();
        VoiceInputRequested?.Invoke();
    }

    private void MascotMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string tag } &&
            Enum.TryParse<NcfMascotKind>(tag, ignoreCase: true, out var mascot))
        {
            Robot?.UseMascotOverride(mascot);
        }
    }

    private DesktopRobotViewModel? Robot => DataContext as DesktopRobotViewModel;

    internal sealed record DesktopRobotLayout(
        double RootWidth,
        double RootHeight,
        double WindowWidth,
        double WindowHeight,
        double MascotTop,
        double CardLeft,
        double CardTop,
        double StatusLeft,
        double StatusTop,
        double BridgeLeft,
        double BridgeTop);
}
