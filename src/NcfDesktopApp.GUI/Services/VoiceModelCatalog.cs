/*----------------------------------------------------------------
    Copyright (C) 2026 Senparc

    文件名：VoiceModelCatalog.cs
    文件功能描述：本地 Whisper 模型目录、选择与完整性预检

    创建标识：Senparc - 20260801

    修改标识：Senparc - 20260808
    修改描述：v0.9.0 就绪文案支持界面语言

    修改标识：Senparc - 20260812
    修改描述：v0.10.0 完善桌面端唤醒词会话激活与中英文提示

----------------------------------------------------------------*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NcfDesktopApp.GUI.Models;
using Whisper.net.Ggml;

namespace NcfDesktopApp.GUI.Services;

internal static class VoiceModelCatalog
{
    private const long MiB = 1024L * 1024L;

    public const string CustomModelId = "custom";

    public static IReadOnlyList<VoiceModelOption> Options { get; } = new[]
    {
        new VoiceModelOption(
            "tiny",
            LocalVoiceModelKind.Tiny,
            "ggml-tiny.bin",
            75 * MiB,
            60 * MiB,
            true,
            75),
        new VoiceModelOption(
            "base",
            LocalVoiceModelKind.Base,
            "ggml-base.bin",
            142 * MiB,
            110 * MiB,
            true,
            142),
        new VoiceModelOption(
            "small",
            LocalVoiceModelKind.Small,
            "ggml-small.bin",
            466 * MiB,
            360 * MiB,
            true,
            466),
        new VoiceModelOption(
            CustomModelId,
            LocalVoiceModelKind.Custom,
            string.Empty,
            0,
            1 * MiB,
            false)
    };

    public static string ModelsDirectory => Path.Combine(NcfService.AppDataPath, "VoiceModels");

    public static VoiceModelOption? FindById(string? id)
    {
        return Options.FirstOrDefault(option =>
            string.Equals(option.Id, id?.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    public static string GetModelPath(VoiceModelOption option, string? customModelPath)
    {
        return option.Kind == LocalVoiceModelKind.Custom
            ? (customModelPath ?? string.Empty).Trim()
            : Path.Combine(ModelsDirectory, option.FileName);
    }

    public static VoiceModelReadiness Evaluate(
        VoiceModelOption? option,
        string? customModelPath)
    {
        if (option == null)
        {
            return new VoiceModelReadiness(
                VoiceModelReadinessState.NotSelected,
                string.Empty,
                LocalizationService.T("Voice.Ready.NotSelected"));
        }

        var path = GetModelPath(option, customModelPath);
        if (string.IsNullOrWhiteSpace(path))
        {
            return new VoiceModelReadiness(
                VoiceModelReadinessState.Missing,
                string.Empty,
                LocalizationService.T("Voice.Ready.NoCustomFile"));
        }

        return EvaluateFile(option, path);
    }

    public static VoiceModelReadiness EvaluateFile(VoiceModelOption option, string path)
    {
        if (!File.Exists(path))
        {
            var message = option.CanDownload
                ? LocalizationService.T("Voice.Ready.NotDownloaded", option.DisplayName)
                : LocalizationService.T("Voice.Ready.FileMissing");
            return new VoiceModelReadiness(VoiceModelReadinessState.Missing, path, message);
        }

        long length;
        try
        {
            length = new FileInfo(path).Length;
        }
        catch (Exception ex)
        {
            return new VoiceModelReadiness(
                VoiceModelReadinessState.Incomplete,
                path,
                LocalizationService.T("Voice.Ready.ReadFailed", ex.Message));
        }

        if (length < option.MinimumExpectedBytes)
        {
            return new VoiceModelReadiness(
                VoiceModelReadinessState.Incomplete,
                path,
                option.CanDownload
                    ? LocalizationService.T("Voice.Ready.IncompleteDownload")
                    : LocalizationService.T("Voice.Ready.FileTooSmall"));
        }

        return new VoiceModelReadiness(
            VoiceModelReadinessState.Ready,
            path,
            LocalizationService.T("Voice.Ready.Ready", option.DisplayName, FormatBytes(length)));
    }

    public static GgmlType GetGgmlType(VoiceModelOption option)
    {
        return option.Kind switch
        {
            LocalVoiceModelKind.Tiny => GgmlType.Tiny,
            LocalVoiceModelKind.Base => GgmlType.Base,
            LocalVoiceModelKind.Small => GgmlType.Small,
            _ => throw new InvalidOperationException("手动模型不支持自动下载。")
        };
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes < MiB)
        {
            return $"{bytes / 1024d:F1} KiB";
        }

        return $"{bytes / (double)MiB:F1} MiB";
    }
}
