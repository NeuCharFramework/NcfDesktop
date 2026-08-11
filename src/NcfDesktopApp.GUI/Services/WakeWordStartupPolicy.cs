/*----------------------------------------------------------------
    Copyright (C) 2026 Senparc

    文件名：WakeWordStartupPolicy.cs
    文件功能描述：固定唤醒词在应用启动时的平台安全策略

    修改标识：Senparc - 20260812
    修改描述：v0.10.0 完善桌面端唤醒词会话激活与中英文提示

----------------------------------------------------------------*/

namespace NcfDesktopApp.GUI.Services;

/// <summary>
/// 将 macOS 首次唤醒监听的安全限制与实际监听条件分开，便于验证和维护。
/// </summary>
internal static class WakeWordStartupPolicy
{
    /// <summary>
    /// macOS 上已保存的开启状态不能在应用启动阶段直接重新申请/占用麦克风；
    /// 需要一次当前会话内的显式操作。Windows 和 Linux 没有此限制。
    /// </summary>
    public static bool RequiresCurrentSessionActivation(
        bool isMacOS,
        bool wakeWordEnabled,
        bool explicitlyActivatedThisSession) =>
        isMacOS && wakeWordEnabled && !explicitlyActivatedThisSession;
}
