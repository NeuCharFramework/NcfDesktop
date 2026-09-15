/*----------------------------------------------------------------
    Copyright (C) 2026 Senparc
  
    文件名：DesktopUserSettings.cs
    文件功能描述：桌面应用用户设置模型
    
    
    创建标识：Senparc - 20260504
    
    修改标识：Senparc - 20260724
    修改描述：v0.1.0 增强更新源选择、下载反馈与桌面窗口兼容性

    修改标识：Senparc - 20260804
    修改描述：v0.6.0 扩展本地语音、朗读和桌面机器人用户设置

    修改标识：Senparc - 20260812
    修改描述：v0.10.0 完善桌面端唤醒词会话激活与中英文提示

    修改标识：Senparc - 20260826
    修改描述：v0.11.0 增加自定义唤醒词用户设置

    修改标识：Senparc - 20260828
    修改描述：v0.12.0 增加唤醒词模型标识用户设置

----------------------------------------------------------------*/
using System.Collections.Generic;
using NcfDesktopApp.GUI.Models;

namespace NcfDesktopApp.GUI.Services;

/// <summary>
/// 桌面端用户设置（持久化到 AppData 目录下的 JSON）。
/// </summary>
public sealed class DesktopUserSettings
{
    public const string DefaultMirrorServerBaseUrl = "https://www.ncf.pub";

    public bool AutoOpenBrowser { get; set; } = true;

    public bool AutoCleanDownloads { get; set; }

    public bool ShowDetailedInfo { get; set; } = true;

    /// <summary>
    /// 界面语言：zh 或 en。默认中文。
    /// </summary>
    public string UiLanguage { get; set; } = "zh";

    public int StartPort { get; set; } = 5000;

    public int EndPort { get; set; } = 5300;

    /// <summary>
    /// 镜像更新源站点根地址（不含路径）。实际请求元数据为 {此地址}/NcfPackages/latest-release.json。
    /// </summary>
    public string MirrorServerBaseUrl { get; set; } = DefaultMirrorServerBaseUrl;

    /// <summary>
    /// 当前工作模式。托管模式允许桌面端更新 Runtime，外部模式只校验和启动用户选择的目标。
    /// </summary>
    public NcfLaunchTargetKind LaunchTargetKind { get; set; } = NcfLaunchTargetKind.ManagedPublished;

    /// <summary>
    /// 最近选择的外部发布目录或源码工作区。
    /// </summary>
    public string ExternalNcfPath { get; set; } = string.Empty;

    /// <summary>
    /// 最近连接的远程 NCF 站点。DesktopBridge 令牌不会持久化。
    /// </summary>
    public string RemoteSiteUrl { get; set; } = string.Empty;

    /// <summary>
    /// 从 Senparc.NCF.Template 创建工作区时使用的父目录。
    /// </summary>
    public string TemplateWorkspaceParentPath { get; set; } = string.Empty;

    /// <summary>
    /// 新建模板工作区时使用模板默认配置、当前托管版本配置或其他工作区配置。
    /// </summary>
    public TemplateWorkspaceConfigurationSourceKind TemplateWorkspaceConfigurationSourceKind { get; set; } =
        TemplateWorkspaceConfigurationSourceKind.TemplateDefault;

    /// <summary>
    /// 最近选择的配置来源工作区。当前托管版本使用固定 Runtime 路径，不写入此字段。
    /// </summary>
    public string TemplateWorkspaceConfigurationSourcePath { get; set; } = string.Empty;

    /// <summary>
    /// 外部目标最近使用记录。
    /// </summary>
    public List<string> RecentNcfPaths { get; set; } = new();

    /// <summary>
    /// 启动 NCF 时使用的 ASP.NET Core 环境。
    /// </summary>
    public string AspNetCoreEnvironment { get; set; } = "Production";

    /// <summary>
    /// 已选择的桌面端离线语音模型标识。空值表示用户尚未选择。
    /// </summary>
    public string VoiceModelId { get; set; } = string.Empty;

    /// <summary>
    /// 手动加载的 Whisper GGML 模型路径；内置可下载模型不使用此字段。
    /// </summary>
    public string VoiceCustomModelPath { get; set; } = string.Empty;

    /// <summary>
    /// 语音识别语言：auto、zh 或 en。
    /// </summary>
    public string VoiceLanguage { get; set; } = "auto";

    /// <summary>
    /// STT 识别完成后是否直接发送到 AdminChat。默认关闭，保留用户确认识别结果的机会。
    /// </summary>
    public bool SttAutoSend { get; set; }

    /// <summary>
    /// 是否启用固定唤醒词监听。默认关闭，避免升级后在用户不知情时常驻麦克风。
    /// </summary>
    public bool WakeWordEnabled { get; set; }

    /// <summary>
    /// 当前使用的唤醒模型。默认保留原中文模型。
    /// </summary>
    public string WakeWordModelId { get; set; } = WakeWordModelCatalog.ChineseModelId;

    /// <summary>
    /// 自定义唤醒词及唤醒后要使用的 Chat 会话。
    /// </summary>
    public List<WakeWordConfiguration> WakeWords { get; set; } = new();

    /// <summary>
    /// 已选择的本地文本转语音模型标识。空值表示用户尚未选择。
    /// </summary>
    public string TtsModelId { get; set; } = string.Empty;

    /// <summary>
    /// 手动加载的 sherpa-onnx Kokoro 模型目录；内置可下载模型不使用此字段。
    /// </summary>
    public string TtsCustomModelPath { get; set; } = string.Empty;

    /// <summary>
    /// Kokoro 音色编号。
    /// </summary>
    public int TtsSpeakerId { get; set; } = 45;

    /// <summary>
    /// 文本转语音语速倍率。
    /// </summary>
    public double TtsSpeed { get; set; } = 1.0;

    /// <summary>
    /// AdminChat 回复完成后是否自动朗读。默认关闭，避免意外播放声音。
    /// </summary>
    public bool TtsAutoRead { get; set; }

    /// <summary>
    /// 鼠标位于桌面宠物上方时，是否允许使用滚轮调整浮窗大小。
    /// </summary>
    public bool DesktopRobotWheelZoomEnabled { get; set; }

    /// <summary>
    /// 多个工作区宠物保持独立浮动，或收纳到一个统一标签列表。
    /// </summary>
    public DesktopRobotLayoutMode DesktopRobotLayoutMode { get; set; } =
        DesktopRobotLayoutMode.FreeFloating;

    /// <summary>
    /// 桌面宠物当前缩放倍率。1.0 为产品默认大小，也是允许的下限。
    /// </summary>
    public double DesktopRobotScale { get; set; } = DesktopRobotPlacementPolicy.MinimumScale;

    /// <summary>
    /// 桌面宠物允许的最大缩放倍率。
    /// </summary>
    public double DesktopRobotMaximumScale { get; set; } = DesktopRobotPlacementPolicy.DefaultMaximumScale;

    /// <summary>
    /// 桌面宠物上次关闭时的屏幕像素坐标；空值表示尚未保存。
    /// </summary>
    public int? DesktopRobotPositionX { get; set; }

    public int? DesktopRobotPositionY { get; set; }
}
