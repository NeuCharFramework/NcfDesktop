/*----------------------------------------------------------------
    Copyright (C) 2026 Senparc

    文件名：AgentPortalView.cs
    文件功能描述：低功耗绘制桌面宠物身后的 Agents 状态空间

    创建标识：Senparc - 20260807

    修改标识：Senparc - 20260810
    修改描述：基于 AgentsManager 实时快照绘制工作组、Agent、协作连线和工作态数据流
    
    修改标识：Senparc - 20260812
    修改描述：v0.10.0 完善桌面端唤醒词会话激活与中英文提示

----------------------------------------------------------------*/

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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
    private static readonly SolidColorBrush GridBrush = Brush("#3238BDF8");
    private static readonly SolidColorBrush WorkingBrush = Brush("#22D3EE");
    private static readonly SolidColorBrush WaitingBrush = Brush("#94A3B8");
    private static readonly SolidColorBrush SuccessBrush = Brush("#34D399");
    private static readonly SolidColorBrush FailureBrush = Brush("#FB7185");
    private static readonly SolidColorBrush CancelledBrush = Brush("#64748B");
    private static readonly SolidColorBrush InfoBrush = Brush("#A78BFA");
    private static readonly SolidColorBrush GroupBrush = Brush("#1D4ED8");
    private static readonly SolidColorBrush GroupCoreBrush = Brush("#0E7490");
    private static readonly SolidColorBrush TextBrush = Brush("#D9F6FF");
    private static readonly SolidColorBrush MutedTextBrush = Brush("#8EB8CA");
    private static readonly SolidColorBrush HeaderBrush = Brush("#ED07182F");
    private static readonly SolidColorBrush LaneBrush = Brush("#D5081E37");
    private static readonly SolidColorBrush ActiveLaneBrush = Brush("#E50A294A");
    private static readonly SolidColorBrush AgentCardBrush = Brush("#E009203A");
    private static readonly SolidColorBrush ActiveAgentCardBrush = Brush("#F00B3652");
    private static readonly Pen GridPen = new(GridBrush, .75);
    private static readonly Pen InnerRingPen = new(Brush("#B838BDF8"), 1.4);
    private static readonly Pen MidRingPen = new(Brush("#A08B5CF6"), 2.1);
    private static readonly Pen OuterRingPen = new(Brush("#7634D399"), 1.1);
    private static readonly Pen LinkPen = new(Brush("#7548C5FF"), .8);
    private static readonly Pen HeaderPen = new(Brush("#A248C9FF"), 1);
    private static readonly Pen LanePen = new(Brush("#6638BDF8"), .8);
    private static readonly Pen ActiveLanePen = new(Brush("#B122D3EE"), 1.1);
    private static readonly Typeface LabelTypeface = new("Inter");

    public static readonly StyledProperty<bool> IsOpenProperty =
        AvaloniaProperty.Register<AgentPortalView, bool>(nameof(IsOpen));
    public static readonly StyledProperty<IReadOnlyList<AgentPortalNode>> NodesProperty =
        AvaloniaProperty.Register<AgentPortalView, IReadOnlyList<AgentPortalNode>>(
            nameof(Nodes),
            Array.Empty<AgentPortalNode>());
    public static readonly StyledProperty<AgentGraphSnapshot?> SnapshotProperty =
        AvaloniaProperty.Register<AgentPortalView, AgentGraphSnapshot?>(nameof(Snapshot));
    public static readonly StyledProperty<IReadOnlyList<AgentPortalRecentCompletion>> RecentCompletionsProperty =
        AvaloniaProperty.Register<AgentPortalView, IReadOnlyList<AgentPortalRecentCompletion>>(
            nameof(RecentCompletions),
            Array.Empty<AgentPortalRecentCompletion>());
    public static readonly StyledProperty<AgentPortalUsageSummary> UsageProperty =
        AvaloniaProperty.Register<AgentPortalView, AgentPortalUsageSummary>(
            nameof(Usage),
            AgentPortalUsageSummary.Unavailable);

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private bool _isAttached;
    private double _transition;
    private double _phase;
    private AgentPortalRenderGraph _graph = AgentPortalRenderGraph.Empty;

    static AgentPortalView() => AffectsRender<AgentPortalView>(
        IsOpenProperty,
        NodesProperty,
        SnapshotProperty,
        RecentCompletionsProperty,
        UsageProperty);

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

    /// <summary>来自 AgentsManager 的只读 3D 图快照；为空时保留入口示意图。</summary>
    public AgentGraphSnapshot? Snapshot
    {
        get => GetValue(SnapshotProperty);
        set => SetValue(SnapshotProperty, value);
    }

    /// <summary>由连续实时快照确认的近期完成增量；没有可验证数据时为空。</summary>
    public IReadOnlyList<AgentPortalRecentCompletion> RecentCompletions
    {
        get => GetValue(RecentCompletionsProperty);
        set => SetValue(RecentCompletionsProperty, value);
    }

    /// <summary>当前执行任务的已授权 Token/时延采样；不可用时不以负载数据替代。</summary>
    public AgentPortalUsageSummary Usage
    {
        get => GetValue(UsageProperty);
        set => SetValue(UsageProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == NodesProperty || change.Property == SnapshotProperty)
        {
            _graph = AgentPortalRenderGraphProjection.Create(Snapshot, Nodes);
        }

        if (change.Property == IsOpenProperty || change.Property == NodesProperty || change.Property == SnapshotProperty)
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
        _graph = AgentPortalRenderGraphProjection.Create(Snapshot, Nodes);
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
        var pulse = _graph.HasContent ? (Math.Sin(_phase) + 1) * .5 : .35;

        context.DrawEllipse(SpaceBrush, null, center, radius, radius);
        context.DrawEllipse(SpaceCoreBrush, null, center, radius * .78, radius * .78);
        DrawPerspectiveGrid(context, center, radius);
        if (_graph.HasContent)
        {
            DrawLiveGraph(context, center, radius, _graph);
        }
        else
        {
            DrawIllustrativePortal(context, center, radius);
        }

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

    }

    private void DrawIllustrativePortal(DrawingContext context, Point center, double radius)
    {
        context.DrawEllipse(null, LinkPen, center, radius * .17, radius * .17);
        context.DrawEllipse(InfoBrush, null, center, radius * .07, radius * .07);
        for (var index = 0; index < 7; index++)
        {
            var angle = _phase * .16 + index * Math.PI * 2 / 7;
            var distance = radius * (.28 + (index % 3) * .17);
            var particle = new Point(
                center.X + Math.Cos(angle) * distance,
                center.Y - radius * .15 + Math.Sin(angle) * distance * .46);
            context.DrawEllipse(WorkingBrush, null, particle, .65, .65);
        }

        if (radius >= 130)
        {
            DrawTextCentered(context, "AGENTS LINK", new Point(center.X, center.Y - radius * .08), radius * .042, TextBrush);
            DrawTextCentered(context, "等待实时状态", new Point(center.X, center.Y + radius * .03), radius * .03, MutedTextBrush);
        }
    }

    private void DrawLiveGraph(
        DrawingContext context,
        Point center,
        double radius,
        AgentPortalRenderGraph graph)
    {
        // 小尺寸的分组列表只保留活动状态信号；自由浮窗展开后才显示完整的协作面板。
        if (radius < 120)
        {
            DrawCompactLiveGraph(context, center, radius, graph);
            return;
        }

        DrawCommandBoard(context, center, radius, graph);
    }

    /// <summary>
    /// 真实多智能体状态的主视图：每一行对应一个 AgentsManager 工作组，而不是把全部 Agent
    /// 压在圆心。左侧先说明「谁在协调什么」，右侧显示实际参与者；仅工作中的链路有数据流。
    /// </summary>
    private void DrawCommandBoard(
        DrawingContext context,
        Point center,
        double radius,
        AgentPortalRenderGraph graph)
    {
        var lanes = CreateExecutionLanes(graph);
        var executionLanes = lanes.Where(item => item.State == AgentPortalNodeState.Working).ToArray();
        var sourceLanes = executionLanes.Length > 0 ? executionLanes : lanes.ToArray();
        var maximumLaneCount = radius >= 320 ? 5 : 4;
        var visibleLanes = sourceLanes.Take(maximumLaneCount).ToArray();
        var recentCompletions = RecentCompletions.Take(radius >= 320 ? 2 : 1).ToArray();

        var boardWidth = radius * 1.45;
        var headerHeight = Math.Clamp(radius * .12, 38, 48);
        var laneGap = Math.Clamp(radius * .014, 4, 6);
        var laneHeight = Math.Clamp(radius * .135, 44, 52);
        var sectionHeight = Math.Clamp(radius * .042, 13, 16);
        var emptyExecutionHeight = visibleLanes.Length == 0 ? Math.Clamp(radius * .075, 24, 30) : 0;
        var recentHeight = recentCompletions.Length == 0
            ? Math.Clamp(radius * .065, 22, 26)
            : recentCompletions.Length * Math.Clamp(radius * .058, 20, 24) + laneGap * Math.Max(0, recentCompletions.Length - 1);
        var overviewHeight = Math.Clamp(radius * .165, 52, 64);
        var boardHeight = headerHeight + sectionHeight * 2 + laneHeight * visibleLanes.Length +
                          laneGap * Math.Max(0, visibleLanes.Length - 1) + emptyExecutionHeight +
                          recentHeight + overviewHeight + radius * .12;
        var boardLeft = center.X - boardWidth / 2;
        var boardTop = center.Y - boardHeight / 2;
        var headerRect = new Rect(boardLeft, boardTop, boardWidth, headerHeight);

        DrawRoundedRectangle(context, HeaderBrush, HeaderPen, headerRect, 12);
        DrawTextLeft(
            context,
            "AGENTS COMMAND",
            new Point(headerRect.X + 16, headerRect.Y + 9),
            Math.Clamp(radius * .042, 13, 16),
            TextBrush);
        DrawTextLeft(
            context,
            $"实时指挥 · {graph.AgentSummary.WorkingCount} 个 Agent 执行 · {graph.TaskSummary.WorkingCount} 项任务运行",
            new Point(headerRect.X + 16, headerRect.Y + headerRect.Height * .57),
            Math.Clamp(radius * .025, 9, 11),
            MutedTextBrush);

        var liveDot = new Point(headerRect.Right - 54, headerRect.Y + headerRect.Height / 2);
        var livePulse = 1 + (Math.Sin(_phase * 1.6) + 1) * .12;
        context.DrawEllipse(WorkingBrush, null, liveDot, 5 * livePulse, 5 * livePulse);
        DrawTextLeft(
            context,
            "LIVE",
            new Point(liveDot.X + 10, liveDot.Y - 7),
            Math.Clamp(radius * .026, 9, 11),
            TextBrush);

        var cursorY = headerRect.Bottom + radius * .022;
        DrawSectionCaption(
            context,
            new Rect(boardLeft, cursorY, boardWidth, sectionHeight),
            "正在执行",
            executionLanes.Length > visibleLanes.Length
                ? $"显示 {visibleLanes.Length} / {executionLanes.Length} 项"
                : $"{graph.TaskSummary.WorkingCount} 项运行");
        cursorY += sectionHeight + laneGap;
        for (var index = 0; index < visibleLanes.Length; index++)
        {
            DrawWorkLane(context, new Rect(boardLeft, cursorY, boardWidth, laneHeight), radius, visibleLanes[index]);
            cursorY += laneHeight + laneGap;
        }

        if (visibleLanes.Length == 0)
        {
            DrawTextLeft(
                context,
                graph.TaskSummary.ActiveCount > 0 ? "当前没有可命名的协作任务，仍在同步组任务状态" : "当前没有正在执行的任务",
                new Point(boardLeft + 10, cursorY + 3),
                Math.Clamp(radius * .023, 8, 10),
                MutedTextBrush);
            cursorY += emptyExecutionHeight + laneGap;
        }

        DrawSectionCaption(
            context,
            new Rect(boardLeft, cursorY, boardWidth, sectionHeight),
            "刚完成",
            "以连续快照确认");
        cursorY += sectionHeight + laneGap;
        DrawRecentCompletionRows(
            context,
            new Rect(boardLeft, cursorY, boardWidth, recentHeight),
            radius,
            recentCompletions);
        cursorY += recentHeight + radius * .028;

        DrawOverviewPanel(
            context,
            new Rect(boardLeft, cursorY, boardWidth, overviewHeight),
            radius,
            graph,
            Usage);
    }

    private static IReadOnlyList<PortalWorkLane> CreateExecutionLanes(AgentPortalRenderGraph graph)
    {
        var agentsById = graph.Agents.ToDictionary(item => item.Id);
        var groupsById = graph.Groups.ToDictionary(item => item.Id);
        var lanes = new List<PortalWorkLane>();

        foreach (var collaboration in graph.Collaborations)
        {
            var agents = collaboration.AgentIds
                .Where(agentsById.ContainsKey)
                .Select(agentId => agentsById[agentId])
                .OrderByDescending(item => item.State == AgentPortalNodeState.Working)
                .ThenByDescending(item => item.ActiveTaskCount)
                .ThenBy(item => item.Label, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
            var group = groupsById.GetValueOrDefault(collaboration.GroupId);
            lanes.Add(new PortalWorkLane(
                group?.Label ?? $"工作组 {collaboration.GroupId}",
                collaboration.Label,
                collaboration.State,
                1,
                group?.IsEnabled ?? true,
                agents));
        }

        var collaborationGroupIds = graph.Collaborations.Select(item => item.GroupId).ToHashSet();
        foreach (var group in graph.Groups.Where(item => item.TaskSummary.ActiveCount > 0 && !collaborationGroupIds.Contains(item.Id)))
        {
            var members = graph.Links
                .Where(item => item.GroupId == group.Id && agentsById.ContainsKey(item.AgentId))
                .Select(item => agentsById[item.AgentId])
                .DistinctBy(item => item.Id)
                .OrderByDescending(item => item.State == AgentPortalNodeState.Working)
                .ThenByDescending(item => item.ActiveTaskCount)
                .ThenBy(item => item.Label, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
            lanes.Add(new PortalWorkLane(
                group.Label,
                group.TaskSummary.ActiveCount > 1
                    ? $"{group.TaskSummary.ActiveCount} 项任务（详情待同步）"
                    : "任务详情待同步",
                group.State,
                Math.Max(1, group.ActiveTaskCount),
                group.IsEnabled,
                members));
        }

        return lanes
            .OrderByDescending(item => item.State == AgentPortalNodeState.Working)
            .ThenByDescending(item => item.ActiveTaskCount)
            .ThenByDescending(item => item.IsEnabled)
            .ThenBy(item => item.GroupLabel, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(item => item.TaskLabel, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private void DrawWorkLane(DrawingContext context, Rect laneRect, double radius, PortalWorkLane lane)
    {
        var isWorking = lane.State == AgentPortalNodeState.Working;
        var outline = isWorking ? ActiveLanePen : LanePen;
        DrawRoundedRectangle(context, isWorking ? ActiveLaneBrush : LaneBrush, outline, laneRect, 10);

        var groupWidth = Math.Clamp(laneRect.Width * .27, 112, 150);
        var splitX = laneRect.X + groupWidth;
        context.DrawLine(outline, new Point(splitX, laneRect.Y + 7), new Point(splitX, laneRect.Bottom - 7));

        var beaconCenter = new Point(laneRect.X + 21, laneRect.Y + laneRect.Height / 2);
        var beaconBrush = GetNodeBrush(lane.State);
        var pulse = isWorking ? 1 + (Math.Sin(_phase * 1.55 + lane.GroupLabel.Length) + 1) * .14 : 1d;
        context.DrawEllipse(beaconBrush, null, beaconCenter, 6 * pulse, 6 * pulse);
        context.DrawEllipse(null, new Pen(beaconBrush, 1.2), beaconCenter, 10 * pulse, 10 * pulse);
        if (isWorking)
        {
            context.DrawEllipse(null, new Pen(WorkingBrush, 1), beaconCenter, 15 * pulse, 15 * pulse);
        }

        DrawTextLeft(
            context,
            Shorten(lane.GroupLabel, 12),
            new Point(laneRect.X + 37, laneRect.Y + 7),
            Math.Clamp(radius * .027, 9, 11),
            lane.IsEnabled ? TextBrush : MutedTextBrush);
        DrawTextLeft(
            context,
            GetLaneStatusText(lane),
            new Point(laneRect.X + 37, laneRect.Y + laneRect.Height * .57),
            Math.Clamp(radius * .020, 7, 9),
            MutedTextBrush);

        var cards = lane.Agents.Take(4).ToArray();
        var cardGap = Math.Clamp(radius * .012, 4, 6);
        var cardsLeft = splitX + 11;
        DrawTextLeft(
            context,
            $"任务 · {Shorten(lane.TaskLabel, 31)}",
            new Point(cardsLeft, laneRect.Y + 5),
            Math.Clamp(radius * .020, 7.5, 9),
            MutedTextBrush);

        var countMarkerWidth = lane.Agents.Count > cards.Length ? 28d : 0;
        var availableWidth = laneRect.Right - cardsLeft - 12 - countMarkerWidth;
        var cardWidth = cards.Length == 0
            ? 0
            : Math.Min(98, (availableWidth - cardGap * Math.Max(0, cards.Length - 1)) / cards.Length);
        var cardTop = laneRect.Y + laneRect.Height - 22;
        var cardHeight = 17d;
        var source = new Point(splitX + 6, laneRect.Y + laneRect.Height * .68);
        for (var index = 0; index < cards.Length; index++)
        {
            var cardRect = new Rect(cardsLeft + index * (cardWidth + cardGap), cardTop, cardWidth, cardHeight);
            DrawAgentChip(context, cardRect, radius, cards[index], source, $"{lane.GroupLabel}-{lane.TaskLabel}-{cards[index].Id}");
        }

        if (lane.Agents.Count > cards.Length)
        {
            var markerCenter = new Point(laneRect.Right - 19, laneRect.Bottom - 13);
            context.DrawEllipse(InfoBrush, null, markerCenter, 10, 10);
            DrawTextCentered(context, $"+{lane.Agents.Count - cards.Length}", new Point(markerCenter.X, markerCenter.Y - 4), 7, TextBrush);
        }
    }

    private void DrawAgentChip(
        DrawingContext context,
        Rect cardRect,
        double radius,
        AgentPortalRenderNode agent,
        Point source,
        string flowKey)
    {
        var isWorking = agent.State == AgentPortalNodeState.Working;
        var brush = GetNodeBrush(agent.State);
        var center = new Point(cardRect.X, cardRect.Y + cardRect.Height / 2);
        context.DrawLine(
            isWorking ? new Pen(WorkingBrush, 1.15) : new Pen(Brush("#514893B7"), .7),
            source,
            center);
        if (isWorking)
        {
            DrawFlowPacket(context, source, center, radius, flowKey, brush);
        }

        DrawRoundedRectangle(
            context,
            isWorking ? ActiveAgentCardBrush : AgentCardBrush,
            new Pen(brush, isWorking ? 1 : .7),
            cardRect,
            5);
        var dot = new Point(cardRect.X + 7, cardRect.Y + cardRect.Height / 2);
        var dotPulse = isWorking ? 1 + (Math.Sin(_phase * 1.7 + agent.Id) + 1) * .12 : 1;
        context.DrawEllipse(brush, null, dot, 2.7 * dotPulse, 2.7 * dotPulse);
        DrawTextLeft(
            context,
            Shorten(agent.Label, 10),
            new Point(cardRect.X + 13, cardRect.Y + 3),
            Math.Clamp(radius * .019, 7, 8.5),
            agent.IsEnabled ? TextBrush : MutedTextBrush);
    }

    private static void DrawSectionCaption(DrawingContext context, Rect rect, string title, string detail)
    {
        DrawTextLeft(context, title, new Point(rect.X + 2, rect.Y), 9, TextBrush);
        var detailText = new FormattedText(
            detail,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            LabelTypeface,
            8,
            MutedTextBrush);
        context.DrawText(detailText, new Point(rect.Right - detailText.Width - 2, rect.Y + 1));
    }

    private static void DrawRecentCompletionRows(
        DrawingContext context,
        Rect rect,
        double radius,
        IReadOnlyList<AgentPortalRecentCompletion> completions)
    {
        if (completions.Count == 0)
        {
            DrawTextLeft(
                context,
                "本次连接尚未观察到可验证的完成增量",
                new Point(rect.X + 8, rect.Y + 3),
                Math.Clamp(radius * .021, 7, 9),
                MutedTextBrush);
            return;
        }

        var rowGap = 4d;
        var rowHeight = (rect.Height - rowGap * Math.Max(0, completions.Count - 1)) / completions.Count;
        for (var index = 0; index < completions.Count; index++)
        {
            var completion = completions[index];
            var row = new Rect(rect.X, rect.Y + index * (rowHeight + rowGap), rect.Width, rowHeight);
            DrawRoundedRectangle(context, Brush("#B10C3A35"), new Pen(SuccessBrush, .8), row, 7);
            var marker = new Point(row.X + 12, row.Y + row.Height / 2);
            context.DrawEllipse(SuccessBrush, null, marker, 4, 4);
            var label = string.IsNullOrWhiteSpace(completion.TaskLabel)
                ? $"{Shorten(completion.GroupLabel, 22)} · 完成 +{completion.CompletedTaskCount}"
                : $"{Shorten(completion.TaskLabel, 25)} · {Shorten(completion.GroupLabel, 13)}";
            DrawTextLeft(
                context,
                label,
                new Point(row.X + 22, row.Y + Math.Max(2, row.Height * .18)),
                Math.Clamp(radius * .021, 7, 9),
                TextBrush);
        }
    }

    private static void DrawOverviewPanel(
        DrawingContext context,
        Rect rect,
        double radius,
        AgentPortalRenderGraph graph,
        AgentPortalUsageSummary usage)
    {
        DrawRoundedRectangle(context, HeaderBrush, HeaderPen, rect, 9);
        var tasks = graph.TaskSummary;
        var agents = graph.AgentSummary;
        var taskLine = $"全任务 运行 {tasks.WorkingCount} · 等待 {tasks.WaitingCount} · 暂停 {tasks.PausedCount} · 完成 {tasks.FinishedCount} · 取消 {tasks.CancelledCount} · 故障 {tasks.FailedCount}";
        var promptScore = agents.HasPromptScore
            ? $"评分 {agents.AveragePromptScore:0.#}"
            : "评分 --";
        var agentLine = $"Agent 执行 {agents.WorkingCount} · 启用 {agents.EnabledCount} · 停用 {agents.DisabledCount} · 并发负载 {agents.ActiveAssignments} · {promptScore}";
        var usageLine = BuildUsageLine(usage);
        DrawTextLeft(
            context,
            taskLine,
            new Point(rect.X + 10, rect.Y + 7),
            Math.Clamp(radius * .020, 7, 9),
            TextBrush);
        DrawTextLeft(
            context,
            agentLine,
            new Point(rect.X + 10, rect.Y + rect.Height * .38),
            Math.Clamp(radius * .020, 7, 9),
            MutedTextBrush);
        DrawTextLeft(
            context,
            usageLine,
            new Point(rect.X + 10, rect.Y + rect.Height * .70),
            Math.Clamp(radius * .020, 7, 9),
            usage.IsAvailable ? InfoBrush : MutedTextBrush);
    }

    private static string BuildUsageLine(AgentPortalUsageSummary usage)
    {
        if (!usage.IsAvailable)
        {
            return "用量 · 当前服务未提供已授权 Token 统计";
        }

        if (usage.SampledTaskCount == 0)
        {
            return usage.RunningTaskCount == 0
                ? "用量 · 当前没有运行任务"
                : $"用量 · {usage.RunningTaskCount} 项运行，暂无可查询的协作任务";
        }

        return $"用量 · 采样 {usage.SampledTaskCount}/{usage.RunningTaskCount} 项 · Token {usage.TotalTokens:N0} · 消息 {usage.MessageCount} · P95 {usage.P95ResponseMilliseconds}ms";
    }

    private void DrawCompactLiveGraph(
        DrawingContext context,
        Point center,
        double radius,
        AgentPortalRenderGraph graph)
    {
        var coreRadius = Math.Max(3, radius * .16);
        context.DrawEllipse(GroupBrush, null, center, coreRadius, coreRadius);
        context.DrawEllipse(null, MidRingPen, center, coreRadius * 1.45, coreRadius * 1.45);
        var agents = graph.Agents.Take(6).ToArray();
        for (var index = 0; index < agents.Length; index++)
        {
            var agent = agents[index];
            var angle = -Math.PI / 2 + Math.PI * 2 * index / Math.Max(1, agents.Length) + _phase * .05;
            var point = new Point(
                center.X + Math.Cos(angle) * radius * .55,
                center.Y + Math.Sin(angle) * radius * .45);
            var brush = GetNodeBrush(agent.State);
            var size = agent.State == AgentPortalNodeState.Working ? 3.7 : 2.8;
            context.DrawLine(new Pen(brush, .7), center, point);
            context.DrawEllipse(brush, null, point, size, size);
        }
    }

    private void DrawFlowPacket(
        DrawingContext context,
        Point from,
        Point to,
        double radius,
        string key,
        IBrush brush)
    {
        var fraction = (_phase / (Math.PI * 2) + GetStablePhase(key)) % 1;
        var point = new Point(
            from.X + (to.X - from.X) * fraction,
            from.Y + (to.Y - from.Y) * fraction);
        var size = Math.Max(1.8, radius * .009);
        context.DrawEllipse(brush, null, point, size, size);
    }

    private static void DrawTextCentered(DrawingContext context, string text, Point center, double fontSize, IBrush brush)
    {
        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            LabelTypeface,
            Math.Max(7, fontSize),
            brush);
        context.DrawText(formatted, new Point(center.X - formatted.Width / 2, center.Y));
    }

    private static void DrawTextLeft(DrawingContext context, string text, Point point, double fontSize, IBrush brush)
    {
        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            LabelTypeface,
            Math.Max(7, fontSize),
            brush);
        context.DrawText(formatted, point);
    }

    private static void DrawRoundedRectangle(
        DrawingContext context,
        IBrush fill,
        IPen pen,
        Rect rect,
        double radius) =>
        context.DrawRectangle(fill, pen, rect, radius, radius, default);

    private static string GetLaneStatusText(PortalWorkLane lane) => lane.State switch
    {
        AgentPortalNodeState.Working => $"运行中 · {Math.Max(1, lane.ActiveTaskCount)} 项",
        AgentPortalNodeState.Info => "暂停中",
        AgentPortalNodeState.Cancelled => "已停用",
        _ => lane.ActiveTaskCount > 0 ? $"排队 · {lane.ActiveTaskCount} 项" : "待命"
    };

    private static string Shorten(string? value, int maximumLength)
    {
        var text = string.IsNullOrWhiteSpace(value) ? "--" : value.Trim();
        return text.Length <= maximumLength ? text : text[..Math.Max(1, maximumLength - 1)] + "…";
    }

    private static double GetStablePhase(string value)
    {
        var hash = 17;
        foreach (var character in value)
        {
            hash = unchecked(hash * 31 + character);
        }

        return (uint)hash % 997 / 997d;
    }

    private sealed record PortalWorkLane(
        string GroupLabel,
        string TaskLabel,
        AgentPortalNodeState State,
        int ActiveTaskCount,
        bool IsEnabled,
        IReadOnlyList<AgentPortalRenderNode> Agents);

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
        if (!isTransitioning && (!IsOpen || !_graph.HasContent))
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
