/*----------------------------------------------------------------
    Copyright (C) 2026 Senparc

    文件名：NeuBellState.cs
    文件功能描述：纽铃聚合快照的桌面只读协议模型

    创建标识：Senparc - 20260807
----------------------------------------------------------------*/

using System;
using System.Collections.Generic;

namespace NcfDesktopApp.GUI.Models;

public sealed class NeuBellState
{
    public DateTimeOffset ServerTime { get; set; }

    public List<NeuBellProviderSnapshot> Providers { get; set; } = [];
}

public sealed class NeuBellProviderSnapshot
{
    public string ProviderId { get; set; } = string.Empty;

    public string ModuleUid { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string Icon { get; set; } = string.Empty;

    public bool DefaultVisible { get; set; }

    public List<NeuBellItemSnapshot> Items { get; set; } = [];
}

public sealed class NeuBellItemSnapshot
{
    public string Id { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Summary { get; set; } = string.Empty;

    public int Count { get; set; }

    public string Severity { get; set; } = string.Empty;

    public string DetailUrl { get; set; } = string.Empty;

    public DateTimeOffset UpdatedAt { get; set; }
}
