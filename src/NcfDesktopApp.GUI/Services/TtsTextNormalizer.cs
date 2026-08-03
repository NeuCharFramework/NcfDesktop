/*----------------------------------------------------------------
    Copyright (C) 2026 Senparc

    文件名：TtsTextNormalizer.cs
    文件功能描述：将 AdminChat Markdown 回复转换为适合朗读的分段纯文本

    创建标识：Senparc - 20260803
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
