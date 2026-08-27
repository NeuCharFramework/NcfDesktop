/*----------------------------------------------------------------
    Copyright (C) 2026 Senparc

    文件名：DesktopSettingsStore.cs
    文件功能描述：读取、规范化并持久化桌面应用用户设置

    创建标识：Senparc - 20260802

    修改标识：Senparc - 20260804
    修改描述：v0.6.0 持久化语音自动发送及桌面机器人布局设置

    修改标识：Senparc - 20260812
    修改描述：v0.10.0 完善桌面端唤醒词会话激活与中英文提示

    修改标识：Senparc - 20260826
    修改描述：v0.11.0 持久化并规范化自定义唤醒词列表

----------------------------------------------------------------*/

using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using NcfDesktopApp.GUI.Models;

namespace NcfDesktopApp.GUI.Services;

/// <summary>
/// 将桌面用户设置读写至 AppData 目录下的 JSON 文件。
/// </summary>
public static class DesktopSettingsStore
{
    private const string FileName = "desktop-user-settings.json";
    private static readonly object SyncRoot = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string SettingsFilePath => Path.Combine(NcfService.AppDataPath, FileName);

    public static string NormalizeMirrorServerBase(string? url)
    {
        var s = (url ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(s))
        {
            return DesktopUserSettings.DefaultMirrorServerBaseUrl.TrimEnd('/');
        }

        return s.TrimEnd('/');
    }

    public static DesktopUserSettings Load()
    {
        lock (SyncRoot)
        {
            try
            {
                var path = SettingsFilePath;
                if (!File.Exists(path))
                {
                    return new DesktopUserSettings();
                }

                var json = File.ReadAllText(path);
                var loaded = JsonSerializer.Deserialize<DesktopUserSettings>(json, JsonOptions);
                return loaded ?? new DesktopUserSettings();
            }
            catch
            {
                return new DesktopUserSettings();
            }
        }
    }

    public static void Save(DesktopUserSettings settings)
    {
        if (settings == null)
        {
            return;
        }

        lock (SyncRoot)
        {
            Directory.CreateDirectory(NcfService.AppDataPath);
            var normalized = NormalizeMirrorServerBase(settings.MirrorServerBaseUrl);
            var environment = string.Equals(settings.AspNetCoreEnvironment, "Development", System.StringComparison.OrdinalIgnoreCase)
                ? "Development"
                : "Production";
            var startPort = Math.Clamp(settings.StartPort, 1024, 65535);
            var endPort = Math.Clamp(settings.EndPort, startPort, 65535);
            var existingRecentPaths = new System.Collections.Generic.List<string>();
            try
            {
                if (File.Exists(SettingsFilePath))
                {
                    existingRecentPaths = JsonSerializer
                        .Deserialize<DesktopUserSettings>(File.ReadAllText(SettingsFilePath), JsonOptions)?
                        .RecentNcfPaths ?? new();
                }
            }
            catch
            {
                // 损坏的旧设置不妨碍写入新的有效设置。
            }

            var recentPaths = (settings.RecentNcfPaths ?? new())
                .Concat(existingRecentPaths)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => path.Trim())
                .Distinct(System.StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .ToList();
            var toWrite = new DesktopUserSettings
            {
                MirrorServerBaseUrl = normalized,
                AutoOpenBrowser = settings.AutoOpenBrowser,
                AutoCleanDownloads = settings.AutoCleanDownloads,
                ShowDetailedInfo = settings.ShowDetailedInfo,
                UiLanguage = LocalizationService.NormalizeLanguage(settings.UiLanguage),
                StartPort = startPort,
                EndPort = endPort,
                LaunchTargetKind = settings.LaunchTargetKind,
                ExternalNcfPath = settings.ExternalNcfPath?.Trim() ?? string.Empty,
                RemoteSiteUrl = settings.RemoteSiteUrl?.Trim() ?? string.Empty,
                TemplateWorkspaceParentPath = settings.TemplateWorkspaceParentPath?.Trim() ?? string.Empty,
                TemplateWorkspaceConfigurationSourceKind = Enum.IsDefined(
                    typeof(TemplateWorkspaceConfigurationSourceKind),
                    settings.TemplateWorkspaceConfigurationSourceKind)
                    ? settings.TemplateWorkspaceConfigurationSourceKind
                    : TemplateWorkspaceConfigurationSourceKind.TemplateDefault,
                TemplateWorkspaceConfigurationSourcePath =
                    settings.TemplateWorkspaceConfigurationSourcePath?.Trim() ?? string.Empty,
                RecentNcfPaths = recentPaths,
                AspNetCoreEnvironment = environment,
                VoiceModelId = settings.VoiceModelId?.Trim() ?? string.Empty,
                VoiceCustomModelPath = settings.VoiceCustomModelPath?.Trim() ?? string.Empty,
                VoiceLanguage = NormalizeVoiceLanguage(settings.VoiceLanguage),
                SttAutoSend = settings.SttAutoSend,
                WakeWordEnabled = settings.WakeWordEnabled,
                WakeWords = NormalizeWakeWords(settings.WakeWords),
                TtsModelId = settings.TtsModelId?.Trim() ?? string.Empty,
                TtsCustomModelPath = settings.TtsCustomModelPath?.Trim() ?? string.Empty,
                TtsSpeakerId = Math.Clamp(settings.TtsSpeakerId, 0, 1024),
                TtsSpeed = Math.Clamp(settings.TtsSpeed, 0.5, 2.0),
                TtsAutoRead = settings.TtsAutoRead,
                DesktopRobotWheelZoomEnabled = settings.DesktopRobotWheelZoomEnabled,
                DesktopRobotLayoutMode = DesktopRobotLayoutModePolicy.Normalize(
                    settings.DesktopRobotLayoutMode),
                DesktopRobotMaximumScale = DesktopRobotPlacementPolicy.NormalizeMaximumScale(
                    settings.DesktopRobotMaximumScale),
                DesktopRobotScale = DesktopRobotPlacementPolicy.NormalizeScale(
                    settings.DesktopRobotScale,
                    settings.DesktopRobotMaximumScale),
                DesktopRobotPositionX = settings.DesktopRobotPositionX,
                DesktopRobotPositionY = settings.DesktopRobotPositionY
            };
            var temporaryPath = $"{SettingsFilePath}.tmp.{Environment.ProcessId}";
            try
            {
                File.WriteAllText(temporaryPath, JsonSerializer.Serialize(toWrite, JsonOptions));
                File.Move(temporaryPath, SettingsFilePath, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }
    }

    private static string NormalizeVoiceLanguage(string? language)
    {
        return language?.Trim().ToLowerInvariant() switch
        {
            "zh" => "zh",
            "en" => "en",
            _ => "auto"
        };
    }

    private static System.Collections.Generic.List<WakeWordConfiguration> NormalizeWakeWords(
        System.Collections.Generic.IEnumerable<WakeWordConfiguration>? wakeWords)
    {
        var normalized = new System.Collections.Generic.List<WakeWordConfiguration>();
        var hasDefault = false;

        foreach (var source in wakeWords ?? Array.Empty<WakeWordConfiguration>())
        {
            if (source == null)
            {
                continue;
            }

            var action = Enum.IsDefined(typeof(WakeWordActionKind), source.Action)
                ? source.Action
                : WakeWordActionKind.ChatSession;
            var item = new WakeWordConfiguration(source.Id)
            {
                Phrase = source.Phrase?.Trim() ?? string.Empty,
                Pinyin = source.Pinyin?.Trim() ?? string.Empty,
                IsEnabled = source.IsEnabled,
                IsDefault = source.IsDefault && !hasDefault,
                Action = action,
                TargetSessionId = Math.Max(0, source.TargetSessionId),
                TargetSessionTitle = source.TargetSessionTitle?.Trim() ?? string.Empty
            };
            hasDefault |= item.IsDefault;
            normalized.Add(item);
        }

        if (normalized.Count > 0 && !hasDefault)
        {
            normalized[0].IsDefault = true;
        }

        return normalized;
    }
}
