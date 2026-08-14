/*----------------------------------------------------------------
    Copyright (C) 2026 Senparc

    文件名：TtsTextNormalizer.cs
    文件功能描述：将 AdminChat Markdown 回复转换为适合朗读的分段纯文本

    创建标识：Senparc - 20260803

    修改标识：Senparc - 20260804
    修改描述：v0.6.0 增加 Markdown 清理与流式短句安全分段

    修改标识：Senparc - 20260815
    修改描述：v0.10.1 优化流式朗读首段响应与后续标点感知分段

----------------------------------------------------------------*/

using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace NcfDesktopApp.GUI.Services;

internal static partial class TtsTextNormalizer
{
    private const int FirstChunkMaximumLength = 260;
    private const int SubsequentChunkMaximumLength = 300;

    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var normalized = CodeBlockRegex().Replace(text, " 代码内容已省略。 ");
        normalized = MarkdownImageRegex().Replace(normalized, string.Empty);
        normalized = MarkdownLinkRegex().Replace(normalized, "$1");
        normalized = UrlRegex().Replace(normalized, string.Empty);
        normalized = HtmlTagRegex().Replace(normalized, " ");
        normalized = InlineMarkupRegex().Replace(normalized, string.Empty);
        normalized = ListMarkerRegex().Replace(normalized, "");
        normalized = WhitespaceRegex().Replace(normalized, " ").Trim();
        return normalized;
    }

    public static IReadOnlyList<string> SplitForSpeech(string? text)
    {
        var normalized = Normalize(text);
        if (normalized.Length == 0)
        {
            return Array.Empty<string>();
        }

        var result = new List<string>();
        var current = new StringBuilder();
        foreach (var character in normalized)
        {
            current.Append(character);
            var maximumLength = result.Count == 0
                ? FirstChunkMaximumLength
                : SubsequentChunkMaximumLength;
            if ((IsStrongSpeechBoundary(character) && current.Length >= 40) ||
                current.Length >= maximumLength)
            {
                AddChunk(result, current);
            }
        }

        AddChunk(result, current);
        return result;
    }

    /// <summary>
    /// 强边界可以作为一句话的自然结束；流式与完整文本分段共用同一规则，
    /// 避免两条朗读路径对同一标点产生不同的断句结果。
    /// </summary>
    internal static bool IsStrongSpeechBoundary(char character) =>
        character is '。' or '！' or '？' or '；' or '.' or '!' or '?' or ';';

    internal static bool IsSoftSpeechBoundary(char character) =>
        character is '，' or ',' or '、' or '：' or ':' or '\n' or '\r';

    private static void AddChunk(List<string> result, StringBuilder current)
    {
        var chunk = current.ToString().Trim();
        current.Clear();
        if (chunk.Length > 0)
        {
            result.Add(chunk);
        }
    }

    [GeneratedRegex(@"```[\s\S]*?```", RegexOptions.CultureInvariant)]
    private static partial Regex CodeBlockRegex();

    [GeneratedRegex(@"!\[[^\]]*\]\([^\)]*\)", RegexOptions.CultureInvariant)]
    private static partial Regex MarkdownImageRegex();

    [GeneratedRegex(@"\[([^\]]+)\]\([^\)]*\)", RegexOptions.CultureInvariant)]
    private static partial Regex MarkdownLinkRegex();

    [GeneratedRegex(@"https?://\S+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlRegex();

    [GeneratedRegex(@"<[^>]+>", RegexOptions.CultureInvariant)]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex(@"[`*_~>#]", RegexOptions.CultureInvariant)]
    private static partial Regex InlineMarkupRegex();

    [GeneratedRegex(@"(?m)^\s*(?:[-+]\s+|\d+[\.、]\s*)", RegexOptions.CultureInvariant)]
    private static partial Regex ListMarkerRegex();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();
}

/// <summary>
/// 将持续到达的 Markdown token 保留到安全的语句边界，再交给本地 TTS。
/// 该类型由单个 AdminChat 流拥有，调用方负责串行访问。
/// </summary>
internal sealed class StreamingTtsTextBuffer
{
    private const int FirstChunkMinimumStrongBoundaryLength = 2;
    private const int FirstChunkMinimumSoftBoundaryLength = 24;
    private const int SubsequentMinimumStrongBoundaryLength = 8;
    private const int SubsequentMinimumSoftBoundaryLength = 32;
    private const int FirstChunkMaximumLength = 96;
    private const int SubsequentPreferredChunkLength = 96;
    private const int MaximumExtendedChunkLength = 128;

    private readonly StringBuilder _receivedText = new();
    private readonly StringBuilder _pendingText = new();
    private bool _hasEmittedSpeechChunk;

    public IReadOnlyList<string> Append(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return Array.Empty<string>();
        }

        _receivedText.Append(text);
        _pendingText.Append(text);
        return ExtractCompletedChunks(isFinal: false);
    }

    public IReadOnlyList<string> Complete(string? finalText)
    {
        AppendMissingFinalSuffix(finalText);
        return ExtractCompletedChunks(isFinal: true);
    }

    private void AppendMissingFinalSuffix(string? finalText)
    {
        if (string.IsNullOrEmpty(finalText))
        {
            return;
        }

        var received = _receivedText.ToString();
        if (received.Length == 0)
        {
            _receivedText.Append(finalText);
            _pendingText.Append(finalText);
            return;
        }

        if (finalText.StartsWith(received, StringComparison.Ordinal))
        {
            var suffix = finalText[received.Length..];
            _receivedText.Append(suffix);
            _pendingText.Append(suffix);
        }
    }

    private IReadOnlyList<string> ExtractCompletedChunks(bool isFinal)
    {
        var result = new List<string>();
        while (_pendingText.Length > 0)
        {
            var pending = _pendingText.ToString();
            var cutIndex = FindSafeCutIndex(pending, isFinal, !_hasEmittedSpeechChunk);
            if (cutIndex <= 0)
            {
                break;
            }

            var rawChunk = pending[..cutIndex];
            _pendingText.Remove(0, cutIndex);
            if (isFinal)
            {
                rawChunk = MaskUnclosedCodeFence(rawChunk);
            }

            var normalized = TtsTextNormalizer.Normalize(rawChunk);
            if (normalized.Length > 0)
            {
                result.Add(normalized);
                _hasEmittedSpeechChunk = true;
            }
        }

        return result;
    }

    private static int FindSafeCutIndex(string text, bool isFinal, bool isFirstChunk)
    {
        var minimumStrongBoundaryLength = isFirstChunk
            ? FirstChunkMinimumStrongBoundaryLength
            : SubsequentMinimumStrongBoundaryLength;
        var minimumSoftBoundaryLength = isFirstChunk
            ? FirstChunkMinimumSoftBoundaryLength
            : SubsequentMinimumSoftBoundaryLength;
        var maximumChunkLength = isFirstChunk
            ? FirstChunkMaximumLength
            : MaximumExtendedChunkLength;
        var preferredFallbackLength = isFirstChunk
            ? FirstChunkMaximumLength - 24
            : SubsequentPreferredChunkLength;
        var visibleLength = 0;
        var lastPreferredCut = 0;
        var lastPreferredCutVisibleLength = 0;
        var inCodeFence = false;
        var inHtmlTag = false;
        var inBareUrl = false;
        var markdownLabelDepth = 0;
        var linkDestinationDepth = 0;

        for (var index = 0; index < text.Length; index++)
        {
            if (!inCodeFence && !inBareUrl && linkDestinationDepth == 0 &&
                StartsWithUrl(text, index))
            {
                inBareUrl = true;
            }

            if (inBareUrl)
            {
                if (char.IsWhiteSpace(text[index]))
                {
                    inBareUrl = false;
                    lastPreferredCut = index + 1;
                    lastPreferredCutVisibleLength = visibleLength;
                }

                continue;
            }

            if (index + 2 < text.Length &&
                text[index] == '`' && text[index + 1] == '`' && text[index + 2] == '`')
            {
                inCodeFence = !inCodeFence;
                index += 2;
                continue;
            }

            if (inCodeFence)
            {
                continue;
            }

            if (linkDestinationDepth > 0)
            {
                if (text[index] == '(')
                {
                    linkDestinationDepth++;
                }
                else if (text[index] == ')')
                {
                    linkDestinationDepth--;
                }

                continue;
            }

            if (text[index] == '[')
            {
                markdownLabelDepth++;
            }
            else if (text[index] == ']' && markdownLabelDepth > 0)
            {
                markdownLabelDepth--;
            }

            if (index > 0 && text[index - 1] == ']' && text[index] == '(')
            {
                linkDestinationDepth = 1;
                continue;
            }

            if (inHtmlTag)
            {
                if (text[index] == '>')
                {
                    inHtmlTag = false;
                }

                continue;
            }

            if (text[index] == '<')
            {
                inHtmlTag = true;
                continue;
            }

            var character = text[index];
            if (!char.IsWhiteSpace(character) && character is not '`' and not '*' and not '_' and not '~' and not '#' and not '>')
            {
                visibleLength++;
            }

            var strongBoundary = TtsTextNormalizer.IsStrongSpeechBoundary(character);
            if (markdownLabelDepth == 0 && strongBoundary && visibleLength >= minimumStrongBoundaryLength)
            {
                return index + 1;
            }

            var softBoundary = TtsTextNormalizer.IsSoftSpeechBoundary(character);
            if (markdownLabelDepth == 0 && (softBoundary || char.IsWhiteSpace(character)))
            {
                lastPreferredCut = index + 1;
                lastPreferredCutVisibleLength = visibleLength;
            }

            if (markdownLabelDepth == 0 && softBoundary && visibleLength >= minimumSoftBoundaryLength)
            {
                return index + 1;
            }

            if (markdownLabelDepth == 0 && visibleLength >= maximumChunkLength)
            {
                // 首段保持 96 字上限，使第一段音频尽快进入播放队列。
                // 后续段的 96 字只是期望值：继续等待标点至 128 字，再强制截断，
                // 从而改善标点恰好比旧上限晚几个 token 到达时的句中断句。
                return lastPreferredCutVisibleLength >= preferredFallbackLength
                    ? lastPreferredCut
                    : index + 1;
            }
        }

        return isFinal ? text.Length : 0;
    }

    private static string MaskUnclosedCodeFence(string text)
    {
        var openFenceIndex = -1;
        for (var index = 0; index + 2 < text.Length; index++)
        {
            if (text[index] != '`' || text[index + 1] != '`' || text[index + 2] != '`')
            {
                continue;
            }

            openFenceIndex = openFenceIndex < 0 ? index : -1;
            index += 2;
        }

        return openFenceIndex < 0
            ? text
            : $"{text[..openFenceIndex]} 代码内容已省略。";
    }

    private static bool StartsWithUrl(string text, int index) =>
        text.AsSpan(index).StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
        text.AsSpan(index).StartsWith("http://", StringComparison.OrdinalIgnoreCase);
}
