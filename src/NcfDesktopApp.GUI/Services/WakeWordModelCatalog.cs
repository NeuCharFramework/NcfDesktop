/*----------------------------------------------------------------
    Copyright (C) 2026 Senparc

    文件名：WakeWordModelCatalog.cs
    文件功能描述：固定唤醒词模型目录、文件校验与低功耗监听策略

    创建标识：Senparc - 20260803

    修改标识：Senparc - 20260804
    修改描述：v0.6.0 增加固定唤醒词模型目录与完整性检查

----------------------------------------------------------------*/

using System;
using System.Collections.Generic;
using System.IO;

namespace NcfDesktopApp.GUI.Services;

internal static class WakeWordModelCatalog
{
    private const long KiB = 1024L;
    private const long MiB = 1024L * KiB;

    public const string WakePhraseDisplay = "你好 Cici";
    public const string WakePhrasePronunciation = "你好西西";
    public const string ModelId = "sherpa-onnx-kws-zipformer-wenetspeech-3.3M-2024-01-01-int8";
    public const string ArchiveFileName = "sherpa-onnx-kws-zipformer-wenetspeech-3.3M-2024-01-01.tar.bz2";
    public const string DownloadUrl =
        "https://github.com/k2-fsa/sherpa-onnx/releases/download/kws-models/" + ArchiveFileName;
    public const long ApproximateDownloadBytes = 32_654_866;
    public const string ArchiveSha256 = "b2f7c89690dc8ce4c6ed6afeab7cd800c36ad1421fb6b6302b4a4b194cf7f35f";

    // 官方源优先；当 GitHub 在当前网络不可达时，按顺序切换到 HTTPS 加速源。
    // 下载完成后必须同时通过固定大小、SHA-256、BZip2 文件头和模型文件校验。
    public static IReadOnlyList<WakeWordDownloadSource> DownloadSources { get; } =
    [
        new("GitHub 官方源", DownloadUrl),
        new("GitHub 加速源 1", "https://ghfast.top/" + DownloadUrl),
        new("GitHub 加速源 2", "https://gh-proxy.com/" + DownloadUrl)
    ];

    public const string EncoderFileName = "encoder-epoch-12-avg-2-chunk-16-left-64.int8.onnx";
    public const string DecoderFileName = "decoder-epoch-12-avg-2-chunk-16-left-64.int8.onnx";
    public const string JoinerFileName = "joiner-epoch-12-avg-2-chunk-16-left-64.int8.onnx";
    public const string TokensFileName = "tokens.txt";
    public const string KeywordsFileName = "keywords.txt";

    // 单个固定词表减少解码分支；较长的四音节短语也比二音节词更不易被日常对话误触发。
    public const string KeywordsFileContent = "n ǐ h ǎo x ī x ī :1.5 #0.35 @你好_Cici\n";

    public static string ModelsDirectory => Path.Combine(VoiceModelCatalog.ModelsDirectory, "WakeWord");

    public static string ModelDirectory => Path.Combine(ModelsDirectory, ModelId);

    public static WakeWordModelReadiness Evaluate() => EvaluateDirectory(ModelDirectory);

    public static WakeWordModelReadiness EvaluateDirectory(string directory)
    {
        var files = ResolveFiles(directory);
        if (files == null)
        {
            return new WakeWordModelReadiness(
                false,
                directory,
                null,
                $"固定唤醒词“{WakePhraseDisplay}”的轻量模型尚未下载。");
        }

        try
        {
            if (new FileInfo(files.Encoder).Length < 4 * MiB ||
                new FileInfo(files.Decoder).Length < 128 * KiB ||
                new FileInfo(files.Joiner).Length < 48 * KiB ||
                new FileInfo(files.Tokens).Length < 1 * KiB)
            {
                return new WakeWordModelReadiness(
                    false,
                    directory,
                    null,
                    "唤醒模型文件体积异常，可能下载或解压不完整，请重新下载。");
            }

            var keywords = File.ReadAllText(files.Keywords);
            if (!string.Equals(keywords, KeywordsFileContent, StringComparison.Ordinal))
            {
                return new WakeWordModelReadiness(
                    false,
                    directory,
                    null,
                    "唤醒词配置与当前应用版本不一致，请重新下载模型。");
            }

            return new WakeWordModelReadiness(
                true,
                directory,
                files,
                $"固定唤醒词“{WakePhraseDisplay}”（读作“{WakePhrasePronunciation}”）已就绪。");
        }
        catch (Exception ex)
        {
            return new WakeWordModelReadiness(
                false,
                directory,
                null,
                $"无法检查唤醒模型：{ex.Message}");
        }
    }

    public static WakeWordModelFiles? ResolveFiles(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return null;
        }

        var encoder = Path.Combine(directory, EncoderFileName);
        var decoder = Path.Combine(directory, DecoderFileName);
        var joiner = Path.Combine(directory, JoinerFileName);
        var tokens = Path.Combine(directory, TokensFileName);
        var keywords = Path.Combine(directory, KeywordsFileName);
        return File.Exists(encoder) &&
               File.Exists(decoder) &&
               File.Exists(joiner) &&
               File.Exists(tokens) &&
               File.Exists(keywords)
            ? new WakeWordModelFiles(directory, encoder, decoder, joiner, tokens, keywords)
            : null;
    }
}

internal static class WakeWordListeningPolicy
{
    public static bool ShouldListen(
        bool enabled,
        bool wakeModelReady,
        bool voiceModelReady,
        bool adminChatActive,
        bool adminChatBusy,
        bool voiceInputBusy,
        bool ttsPlaying,
        bool workspaceDisposed)
    {
        return enabled &&
               wakeModelReady &&
               voiceModelReady &&
               adminChatActive &&
               // 流式自动朗读会在回复尚未完全结束时播放；此时允许用户用唤醒词打断朗读。
               (!adminChatBusy || ttsPlaying) &&
               !voiceInputBusy &&
               !workspaceDisposed;
    }
}

internal sealed record WakeWordModelFiles(
    string RootDirectory,
    string Encoder,
    string Decoder,
    string Joiner,
    string Tokens,
    string Keywords);

internal sealed record WakeWordModelReadiness(
    bool IsReady,
    string ModelDirectory,
    WakeWordModelFiles? Files,
    string Message);

internal sealed record WakeWordDownloadSource(string DisplayName, string Url);

internal sealed record WakeWordDownloadProgress(
    string SourceName,
    long DownloadedBytes,
    long? TotalBytes,
    WakeWordDownloadStage Stage = WakeWordDownloadStage.Downloading,
    string? Detail = null);

internal enum WakeWordDownloadStage
{
    Connecting,
    Downloading,
    Validating,
    SourceFailed
}
