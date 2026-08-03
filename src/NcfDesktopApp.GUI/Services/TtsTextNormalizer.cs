/*----------------------------------------------------------------
    Copyright (C) 2026 Senparc

    文件名：TtsTextNormalizer.cs
    文件功能描述：将 AdminChat Markdown 回复转换为适合朗读的分段纯文本

    创建标识：Senparc - 20260803

    修改标识：Senparc - 20260804
    修改描述：v0.6.0 增加 Markdown 清理与流式短句安全分段

----------------------------------------------------------------*/

using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace NcfDesktopApp.GUI.Services;

internal static partial class TtsTextNormalizer
{
    private const int PreferredChunkLength = 260;

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
            var sentenceEnd = character is '。' or '！' or '？' or '；' or '.' or '!' or '?' or ';';
            if ((sentenceEnd && current.Length >= 40) || current.Length >= PreferredChunkLength)
            {
                AddChunk(result, current);
            }
        }

        AddChunk(result, current);
        return result;
    }

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
    private const int MinimumStrongBoundaryLength = 8;
    private const int MinimumSoftBoundaryLength = 32;
    private const int MaximumChunkLength = 96;

    private readonly StringBuilder _receivedText = new();
    private readonly StringBuilder _pendingText = new();

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
            var cutIndex = FindSafeCutIndex(pending, isFinal);
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
            }
        }

        return result;
    }

    private static int FindSafeCutIndex(string text, bool isFinal)
    {
        var visibleLength = 0;
        var lastPreferredCut = 0;
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
                }

                continue;
            }

            if (index + 2 < text.Length &&
                text[index] == '`' && text[index + 1] == '`' && text[index + 2] == '`')
            {
                inCodeFence = !inCodeFence;
                index += 2;
                if (!inCodeFence)
                {
                    lastPreferredCut = index + 1;
                }

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
                    if (linkDestinationDepth == 0)
                    {
                        lastPreferredCut = index + 1;
                    }
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

            var strongBoundary = character is '。' or '！' or '？' or '；' or '.' or '!' or '?' or ';';
            if (markdownLabelDepth == 0 && strongBoundary && visibleLength >= MinimumStrongBoundaryLength)
            {
                return index + 1;
            }

            var softBoundary = character is '，' or ',' or '、' or '：' or ':' or '\n' or '\r';
            if (softBoundary || char.IsWhiteSpace(character))
            {
                lastPreferredCut = index + 1;
            }

            if (markdownLabelDepth == 0 && softBoundary && visibleLength >= MinimumSoftBoundaryLength)
            {
                return index + 1;
            }

            if (markdownLabelDepth == 0 && visibleLength >= MaximumChunkLength)
            {
                return lastPreferredCut > 0 ? lastPreferredCut : index + 1;
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
