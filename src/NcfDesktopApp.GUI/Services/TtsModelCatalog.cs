/*----------------------------------------------------------------
    Copyright (C) 2026 Senparc

    文件名：TtsModelCatalog.cs
    文件功能描述：离线 TTS 模型目录、体积备注、文件完整性与音色目录

    创建标识：Senparc - 20260803
----------------------------------------------------------------*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NcfDesktopApp.GUI.Models;

namespace NcfDesktopApp.GUI.Services;

internal static class TtsModelCatalog
{
    private const long MiB = 1024L * 1024L;
    public const string CustomModelId = "custom";

    public static IReadOnlyList<TtsModelOption> Options { get; } = new[]
    {
        new TtsModelOption(
            "kokoro-int8-v1.1",
            "Kokoro v1.1 INT8（中英·103 音色）",
            "推荐。自然度与速度均衡，CPU 可离线运行；下载约 140 MiB，解压占用以模型包为准。",
            "约 140 MiB",
            LocalTtsModelKind.Kokoro,
            "kokoro-int8-multi-lang-v1_1.tar.bz2",
            "https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/kokoro-int8-multi-lang-v1_1.tar.bz2",
            147_031_220,
            true),
        new TtsModelOption(
            "kokoro-full-v1.1",
            "Kokoro v1.1 完整版（中英·103 音色）",
            "高质量选项。未量化模型保留更多计算精度，但下载、加载和内存占用明显更高。",
            "约 348 MiB",
            LocalTtsModelKind.Kokoro,
            "kokoro-multi-lang-v1_1.tar.bz2",
            "https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/kokoro-multi-lang-v1_1.tar.bz2",
            364_816_464,
            true),
        new TtsModelOption(
            CustomModelId,
            "手动加载本地 Kokoro 模型目录",
            "选择已解压的 sherpa-onnx Kokoro 模型目录；目录需包含 ONNX、voices.bin、tokens.txt、词典与 espeak-ng-data。",
            "用户提供",
            LocalTtsModelKind.Custom,
            string.Empty,
            string.Empty,
            0,
            false)
    };

    public static IReadOnlyList<TtsVoiceOption> Voices { get; } = new[]
    {
        new TtsVoiceOption(45, "女声 zf_078（默认）", "Kokoro v1.1 中文女声，Speaker ID 45；建议试听后选择。"),
        new TtsVoiceOption(3, "女声 zf_001", "Kokoro v1.1 中文女声，Speaker ID 3。"),
        new TtsVoiceOption(15, "女声 zf_022", "Kokoro v1.1 中文女声，Speaker ID 15。"),
        new TtsVoiceOption(30, "女声 zf_047", "Kokoro v1.1 中文女声，Speaker ID 30。"),
        new TtsVoiceOption(58, "男声 zm_009", "Kokoro v1.1 中文男声，Speaker ID 58。"),
        new TtsVoiceOption(70, "男声 zm_031", "Kokoro v1.1 中文男声，Speaker ID 70。"),
        new TtsVoiceOption(85, "男声 zm_061", "Kokoro v1.1 中文男声，Speaker ID 85。"),
        new TtsVoiceOption(100, "男声 zm_097", "Kokoro v1.1 中文男声，Speaker ID 100。")
    };

    public static string ModelsDirectory => Path.Combine(NcfService.AppDataPath, "TtsModels");

    public static TtsModelOption? FindById(string? id) => Options.FirstOrDefault(option =>
        string.Equals(option.Id, id?.Trim(), StringComparison.OrdinalIgnoreCase));

    public static TtsVoiceOption FindVoice(int speakerId) =>
        Voices.FirstOrDefault(voice => voice.SpeakerId == speakerId) ?? Voices[0];

    public static string GetModelDirectory(TtsModelOption option, string? customPath)
    {
        return option.Kind == LocalTtsModelKind.Custom
            ? (customPath ?? string.Empty).Trim()
            : Path.Combine(ModelsDirectory, option.Id);
    }

    public static TtsModelReadiness Evaluate(TtsModelOption? option, string? customPath)
    {
        if (option == null)
        {
            return new TtsModelReadiness(
                TtsModelReadinessState.NotSelected,
                string.Empty,
                "尚未选择朗读模型。请先在“工作台设置 → 本地语音输出”中选择模型。");
        }

        var directory = GetModelDirectory(option, customPath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return new TtsModelReadiness(
                TtsModelReadinessState.Missing,
                directory,
                option.CanDownload
                    ? $"{option.DisplayName} 尚未下载，请点击“下载所选模型”。"
                    : "所选本地模型目录不存在，请重新选择。");
        }

        return EvaluateDirectory(directory);
    }

    public static TtsModelReadiness EvaluateDirectory(string directory)
    {
        try
        {
            var files = ResolveFiles(directory);
            if (files == null)
            {
                return new TtsModelReadiness(
                    TtsModelReadinessState.Incomplete,
                    directory,
                    "模型目录不完整，需要 ONNX、voices.bin、tokens.txt、词典和 espeak-ng-data。");
            }

            var modelLength = new FileInfo(files.Model).Length;
            var voicesLength = new FileInfo(files.Voices).Length;
            if (modelLength < 8 * MiB || voicesLength < 1 * MiB)
            {
                return new TtsModelReadiness(
                    TtsModelReadinessState.Incomplete,
                    directory,
                    "模型文件体积异常，可能下载或解压不完整。");
            }

            return new TtsModelReadiness(
                TtsModelReadinessState.Ready,
                files.RootDirectory,
                $"朗读模型已就绪（核心模型 {VoiceModelCatalog.FormatBytes(modelLength)}）。",
                files);
        }
        catch (Exception ex)
        {
            return new TtsModelReadiness(
                TtsModelReadinessState.Incomplete,
                directory,
                $"无法检查朗读模型：{ex.Message}");
        }
    }

    public static TtsModelFiles? ResolveFiles(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return null;
        }

        var model = Directory.EnumerateFiles(directory, "*.onnx", SearchOption.AllDirectories)
            .Where(path => Path.GetFileName(path).Contains("model", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(path => Path.GetFileName(path).Contains("int8", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();
        var voices = FindFile(directory, "voices.bin");
        var tokens = FindFile(directory, "tokens.txt");
        var dataDirectory = Directory.EnumerateDirectories(directory, "espeak-ng-data", SearchOption.AllDirectories)
            .FirstOrDefault();
        var lexicons = Directory.EnumerateFiles(directory, "lexicon*.txt", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (model == null || voices == null || tokens == null || dataDirectory == null || lexicons.Length == 0)
        {
            return null;
        }

        var ruleFsts = Directory.EnumerateFiles(directory, "*-zh.fst", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new TtsModelFiles(
            Path.GetDirectoryName(model) ?? directory,
            model,
            voices,
            tokens,
            dataDirectory,
            string.Join(',', lexicons),
            string.Join(',', ruleFsts));
    }

    private static string? FindFile(string directory, string fileName) =>
        Directory.EnumerateFiles(directory, fileName, SearchOption.AllDirectories).FirstOrDefault();
}
