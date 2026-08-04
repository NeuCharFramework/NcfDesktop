/*----------------------------------------------------------------
    Copyright (C) 2026 Senparc

    文件名：TemplateWorkspaceConfigurationSourceKind.cs
    文件功能描述：新建模板工作区时的配置文件来源

    创建标识：Senparc - 20260805
----------------------------------------------------------------*/

namespace NcfDesktopApp.GUI.Models;

/// <summary>
/// 新建模板工作区时，appsettings.json、web.config 和
/// App_Data/Database/SenparcConfig.config 的来源。
/// </summary>
public enum TemplateWorkspaceConfigurationSourceKind
{
    TemplateDefault,
    ManagedRuntime,
    OtherWorkspace
}
