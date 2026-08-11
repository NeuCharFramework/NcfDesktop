/*----------------------------------------------------------------
    Copyright (C) 2026 Senparc

    文件名：TtsModelCatalog.cs
    文件功能描述：离线 TTS 模型目录、体积备注、文件完整性与音色目录

    创建标识：Senparc - 20260803

    修改标识：Senparc - 20260804
    修改描述：v0.6.0 增加本地 TTS 模型下载与文件完整性检查

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

namespace NcfDesktopApp.GUI.Services;

internal static class TtsModelCatalog
{
    private const long MiB = 1024L * 1024L;
    public const string CustomModelId = "custom";

    public static IReadOnlyList<TtsModelOption> Options { get; } = new[]
    {
        new TtsModelOption(
            "kokoro-int8-v1.1",
            LocalTtsModelKind.Kokoro,
            "kokoro-int8-multi-lang-v1_1.tar.bz2",
            "https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/kokoro-int8-multi-lang-v1_1.tar.bz2",
            147_031_220,
            true,
            140),
        new TtsModelOption(
            "kokoro-full-v1.1",
            LocalTtsModelKind.Kokoro,
            "kokoro-multi-lang-v1_1.tar.bz2",
            "https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/kokoro-multi-lang-v1_1.tar.bz2",
            364_816_464,
            true,
            348),
        new TtsModelOption(
            CustomModelId,
            LocalTtsModelKind.Custom,
            string.Empty,
            string.Empty,
            0,
            false)
    };

    public static IReadOnlyList<TtsVoiceOption> Voices { get; } = new[]
    {
        new TtsVoiceOption(45),
        new TtsVoiceOption(3),
        new TtsVoiceOption(15),
        new TtsVoiceOption(30),
        new TtsVoiceOption(58),
        new TtsVoiceOption(70),
        new TtsVoiceOption(85),
        new TtsVoiceOption(100)
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
                LocalizationService.T("Tts.Ready.NotSelected"));
        }

        var directory = GetModelDirectory(option, customPath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return new TtsModelReadiness(
                TtsModelReadinessState.Missing,
                directory,
                option.CanDownload
                    ? LocalizationService.T("Tts.Ready.NotDownloaded", option.DisplayName)
                    : LocalizationService.T("Tts.Ready.NoCustomDirectory"));
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
                    LocalizationService.T("Tts.Ready.Incomplete"));
            }

            var modelLength = new FileInfo(files.Model).Length;
            var voicesLength = new FileInfo(files.Voices).Length;
            if (modelLength < 8 * MiB || voicesLength < 1 * MiB)
            {
                return new TtsModelReadiness(
                    TtsModelReadinessState.Incomplete,
                    directory,
                    LocalizationService.T("Tts.Ready.SizeAbnormal"));
            }

            return new TtsModelReadiness(
                TtsModelReadinessState.Ready,
                files.RootDirectory,
                LocalizationService.T("Tts.Ready.Ready", VoiceModelCatalog.FormatBytes(modelLength)),
                files);
        }
        catch (Exception ex)
        {
            return new TtsModelReadiness(
                TtsModelReadinessState.Incomplete,
                directory,
                LocalizationService.T("Tts.Ready.CheckFailed", ex.Message));
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
