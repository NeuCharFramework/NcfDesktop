/*----------------------------------------------------------------
    Copyright (C) 2026 Senparc

    文件名：AgentPortalView.cs
    文件功能描述：低功耗绘制桌面宠物身后的 Agents 状态空间

    创建标识：Senparc - 20260807
----------------------------------------------------------------*/

using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NcfDesktopApp.GUI.Models;

namespace NcfDesktopApp.GUI.Views.Controls;

/// <summary>
/// 以少量矢量图元模拟具有纵深感的空间门户。进入和退出期间使用 25 FPS，
/// 稳定显示时降至 8 FPS；不可见或关闭后完全停止计时器。
/// </summary>
public sealed class AgentPortalView : Control
{
    private static readonly SolidColorBrush SpaceBrush = Brush("#F2051022");
    private static readonly SolidColorBrush SpaceCoreBrush = Brush("#E80A1F3D");
    private static readonly SolidColorBrush GridBrush = Brush("#5B38BDF8");
    private static readonly SolidColorBrush WorkingBrush = Brush("#22D3EE");
    private static readonly SolidColorBrush WaitingBrush = Brush("#94A3B8");
    private static readonly SolidColorBrush SuccessBrush = Brush("#34D399");
    private static readonly SolidColorBrush FailureBrush = Brush("#FB7185");
    private static readonly SolidColorBrush CancelledBrush = Brush("#64748B");
    private static readonly SolidColorBrush InfoBrush = Brush("#A78BFA");
    private static readonly Pen GridPen = new(GridBrush, .75);
    private static readonly Pen InnerRingPen = new(Brush("#B838BDF8"), 1.4);
    private static readonly Pen MidRingPen = new(Brush("#A08B5CF6"), 2.1);
    private static readonly Pen OuterRingPen = new(Brush("#7634D399"), 1.1);
    private static readonly Pen LinkPen = new(Brush("#7548C5FF"), .8);

    public static readonly StyledProperty<bool> IsOpenProperty =
        AvaloniaProperty.Register<AgentPortalView, bool>(nameof(IsOpen));
    public static readonly StyledProperty<IReadOnlyList<AgentPortalNode>> NodesProperty =
        AvaloniaProperty.Register<AgentPortalView, IReadOnlyList<AgentPortalNode>>(
            nameof(Nodes),
            Array.Empty<AgentPortalNode>());

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private bool _isAttached;
    private double _transition;
    private double _phase;

    static AgentPortalView() => AffectsRender<AgentPortalView>(IsOpenProperty, NodesProperty);

    public AgentPortalView()
    {
        IsHitTestVisible = false;
        _timer.Tick += (_, _) => AdvanceFrame();
    }

    public bool IsOpen
    {
        get => GetValue(IsOpenProperty);
        set => SetValue(IsOpenProperty, value);
    }

    public IReadOnlyList<AgentPortalNode> Nodes
    {
        get => GetValue(NodesProperty);
        set => SetValue(NodesProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsOpenProperty || change.Property == NodesProperty)
        {
            if (change.Property == IsOpenProperty && IsOpen && _transition <= .002)
            {
                // 第一帧即保留原宠物圆框大小，避免宠物消失与门户出现之间闪空。
                _transition = .02;
            }

            UpdateTimerState();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _isAttached = true;
        UpdateTimerState();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _isAttached = false;
        _timer.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (_transition <= .002 || Bounds.Width < 4 || Bounds.Height < 4)
        {
            return;
        }

        var eased = 1 - Math.Pow(1 - _transition, 3);
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var maximumRadius = Math.Max(2, Math.Min(Bounds.Width, Bounds.Height) / 2 - 2);
        var radius = maximumRadius * (.58 + .42 * eased);
        var pulse = Nodes.Count > 0 ? (Math.Sin(_phase) + 1) * .5 : .35;

        context.DrawEllipse(SpaceBrush, null, center, radius, radius);
        context.DrawEllipse(SpaceCoreBrush, null, center, radius * .78, radius * .78);
        DrawPerspectiveGrid(context, center, radius);
        DrawNodes(context, center, radius);

        context.DrawEllipse(null, InnerRingPen, center, radius * .91, radius * .91);
        context.DrawEllipse(null, MidRingPen, center, radius + pulse * 1.1, radius + pulse * 1.1);
        context.DrawEllipse(null, OuterRingPen, center, radius + 2.2 + pulse * 1.4, radius + 2.2 + pulse * 1.4);
    }

    private void DrawPerspectiveGrid(DrawingContext context, Point center, double radius)
    {
        var horizonY = center.Y - radius * .1;
        for (var index = 1; index <= 4; index++)
        {
            var ratio = index / 4d;
            var y = horizonY + radius * .78 * ratio;
            var halfWidth = Math.Sqrt(Math.Max(0, radius * radius - Math.Pow(y - center.Y, 2))) * .83;
            context.DrawLine(GridPen, new Point(center.X - halfWidth, y), new Point(center.X + halfWidth, y));
        }

        for (var index = -2; index <= 2; index++)
        {
            var bottomX = center.X + index * radius * .28;
            context.DrawLine(
                GridPen,
                new Point(center.X + index * radius * .055, horizonY),
                new Point(bottomX, center.Y + radius * .82));
        }

        for (var index = 0; index < 7; index++)
        {
            var angle = _phase * .16 + index * Math.PI * 2 / 7;
            var distance = radius * (.28 + (index % 3) * .17);
            var particle = new Point(
                center.X + Math.Cos(angle) * distance,
                center.Y - radius * .15 + Math.Sin(angle) * distance * .46);
            context.DrawEllipse(WorkingBrush, null, particle, .65, .65);
        }
    }

    private void DrawNodes(DrawingContext context, Point center, double radius)
    {
        var count = Math.Min(AgentPortalProjection.MaximumVisibleNodes, Nodes.Count);
        if (count == 0)
        {
            context.DrawEllipse(null, LinkPen, center, radius * .17, radius * .17);
            context.DrawEllipse(InfoBrush, null, center, radius * .07, radius * .07);
            return;
        }

        var coreRadius = Math.Max(2.2, radius * .075);
        context.DrawEllipse(InfoBrush, null, center, coreRadius, coreRadius);
        for (var index = 0; index < count; index++)
        {
            var node = Nodes[index];
            var angle = -Math.PI / 2 + Math.PI * 2 * index / Math.Max(1, count) + _phase * .025;
            var x = center.X + Math.Cos(angle) * radius * .55;
            var y = center.Y + Math.Sin(angle) * radius * .33;
            var depth = (Math.Sin(angle) + 1) * .5;
            var nodeRadius = radius * (.055 + depth * .035);
            var point = new Point(x, y);
            var brush = GetNodeBrush(node.State);

            context.DrawLine(LinkPen, center, point);
            if (node.State == AgentPortalNodeState.Working)
            {
                var pulse = 1 + (Math.Sin(_phase * 1.35 + index) + 1) * .18;
                context.DrawEllipse(null, InnerRingPen, point, nodeRadius * pulse * 1.65, nodeRadius * pulse * 1.65);
            }

            context.DrawEllipse(brush, null, point, nodeRadius, nodeRadius);
            if (node.Progress > 0)
            {
                var progressRadius = nodeRadius * (.35 + .55 * node.Progress / 100d);
                context.DrawEllipse(SpaceBrush, null, point, progressRadius, progressRadius);
            }
        }
    }

    private void AdvanceFrame()
    {
        if (!IsEffectivelyVisible)
        {
            _timer.Stop();
            return;
        }

        var target = IsOpen ? 1d : 0d;
        var difference = target - _transition;
        _transition = Math.Abs(difference) < .012 ? target : _transition + difference * .24;
        _phase = (_phase + .34) % (Math.PI * 2);
        InvalidateVisual();

        var isTransitioning = Math.Abs(target - _transition) >= .012;
        _timer.Interval = isTransitioning
            ? TimeSpan.FromMilliseconds(40)
            : TimeSpan.FromMilliseconds(125);
        if (!isTransitioning && (!IsOpen || Nodes.Count == 0))
        {
            _timer.Stop();
        }
    }

    private void UpdateTimerState()
    {
        if (!_isAttached || !IsEffectivelyVisible)
        {
            _timer.Stop();
            return;
        }

        if (IsOpen || _transition > .002)
        {
            _timer.Start();
            InvalidateVisual();
        }
    }

    private static IBrush GetNodeBrush(AgentPortalNodeState state) => state switch
    {
        AgentPortalNodeState.Working => WorkingBrush,
        AgentPortalNodeState.Succeeded => SuccessBrush,
        AgentPortalNodeState.Failed => FailureBrush,
        AgentPortalNodeState.Cancelled => CancelledBrush,
        AgentPortalNodeState.Info => InfoBrush,
        _ => WaitingBrush
    };

    private static SolidColorBrush Brush(string value) => new(Color.Parse(value));
}
