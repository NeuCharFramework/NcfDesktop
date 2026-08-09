/*----------------------------------------------------------------
    Copyright (C) 2026 Senparc

    文件名：DesktopRobotWindow.axaml.cs
    文件功能描述：桌面机器人窗口定位、拖动和主窗口唤起

    创建标识：Senparc - 20260725

    修改标识：Senparc - 20260804
    修改描述：v0.6.0 增加桌面机器人缩放、位置恢复与多屏约束

    修改标识：Senparc - 20260804
    修改描述：v0.7.0 在原生窗口释放前保存位置，避免关闭工作台时访问已释放窗口

    修改标识：Senparc - 20260809
    修改描述：Agents 空间门户支持 68% 工作区高度展开、返回宠物和透明命中区域同步

----------------------------------------------------------------*/

using System;
using System.Collections.Generic;
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
    private const double MascotCircleTop = 24;
    private const double MascotCircleRight = 88;
    private const double CardWidth = 342;
    private const double CardHeight = 138;
    private const double CardOverlap = 46;
    private const double WindowPadding = 16;
    private const double WindowInset = WindowPadding / 2;
    private const double CompactStatusMaxWidth = 110;
    private const double CompactStatusHeight = 26;
    private const double CompactStatusCornerRadius = 9;
    private const double CompactStatusHorizontalOverlap = 12;
    private const double CompactStatusVerticalOverlap = 8;
    private const double CardCornerRadius = 18;
    private const double NeuBellBadgeSize = 30;
    private const double NeuBellBadgeTop = 3;
    private const double NeuBellBadgeRight = 3;
    private const double WheelScaleStep = .1;
    private const double AgentPortalScreenHeightRatio = .68;
    private const double PortalReturnButtonWidth = 84;
    private const double PortalReturnButtonHeight = 34;
    private const double PortalReturnButtonBottomInset = 30;

    private readonly DispatcherTimer _globalPointerTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(50)
    };
    private readonly DispatcherTimer _placementSaveTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(500)
    };
    private readonly DispatcherTimer _agentPortalViewportTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(40)
    };
    private MainWindowViewModel? _workspaceViewModel;
    private DesktopRobotViewModel? _subscribedRobot;
    private bool _isOpened;
    private bool _isApplyingScale;
    private bool _isCardExpanded;
    private bool _isAgentPortalViewportActive;
    private bool _hasRestoredInitialPlacement;
    private bool _macOsMousePassthrough;
    private double _currentScale = DesktopRobotPlacementPolicy.MinimumScale;
    private double _agentPortalViewportDiameter = MascotSurfaceSize;
    private double _agentPortalViewportTargetDiameter = MascotSurfaceSize;
    private PixelPoint? _agentPortalAnchor;
    private PixelPoint? _agentPortalOriginalPosition;
    private IReadOnlyList<FloatingWindowHitRegion> _hitRegions = Array.Empty<FloatingWindowHitRegion>();

    public DesktopRobotWindow()
    {
        InitializeComponent();
        FloatingWindowPlatformService.ConfigureTransparentWindow(this);
        DataContextChanged += OnDataContextChanged;
        _globalPointerTimer.Tick += (_, _) => UpdateGlobalGaze();
        _placementSaveTimer.Tick += (_, _) =>
        {
            _placementSaveTimer.Stop();
            SavePlacement();
        };
        _agentPortalViewportTimer.Tick += (_, _) => AdvanceAgentPortalViewport();
        Opened += (_, _) =>
        {
            FloatingWindowPlatformService.ConfigureTransparentWindow(this);
            _isOpened = true;
            RestorePlacementOrUseDefault();
            UpdateNativeHitArea(CalculateLayout(_currentScale, _isCardExpanded, GetCompactStatusWidth()));
            Robot?.ResetGaze();
            _globalPointerTimer.Start();
        };
        Closing += OnWindowClosing;
        Closed += OnWindowClosed;
    }

    public Action? OpenMainWindowRequested { get; set; }

    public Action? VoiceInputRequested { get; set; }

    public Action? NeuBellOpenRequested { get; set; }

    /// <summary>
    /// 多个自由浮动宠物首次显示时的错位序号，避免都覆盖在同一保存坐标上。
    /// </summary>
    public int PlacementOffsetIndex { get; set; }

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
                Position = GetInitialPlacement(
                    requestedPosition.Value,
                    size,
                    screen.WorkingArea);
                _hasRestoredInitialPlacement = true;
                return;
            }
        }

        var defaultScreen = Screens.ScreenFromWindow(this) ?? Screens.Primary ?? Screens.All.FirstOrDefault();
        ApplyScale(requestedScale, defaultScreen, preserveScreenAnchor: false);
        PositionNearWorkingAreaCorner(defaultScreen);
        _hasRestoredInitialPlacement = true;
    }

    private void PositionNearWorkingAreaCorner(Screen? screen = null)
    {
        screen ??= Screens.ScreenFromWindow(this) ?? Screens.Primary ?? Screens.All.FirstOrDefault();
        if (screen == null)
        {
            return;
        }

        var windowSize = GetWindowPixelSize(screen, _currentScale);
        var defaultPosition = DesktopRobotPlacementPolicy.GetDefaultPosition(
            windowSize,
            screen.WorkingArea);
        Position = GetInitialPlacement(defaultPosition, windowSize, screen.WorkingArea);
    }

    private PixelPoint GetInitialPlacement(
        PixelPoint position,
        PixelSize windowSize,
        PixelRect workingArea) =>
        _hasRestoredInitialPlacement
            ? position
            : CalculateInitialFreeFloatingPosition(
                position,
                windowSize,
                workingArea,
                PlacementOffsetIndex);

    internal static PixelPoint CalculateInitialFreeFloatingPosition(
        PixelPoint basePosition,
        PixelSize windowSize,
        PixelRect workingArea,
        int offsetIndex)
    {
        if (offsetIndex <= 0)
        {
            return basePosition;
        }

        const int rowsPerColumn = 5;
        const int verticalStep = 112;
        const int horizontalStep = 190;
        var row = offsetIndex % rowsPerColumn;
        var column = offsetIndex / rowsPerColumn;
        var requestedX = basePosition.X - column * horizontalStep;
        var requestedY = basePosition.Y - row * verticalStep;
        var maximumX = Math.Max(workingArea.X, workingArea.Right - windowSize.Width);
        var maximumY = Math.Max(workingArea.Y, workingArea.Bottom - windowSize.Height);
        return new PixelPoint(
            Math.Clamp(requestedX, workingArea.X, maximumX),
            Math.Clamp(requestedY, workingArea.Y, maximumY));
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
            var oldRightGap = screen == null
                ? 0
                : screen.WorkingArea.Right - (Position.X + oldSize.Width);
            var oldBottomGap = screen == null
                ? 0
                : screen.WorkingArea.Bottom - (Position.Y + oldSize.Height);

            _currentScale = normalizedScale;
            UpdateWindowExtent();
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

            if (!_isOpened || screen == null || !preserveScreenAnchor || _isAgentPortalViewportActive)
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
        var circleTop = MascotCircleTop * scale;
        var statusLeft = Math.Max(0, circleRight - CompactStatusHorizontalOverlap);
        var safeStatusWidth = double.IsFinite(compactStatusWidth)
            ? Math.Max(0, compactStatusWidth)
            : 0;
        var expandedRootWidth = Math.Max(mascotSize, cardLeft + CardWidth);
        var collapsedRootWidth = Math.Max(mascotSize, statusLeft + safeStatusWidth);
        var rootWidth = isCardExpanded ? expandedRootWidth : collapsedRootWidth;
        // 高度始终按展开状态保留；透明空白由原生命中区域（Windows）或鼠标穿透
        // （macOS）释放。这样缩放低于 CardHeight / MascotSurfaceSize 时，展开卡片
        // 不需要移动原生窗口，宠物的屏幕坐标也不会发生跳变。
        var rootHeight = Math.Max(mascotSize, CardHeight);
        var mascotTop = (rootHeight - mascotSize) / 2;
        var cardTop = (rootHeight - CardHeight) / 2;
        var statusTop = Math.Max(
            0,
            mascotTop + circleTop - (CompactStatusHeight - CompactStatusVerticalOverlap));

        return new DesktopRobotLayout(
            rootWidth,
            rootHeight,
            rootWidth + WindowPadding,
            rootHeight + WindowPadding,
            scale,
            mascotSize,
            mascotTop,
            cardLeft,
            cardTop,
            statusLeft,
            statusTop,
            safeStatusWidth,
            circleRight - 10,
            cardTop + 4);
    }

    /// <summary>
    /// 目标门户直径为当前工作区可用高度的 68%；过窄显示器优先保证整个圆形仍在屏幕内。
    /// </summary>
    internal static double CalculateAgentPortalDiameter(double workingAreaWidth, double workingAreaHeight)
    {
        var availableWidth = Math.Max(0, workingAreaWidth - WindowPadding);
        var availableHeight = Math.Max(0, workingAreaHeight - WindowPadding);
        return Math.Min(availableWidth, Math.Min(availableHeight, workingAreaHeight * AgentPortalScreenHeightRatio));
    }

    internal static IReadOnlyList<FloatingWindowHitRegion> CalculateHitRegions(
        DesktopRobotLayout layout,
        bool isCardExpanded,
        double agentPortalDiameter = 0)
    {
        var regions = new List<FloatingWindowHitRegion>(4)
        {
            new(
                new Rect(
                    WindowInset,
                    WindowInset + layout.MascotTop,
                    layout.MascotSize,
                    layout.MascotSize),
                layout.MascotSize / 2,
                IsEllipse: true),
            new(
                new Rect(
                    WindowInset + layout.StatusLeft,
                    WindowInset + layout.StatusTop,
                    layout.StatusWidth,
                    CompactStatusHeight),
                CompactStatusCornerRadius),
            // 纽铃徽标位于宠物圆环右上角，不能被圆形原生区域裁掉。
            new(
                new Rect(
                    WindowInset + (MascotSurfaceSize - NeuBellBadgeSize - NeuBellBadgeRight) * layout.Scale,
                    WindowInset + layout.MascotTop + NeuBellBadgeTop * layout.Scale,
                    NeuBellBadgeSize * layout.Scale,
                    NeuBellBadgeSize * layout.Scale),
                NeuBellBadgeSize * layout.Scale / 2,
                IsEllipse: true)
        };

        if (isCardExpanded)
        {
            regions.Add(new FloatingWindowHitRegion(
                new Rect(
                    WindowInset + layout.CardLeft,
                    WindowInset + layout.CardTop,
                    CardWidth,
                    CardHeight),
                CardCornerRadius));
        }

        if (agentPortalDiameter > 0)
        {
            var rootHeight = Math.Max(layout.RootHeight, agentPortalDiameter);
            var portalTop = (rootHeight - agentPortalDiameter) / 2;
            regions.Add(new FloatingWindowHitRegion(
                new Rect(
                    WindowInset,
                    WindowInset + portalTop,
                    agentPortalDiameter,
                    agentPortalDiameter),
                agentPortalDiameter / 2,
                IsEllipse: true));
        }

        return regions;
    }

    private void UpdateNativeHitArea(DesktopRobotLayout layout)
    {
        var portalDiameter = _isAgentPortalViewportActive
            ? Math.Max(layout.MascotSize, _agentPortalViewportDiameter)
            : 0;
        _hitRegions = CalculateHitRegions(layout, _isCardExpanded, portalDiameter);
        if (_isOpened)
        {
            FloatingWindowPlatformService.ApplyHitRegions(this, _hitRegions);
        }
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
        var portalDiameter = _isAgentPortalViewportActive
            ? Math.Max(layout.MascotSize, _agentPortalViewportDiameter)
            : layout.MascotSize;
        var rootWidth = _isAgentPortalViewportActive
            ? Math.Max(layout.RootWidth, portalDiameter)
            : layout.RootWidth;
        var rootHeight = _isAgentPortalViewportActive
            ? Math.Max(layout.RootHeight, portalDiameter)
            : layout.RootHeight;
        var portalTop = (rootHeight - portalDiameter) / 2;

        Width = rootWidth + WindowPadding;
        Height = rootHeight + WindowPadding;
        RootSurface.Width = rootWidth;
        RootSurface.Height = rootHeight;
        MascotSurface.RenderTransform = new ScaleTransform(_currentScale, _currentScale);
        Canvas.SetLeft(MascotSurface, 0);
        Canvas.SetTop(MascotSurface, layout.MascotTop);
        Canvas.SetLeft(AgentPortalSurface, 0);
        Canvas.SetTop(AgentPortalSurface, portalTop);
        AgentPortalSurface.Width = portalDiameter;
        AgentPortalSurface.Height = portalDiameter;
        Canvas.SetLeft(ExpandedCard, layout.CardLeft);
        Canvas.SetTop(ExpandedCard, layout.CardTop);
        Canvas.SetLeft(CompactStatus, layout.StatusLeft);
        Canvas.SetTop(CompactStatus, layout.StatusTop);
        Canvas.SetLeft(HideRobotButton, layout.CardLeft + CardWidth - 34);
        Canvas.SetTop(HideRobotButton, layout.CardTop + 10);
        Canvas.SetLeft(HoverBridge, layout.BridgeLeft);
        Canvas.SetTop(HoverBridge, layout.BridgeTop);
        Canvas.SetLeft(
            PortalReturnButton,
            Math.Max(12, (portalDiameter - PortalReturnButtonWidth) / 2));
        Canvas.SetTop(
            PortalReturnButton,
            portalTop + portalDiameter - PortalReturnButtonHeight - PortalReturnButtonBottomInset);
        UpdateNativeHitArea(layout);
        if (_isAgentPortalViewportActive)
        {
            KeepAgentPortalCenteredOnAnchor();
        }
    }

    private void SetCardExpanded(bool isExpanded)
    {
        if (_isAgentPortalViewportActive)
        {
            isExpanded = false;
        }

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
        if (!_isOpened)
        {
            return;
        }

        if (e.PropertyName == nameof(DesktopRobotViewModel.IsAgentPortalOpen))
        {
            Dispatcher.UIThread.Post(
                () => HandleAgentPortalStateChanged(Robot?.IsAgentPortalOpen == true),
                DispatcherPriority.Input);
            return;
        }

        if (_isCardExpanded || _isAgentPortalViewportActive || e.PropertyName != nameof(DesktopRobotViewModel.StateText))
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
        _agentPortalViewportTimer.Stop();
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
        _agentPortalViewportTimer.Stop();
        StopWindowTracking(_workspaceViewModel);
    }

    private void StopWindowTracking(MainWindowViewModel? workspaceViewModel)
    {
        _globalPointerTimer.Stop();
        _placementSaveTimer.Stop();
        if (_macOsMousePassthrough)
        {
            FloatingWindowPlatformService.SetMacOsMousePassthrough(this, passthrough: false);
            _macOsMousePassthrough = false;
        }

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
        if (!_isAgentPortalViewportActive)
        {
            SetCardExpanded(true);
        }
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
            if (!RootSurface.IsPointerOver && !_isAgentPortalViewportActive)
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

        UpdateMacOsPointerBehavior(pointerPosition);

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

    private void UpdateMacOsPointerBehavior(PixelPoint pointerPosition)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var localPoint = this.PointToClient(pointerPosition);
        var isInteractive = _hitRegions.Any(region => region.Contains(localPoint));
        var shouldPassThrough = !isInteractive;

        if (_macOsMousePassthrough != shouldPassThrough)
        {
            FloatingWindowPlatformService.SetMacOsMousePassthrough(this, shouldPassThrough);
            _macOsMousePassthrough = shouldPassThrough;
        }

        // NSWindow 忽略鼠标后不会再产生 PointerEntered/Exited；用已有的全局指针轮询
        // 保持悬停展开和收起，同时让透明像素后的应用收到点击。
        if (_isAgentPortalViewportActive)
        {
            return;
        }

        if (isInteractive && !_isCardExpanded)
        {
            SetCardExpanded(true);
            PromoteAboveOtherAlwaysOnTopWindows();
        }
        else if (!isInteractive && _isCardExpanded)
        {
            SetCardExpanded(false);
            Robot?.ResetGaze();
        }
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

    private void AgentPortalButton_OnClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        Robot?.ToggleAgentPortal();
    }

    private static void PortalReturnButton_OnPointerPressed(object? sender, PointerPressedEventArgs e) =>
        e.Handled = true;

    private void PortalReturnButton_OnClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        Robot?.ToggleAgentPortal();
    }

    private void AgentPortalMenuItem_OnClick(object? sender, RoutedEventArgs e) =>
        Robot?.ToggleAgentPortal();

    private static void NeuBellButton_OnPointerPressed(object? sender, PointerPressedEventArgs e) =>
        e.Handled = true;

    private void NeuBellButton_OnClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        NeuBellOpenRequested?.Invoke();
    }

    private void NeuBellMenuItem_OnClick(object? sender, RoutedEventArgs e) =>
        NeuBellOpenRequested?.Invoke();

    private void MascotMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string tag } &&
            Enum.TryParse<NcfMascotKind>(tag, ignoreCase: true, out var mascot))
        {
            Robot?.UseMascotOverride(mascot);
        }
    }

    private DesktopRobotViewModel? Robot => DataContext as DesktopRobotViewModel;

    private void HandleAgentPortalStateChanged(bool isOpen)
    {
        if (!_isOpened)
        {
            return;
        }

        var layout = CalculateLayout(_currentScale, _isCardExpanded, GetCompactStatusWidth());
        if (isOpen)
        {
            _agentPortalViewportTimer.Stop();
            _agentPortalViewportDiameter = layout.MascotSize;
            _agentPortalOriginalPosition = Position;
            _agentPortalAnchor = GetMascotCenterOnScreen();
            _isAgentPortalViewportActive = true;
            PortalReturnButton.IsVisible = true;
            SetCardExpanded(false);
            _agentPortalViewportTargetDiameter = Math.Max(
                layout.MascotSize,
                GetAgentPortalTargetDiameter());
        }
        else
        {
            _agentPortalViewportTargetDiameter = layout.MascotSize;
            PortalReturnButton.IsVisible = false;
        }

        UpdateWindowExtent();
        if (Math.Abs(_agentPortalViewportTargetDiameter - _agentPortalViewportDiameter) < .5)
        {
            CompleteAgentPortalViewportTransition();
        }
        else
        {
            _agentPortalViewportTimer.Start();
        }
    }

    private void AdvanceAgentPortalViewport()
    {
        var difference = _agentPortalViewportTargetDiameter - _agentPortalViewportDiameter;
        if (Math.Abs(difference) < .5)
        {
            _agentPortalViewportDiameter = _agentPortalViewportTargetDiameter;
            UpdateWindowExtent();
            CompleteAgentPortalViewportTransition();
            return;
        }

        _agentPortalViewportDiameter += difference * .28;
        UpdateWindowExtent();
    }

    private void CompleteAgentPortalViewportTransition()
    {
        _agentPortalViewportTimer.Stop();
        if (Robot?.IsAgentPortalOpen == true)
        {
            return;
        }

        _isAgentPortalViewportActive = false;
        _agentPortalAnchor = null;
        _agentPortalViewportDiameter = CalculateLayout(
            _currentScale,
            _isCardExpanded,
            GetCompactStatusWidth()).MascotSize;
        UpdateWindowExtent();
        if (_agentPortalOriginalPosition is { } originalPosition)
        {
            Position = originalPosition;
        }

        _agentPortalOriginalPosition = null;
    }

    private double GetAgentPortalTargetDiameter()
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary ?? Screens.All.FirstOrDefault();
        if (screen == null)
        {
            return _agentPortalViewportDiameter;
        }

        var scaling = screen.Scaling > 0 ? screen.Scaling : 1;
        return CalculateAgentPortalDiameter(
            screen.WorkingArea.Width / scaling,
            screen.WorkingArea.Height / scaling);
    }

    private PixelPoint? GetMascotCenterOnScreen()
    {
        var mascotCenter = MascotView.TranslatePoint(
            new Point(MascotView.Bounds.Width / 2, MascotView.Bounds.Height / 2), this);
        if (!mascotCenter.HasValue)
        {
            return null;
        }

        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        var scaling = screen?.Scaling > 0 ? screen.Scaling : 1;
        return new PixelPoint(
            Position.X + (int)Math.Round(mascotCenter.Value.X * scaling),
            Position.Y + (int)Math.Round(mascotCenter.Value.Y * scaling));
    }

    private void KeepAgentPortalCenteredOnAnchor()
    {
        if (!_agentPortalAnchor.HasValue)
        {
            return;
        }

        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary ?? Screens.All.FirstOrDefault();
        if (screen == null)
        {
            return;
        }

        var scaling = screen.Scaling > 0 ? screen.Scaling : 1;
        var diameter = Math.Max(0, _agentPortalViewportDiameter);
        var portalTop = (RootSurface.Height - diameter) / 2;
        var windowSize = new PixelSize(
            (int)Math.Ceiling(Width * scaling),
            (int)Math.Ceiling(Height * scaling));
        var centerX = (int)Math.Round((WindowInset + diameter / 2) * scaling);
        var centerY = (int)Math.Round((WindowInset + portalTop + diameter / 2) * scaling);
        var maximumX = Math.Max(screen.WorkingArea.X, screen.WorkingArea.Right - windowSize.Width);
        var maximumY = Math.Max(screen.WorkingArea.Y, screen.WorkingArea.Bottom - windowSize.Height);
        Position = new PixelPoint(
            Math.Clamp(_agentPortalAnchor.Value.X - centerX, screen.WorkingArea.X, maximumX),
            Math.Clamp(_agentPortalAnchor.Value.Y - centerY, screen.WorkingArea.Y, maximumY));
    }

    internal sealed record DesktopRobotLayout(
        double RootWidth,
        double RootHeight,
        double WindowWidth,
        double WindowHeight,
        double Scale,
        double MascotSize,
        double MascotTop,
        double CardLeft,
        double CardTop,
        double StatusLeft,
        double StatusTop,
        double StatusWidth,
        double BridgeLeft,
        double BridgeTop);
}
