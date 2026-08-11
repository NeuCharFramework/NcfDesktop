/*----------------------------------------------------------------
    Copyright (C) 2026 Senparc

    文件名：NcfLaunchTarget.cs
    文件功能描述：NCF 桌面端启动目标模型


    创建标识：Senparc - 20260725

    修改标识：Senparc - 20260812
    修改描述：v0.10.0 完善桌面端唤醒词会话激活与中英文提示

----------------------------------------------------------------*/
using NcfDesktopApp.GUI.Services;

namespace NcfDesktopApp.GUI.Models;

/// <summary>
/// NCF 启动目标类型。托管目录由桌面端更新器维护，外部目标只负责校验和启动。
/// </summary>
public enum NcfLaunchTargetKind
{
    ManagedPublished,
    ExternalPublished,
    SourceProject,
    RemoteSite
}

/// <summary>
/// 已解析且可以启动的 NCF 目标。
/// </summary>
public sealed record NcfLaunchTarget(
    NcfLaunchTargetKind Kind,
    string SelectedPath,
    string WorkingDirectory,
    string EntryPath,
    string DisplayName,
    string Version,
    string TargetFramework)
{
    public bool IsManaged => Kind == NcfLaunchTargetKind.ManagedPublished;

    public bool IsSourceProject => Kind == NcfLaunchTargetKind.SourceProject;

    public bool IsRemoteSite => Kind == NcfLaunchTargetKind.RemoteSite;

    public string KindDisplayName => Kind switch
    {
        NcfLaunchTargetKind.ManagedPublished => LocalizationService.T("Target.ManagedPublished"),
        NcfLaunchTargetKind.ExternalPublished => LocalizationService.T("Target.ExternalPublished"),
        NcfLaunchTargetKind.SourceProject => LocalizationService.T("Target.SourceProject"),
        NcfLaunchTargetKind.RemoteSite => LocalizationService.T("Target.RemoteNcf"),
        _ => LocalizationService.T("Target.Generic")
    };
}

/// <summary>
/// 启动目标解析结果。失败时不抛出目录结构异常，便于直接在 UI 展示诊断信息。
/// </summary>
public sealed record NcfLaunchTargetResolution(NcfLaunchTarget? Target, string ErrorMessage)
{
    public bool IsValid => Target != null;

    public static NcfLaunchTargetResolution Success(NcfLaunchTarget target) => new(target, string.Empty);

    public static NcfLaunchTargetResolution Failure(string message) => new(null, message);
}
