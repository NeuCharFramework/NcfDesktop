/*----------------------------------------------------------------
    Copyright (C) 2026 Senparc

    文件名：DesktopRobotViewModel.cs
    文件功能描述：桌面机器人显示状态与兼容模式日志映射

    创建标识：Senparc - 20260725

    修改标识：Senparc - 20260804
    修改描述：v0.6.0 扩展桌面机器人的交互状态与音频反馈

----------------------------------------------------------------*/

using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using NcfDesktopApp.GUI.Models;

namespace NcfDesktopApp.GUI.ViewModels;

public partial class DesktopRobotViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _emoji = "🤖";

    [ObservableProperty]
    private NcfMascotKind _mascot = NcfMascotKind.Nono;

    [ObservableProperty]
    private NcfMascotPose _pose = NcfMascotPose.Idle;

    [ObservableProperty]
    private double _gazeX;

    [ObservableProperty]
    private double _gazeY;

    [ObservableProperty]
    private bool _isInteracting;

    [ObservableProperty]
    private string _mascotName = "Nono";

    [ObservableProperty]
    private string _title = "NCF 桌面助手";

    [ObservableProperty]
    private string _detail = "等待启动 NCF";

    [ObservableProperty]
    private string _stateText = "待机";

    [ObservableProperty]
    private string _stateColor = "#6C757D";

    [ObservableProperty]
    private string _connectionText = "尚未连接";

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private bool _isProgressVisible;

    [ObservableProperty]
    private AudioVisualizationMode _audioVisualizationMode;

    [ObservableProperty]
    private double _audioVisualizationLevel;

    [ObservableProperty]
    private double[] _audioVisualizationBands = new double[12];

    [ObservableProperty]
    private bool _isAgentPortalAvailable;

    [ObservableProperty]
    private bool _isAgentPortalOpen;

    [ObservableProperty]
    private string _agentPortalStatusText = "等待 AgentsManager 活动";

    [ObservableProperty]
    private IReadOnlyList<AgentPortalNode> _agentPortalNodes = Array.Empty<AgentPortalNode>();

    [ObservableProperty]
    private AgentGraphSnapshot? _agentPortalSnapshot;

    [ObservableProperty]
    private IReadOnlyList<AgentPortalRecentCompletion> _agentPortalRecentCompletions =
        Array.Empty<AgentPortalRecentCompletion>();

    [ObservableProperty]
    private AgentPortalUsageSummary _agentPortalUsage = AgentPortalUsageSummary.Unavailable;

    [ObservableProperty]
    private int _neuBellCount;

    [ObservableProperty]
    private string _neuBellBadgeText = string.Empty;

    [ObservableProperty]
    private string _neuBellSummary = "当前没有纽铃提醒";

    private DispatcherTimer? _interactionTimer;
    private readonly Dictionary<string, AgentPortalNode> _activeAgentPortalNodes =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly AgentPortalRecentCompletionTracker _agentPortalRecentCompletionTracker = new();
    private NcfMascotKind _resolvedMascot = NcfMascotKind.Nono;
    private NcfMascotKind _mascotOverride = NcfMascotKind.Nono;
    private bool _isMascotOverride;

    public bool IsMascotVisible => !IsAgentPortalOpen;

    public string AgentPortalActionText => IsAgentPortalOpen ? "返回" : "Agents";

    public bool HasNeuBellNotifications => NeuBellCount > 0;

    partial void OnNeuBellCountChanged(int value) =>
        OnPropertyChanged(nameof(HasNeuBellNotifications));

    public void ApplyNeuBellPresentation(int count, string badgeText, string summary)
    {
        RunOnUi(() =>
        {
            NeuBellCount = Math.Max(0, count);
            NeuBellBadgeText = NeuBellCount > 0 ? badgeText : string.Empty;
            NeuBellSummary = string.IsNullOrWhiteSpace(summary)
                ? $"纽铃有 {NeuBellCount} 条待处理提醒"
                : summary;
        });
    }

    public void ClearNeuBellNotifications()
    {
        RunOnUi(() =>
        {
            NeuBellCount = 0;
            NeuBellBadgeText = string.Empty;
            NeuBellSummary = "当前没有纽铃提醒";
        });
    }

    public void ToggleAgentPortal()
    {
        RunOnUi(() =>
        {
            if (!IsAgentPortalAvailable)
            {
                AgentPortalStatusText = "尚未检测到已启用的 AgentsManager 实时活动";
                return;
            }

            IsAgentPortalOpen = !IsAgentPortalOpen;
        });
    }

    public void ApplyAgentGraphSnapshot(AgentGraphSnapshot snapshot)
    {
        RunOnUi(() =>
        {
            var activeAgentIds = snapshot.Collaborations
                .Where(item => item.Status is 0 or 1 or 2)
                .SelectMany(item => item.AgentIds)
                .ToHashSet();
            var now = DateTimeOffset.UtcNow;

            _activeAgentPortalNodes.Clear();
            foreach (var agent in snapshot.Agents
                         .OrderByDescending(item => activeAgentIds.Contains(item.Id) || item.ChattingCount > 0)
                         .ThenByDescending(item => item.Enable)
                         .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
                         .Take(AgentPortalProjection.MaximumVisibleNodes))
            {
                var isWorking = activeAgentIds.Contains(agent.Id) || agent.ChattingCount > 0;
                var node = new AgentPortalNode(
                    $"agent-{agent.Id}",
                    string.IsNullOrWhiteSpace(agent.Name) ? $"Agent {agent.Id}" : agent.Name.Trim(),
                    isWorking
                        ? AgentPortalNodeState.Working
                        : agent.Enable
                            ? AgentPortalNodeState.Waiting
                            : AgentPortalNodeState.Cancelled,
                    isWorking ? Math.Clamp(25 + agent.ChattingCount * 15, 25, 100) : 0,
                    now);
                _activeAgentPortalNodes[node.Id] = node;
            }

            AgentPortalNodes = _activeAgentPortalNodes.Values.ToArray();
            AgentPortalSnapshot = snapshot;
            AgentPortalRecentCompletions = _agentPortalRecentCompletionTracker.Update(snapshot, now);
            IsAgentPortalAvailable = true;
            var activeCount = AgentPortalNodes.Count(item => item.State == AgentPortalNodeState.Working);
            var runningTaskCount = snapshot.Collaborations.Count(item => item.Status == 1);
            AgentPortalStatusText = runningTaskCount > 0
                ? $"Agents 空间 · {snapshot.Agents.Count} 个 Agent · {runningTaskCount} 个协作任务运行中"
                : $"Agents 空间 · {snapshot.Agents.Count} 个 Agent · {activeCount} 个工作中";
        });
    }

    public void SetAgentPortalUnavailable(string status)
    {
        RunOnUi(() =>
        {
            ResetAgentPortal();
            AgentPortalStatusText = string.IsNullOrWhiteSpace(status)
                ? "未检测到已启用的 AgentsManager"
                : status;
        });
    }

    public void ApplyAgentPortalUsage(AgentPortalUsageSummary usage)
    {
        RunOnUi(() => AgentPortalUsage = usage ?? AgentPortalUsageSummary.Unavailable);
    }

    partial void OnIsAgentPortalOpenChanged(bool value)
    {
        OnPropertyChanged(nameof(IsMascotVisible));
        OnPropertyChanged(nameof(AgentPortalActionText));
        if (value)
        {
            ResetGaze();
        }
    }

    /// <summary>更新浮动角色的视线方向，供桌面窗口的鼠标移动事件调用。</summary>
    public void UpdateGaze(double x, double y)
    {
        RunOnUi(() =>
        {
            GazeX = Math.Clamp(x, -1, 1);
            GazeY = Math.Clamp(y, -1, 1);
        });
    }

    public void ResetGaze() => UpdateGaze(0, 0);

    /// <summary>点击角色时显示一个短暂的回应动画，不改变当前 NCF 工作状态。</summary>
    public void ReactToPointer()
    {
        RunOnUi(() =>
        {
            IsInteracting = true;
            _interactionTimer ??= CreateInteractionTimer();
            _interactionTimer.Stop();
            _interactionTimer.Start();
        });
    }

    /// <summary>固定使用用户选择的角色；工作状态和姿态仍会继续更新。</summary>
    public void UseMascotOverride(NcfMascotKind mascot)
    {
        RunOnUi(() =>
        {
            _mascotOverride = mascot;
            _isMascotOverride = true;
            Mascot = mascot;
            MascotName = string.Concat(mascot, "（手动）");
        });
    }

    /// <summary>恢复根据来源、状态自动选择角色。</summary>
    public void UseAutomaticMascot()
    {
        RunOnUi(() =>
        {
            _isMascotOverride = false;
            Mascot = _resolvedMascot;
            MascotName = _resolvedMascot.ToString();
        });
    }

    public void SetProcessState(string state, string detail, bool isError = false)
    {
        RunOnUi(() =>
        {
            if (state is "已停止" or "授权已撤销")
            {
                ResetAgentPortal();
            }

            SetMascot(
                isError ? NcfMascotKind.Opsi : NcfMascotKind.Nono,
                isError ? NcfMascotPose.Warning : state switch
                {
                    "启动中" => NcfMascotPose.Working,
                    "已完成" => NcfMascotPose.Success,
                    "已停止" => NcfMascotPose.Idle,
                    _ => NcfMascotPose.Idle
                });
            Title = "NCF 桌面助手";
            Detail = detail;
            StateText = state;
            Emoji = isError ? "🛠️" : state switch
            {
                "运行中" => "🤖",
                "启动中" => "🚀",
                "已完成" => "✅",
                "已停止" => "💤",
                _ => "🤖"
            };
            StateColor = isError ? "#DC3545" : state switch
            {
                "运行中" => "#007ACC",
                "启动中" => "#6F42C1",
                "已完成" => "#28A745",
                "已停止" => "#6C757D",
                _ => "#6C757D"
            };
            IsProgressVisible = false;
        });
    }

    public void SetBridgeAvailability(DesktopBridgeProbeResult result)
    {
        RunOnUi(() =>
        {
            if (result.Availability != DesktopBridgeAvailability.Available)
            {
                ResetAgentPortal();
            }

            var pose = result.Availability switch
            {
                DesktopBridgeAvailability.Available => NcfMascotPose.Wave,
                DesktopBridgeAvailability.Unavailable => NcfMascotPose.Working,
                _ => NcfMascotPose.Warning
            };
            SetMascot(
                result.Availability is DesktopBridgeAvailability.NotInstalled
                    or DesktopBridgeAvailability.Incompatible
                    ? NcfMascotKind.Opsi
                    : NcfMascotKind.Qiao,
                pose);
            ConnectionText = result.Availability switch
            {
                DesktopBridgeAvailability.Available => "DesktopBridge 实时模式",
                DesktopBridgeAvailability.NotInstalled => "兼容模式 · 未安装 Bridge",
                DesktopBridgeAvailability.Incompatible => "兼容模式 · Bridge 待更新",
                DesktopBridgeAvailability.Unauthorized => "兼容模式 · 会话无效",
                DesktopBridgeAvailability.Inactive => "兼容模式 · Bridge 未启用",
                DesktopBridgeAvailability.Unavailable => "兼容模式 · 后台重连中",
                _ => "兼容模式"
            };
        });
    }

    /// <summary>显示语音输入、转写或配置状态。</summary>
    public void SetVoiceInputState(string state, string detail, bool isError = false)
    {
        RunOnUi(() =>
        {
            SetMascot(NcfMascotKind.Cici, isError ? NcfMascotPose.Warning : state switch
            {
                "正在录音" => NcfMascotPose.Working,
                "正在识别" => NcfMascotPose.Thinking,
                "识别完成" => NcfMascotPose.Success,
                _ => NcfMascotPose.Idle
            });
            Title = "AdminChat 语音输入";
            Detail = detail;
            StateText = state;
            StateColor = isError ? "#DC3545" : state switch
            {
                "正在录音" => "#DC2626",
                "正在识别" => "#7C3AED",
                "识别完成" => "#16A34A",
                _ => "#64748B"
            };
            Emoji = state == "正在录音" ? "🎙️" : isError ? "🛠️" : "💬";
            IsProgressVisible = state == "正在识别";
            Progress = 0;
        });
    }

    /// <summary>显示 AI 本地朗读状态。</summary>
    public void SetSpeechState(string state, string detail, bool isError = false)
    {
        RunOnUi(() =>
        {
            SetMascot(NcfMascotKind.Cici, isError ? NcfMascotPose.Warning : state switch
            {
                "正在朗读" => NcfMascotPose.Working,
                "朗读完成" => NcfMascotPose.Success,
                _ => NcfMascotPose.Idle
            });
            Title = "AdminChat 本地朗读";
            Detail = detail;
            StateText = state;
            StateColor = isError ? "#DC3545" : state == "正在朗读" ? "#7C3AED" : "#16A34A";
            Emoji = isError ? "🛠️" : state == "正在朗读" ? "🔊" : "💬";
            IsProgressVisible = state == "正在朗读";
            Progress = 0;
        });
    }

    /// <summary>用实际麦克风或 TTS PCM 数据驱动桌面角色周围的声场。</summary>
    public void SetAudioVisualization(AudioVisualizationMode mode, AudioVisualizationFrame frame)
    {
        RunOnUi(() =>
        {
            AudioVisualizationMode = mode;
            AudioVisualizationLevel = Math.Clamp(frame.Level, 0, 1);
            AudioVisualizationBands = frame.Bands.ToArray();
        });
    }

    public void ApplyActivity(DesktopActivityMessage activity)
    {
        RunOnUi(() =>
        {
            ApplyAgentPortalActivity(activity);
            SetMascot(ResolveMascot(activity.Source, activity.Title, activity.State),
                ResolvePose(activity.State));
            Title = string.IsNullOrWhiteSpace(activity.Source)
                ? activity.Title
                : $"{activity.Source} · {activity.Title}";
            Detail = string.IsNullOrWhiteSpace(activity.Detail) ? "NCF 正在处理系统任务" : activity.Detail;
            StateText = activity.State switch
            {
                "Working" => "工作中",
                "Succeeded" => "已完成",
                "Failed" => "发生错误",
                "Cancelled" => "已取消",
                _ => "新动态"
            };
            Emoji = activity.State switch
            {
                "Working" => "⚙️",
                "Succeeded" => "✅",
                "Failed" => "🛠️",
                "Cancelled" => "⏹️",
                _ => "🤖"
            };
            StateColor = activity.State switch
            {
                "Working" => "#007ACC",
                "Succeeded" => "#28A745",
                "Failed" => "#DC3545",
                "Cancelled" => "#6C757D",
                _ => "#6F42C1"
            };
            IsProgressVisible = activity.Progress.HasValue;
            Progress = activity.Progress ?? 0;
        });
    }

    private void ApplyAgentPortalActivity(DesktopActivityMessage activity)
    {
        if (!AgentPortalProjection.IsAgentsManagerActivity(activity))
        {
            return;
        }

        IsAgentPortalAvailable = true;
        var node = AgentPortalProjection.Project(activity);
        if (activity.IsTerminal)
        {
            _activeAgentPortalNodes.Remove(node.Id);
        }
        else
        {
            _activeAgentPortalNodes[node.Id] = node;
        }

        AgentPortalNodes = _activeAgentPortalNodes.Values
            .OrderByDescending(item => item.State == AgentPortalNodeState.Working)
            .ThenByDescending(item => item.Time)
            .Take(AgentPortalProjection.MaximumVisibleNodes)
            .ToArray();
        AgentPortalStatusText = AgentPortalNodes.Count == 0
            ? "AgentsManager 已连接 · 当前空闲"
            : $"Agents 空间 · {AgentPortalNodes.Count} 项活动";
    }

    private void ResetAgentPortal()
    {
        _activeAgentPortalNodes.Clear();
        _agentPortalRecentCompletionTracker.Reset();
        AgentPortalNodes = Array.Empty<AgentPortalNode>();
        AgentPortalSnapshot = null;
        AgentPortalRecentCompletions = Array.Empty<AgentPortalRecentCompletion>();
        AgentPortalUsage = AgentPortalUsageSummary.Unavailable;
        IsAgentPortalOpen = false;
        IsAgentPortalAvailable = false;
        AgentPortalStatusText = "等待 AgentsManager 活动";
    }

    public void ApplyCompatibilityLog(string message, bool isError)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        var looksLikeError = isError || ContainsAny(message, "error", "exception", "失败", "错误");
        var looksLikeCompleted = ContainsAny(message, "completed", "complete", "成功", "完成", "已启动");
        var looksLikeWork = ContainsAny(message, "starting", "processing", "running", "开始", "正在", "处理中");
        if (!looksLikeError && !looksLikeCompleted && !looksLikeWork)
        {
            return;
        }

        var detail = message.Length <= 180 ? message : message[..180] + "…";
        RunOnUi(() =>
        {
            Detail = detail;
            IsProgressVisible = false;
            if (looksLikeError)
            {
                SetMascot(NcfMascotKind.Opsi, NcfMascotPose.Warning);
                Emoji = "🛠️";
                StateText = "发生错误";
                StateColor = "#DC3545";
            }
            else if (looksLikeCompleted)
            {
                SetMascot(NcfMascotKind.Opsi, NcfMascotPose.Success);
                Emoji = "✅";
                StateText = "已完成";
                StateColor = "#28A745";
            }
            else
            {
                SetMascot(NcfMascotKind.Opsi, NcfMascotPose.Working);
                Emoji = "⚙️";
                StateText = "工作中";
                StateColor = "#007ACC";
            }
        });
    }

    internal static NcfMascotKind ResolveMascot(params string?[] values)
    {
        var value = string.Join(' ', values.Where(item => !string.IsNullOrWhiteSpace(item)));
        if (ContainsAny(value, "admin", "chat", "prompt", "agent"))
        {
            return NcfMascotKind.Cici;
        }

        if (ContainsAny(value, "bridge", "event", "sync", "总线", "同步"))
        {
            return NcfMascotKind.Qiao;
        }

        if (ContainsAny(value, "build", "publish", "deploy", "error", "failed", "构建", "发布", "错误", "失败"))
        {
            return NcfMascotKind.Opsi;
        }

        return NcfMascotKind.Nono;
    }

    internal static NcfMascotPose ResolvePose(string? state) => state switch
    {
        "Working" => NcfMascotPose.Working,
        "Succeeded" => NcfMascotPose.Success,
        "Failed" => NcfMascotPose.Warning,
        "Cancelled" => NcfMascotPose.Idle,
        _ => NcfMascotPose.Wave
    };

    private void SetMascot(NcfMascotKind mascot, NcfMascotPose pose)
    {
        _resolvedMascot = mascot;
        Mascot = _isMascotOverride ? _mascotOverride : mascot;
        Pose = pose;
        MascotName = _isMascotOverride
            ? string.Concat(_mascotOverride, "（手动）")
            : mascot.ToString();
    }

    private DispatcherTimer CreateInteractionTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(850) };
        timer.Tick += (_, _) =>
        {
            IsInteracting = false;
            timer.Stop();
        };
        return timer;
    }

    private static bool ContainsAny(string value, params string[] candidates)
    {
        return candidates.Any(candidate => value.Contains(candidate, StringComparison.OrdinalIgnoreCase));
    }

    private static void RunOnUi(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.UIThread.Post(action);
        }
    }
}
