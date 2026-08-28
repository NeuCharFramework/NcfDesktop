/*----------------------------------------------------------------
    Copyright (C) 2026 Senparc

    文件名：WakeWordModelCatalog.cs
    文件功能描述：固定唤醒词模型目录、文件校验与低功耗监听策略

    创建标识：Senparc - 20260803

    修改标识：Senparc - 20260804
    修改描述：v0.6.0 增加固定唤醒词模型目录与完整性检查

    修改标识：Senparc - 20260812
    修改描述：v0.10.0 完善桌面端唤醒词会话激活与中英文提示

    修改标识：Senparc - 20260826
    修改描述：v0.11.0 支持自定义唤醒短语拼音转换与模型目录

----------------------------------------------------------------*/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.IO;
using hyjiacan.py4n;
using NcfDesktopApp.GUI.Models;

namespace NcfDesktopApp.GUI.Services;

internal static class WakeWordModelCatalog
{
    private const long KiB = 1024L;
    private const long MiB = 1024L * KiB;

    public const string WakePhraseDisplay = "你好 Cici";
    public const string WakePhrasePronunciation = "你好西西";
    public const string WakePhrasePinyin = "nǐ hǎo xī xī";
    public const string ChineseModelId = "sherpa-onnx-kws-zipformer-wenetspeech-3.3M-2024-01-01-int8";
    public const string ChineseEnglishModelId = "sherpa-onnx-kws-zipformer-zh-en-3M-2025-12-20";
    public const string ModelId = ChineseModelId;
    public const string ArchiveFileName = "sherpa-onnx-kws-zipformer-wenetspeech-3.3M-2024-01-01.tar.bz2";
    public const string DownloadUrl =
        "https://github.com/k2-fsa/sherpa-onnx/releases/download/kws-models/" + ArchiveFileName;
    public const long ApproximateDownloadBytes = 32_654_866;
    public const string ArchiveSha256 = "b2f7c89690dc8ce4c6ed6afeab7cd800c36ad1421fb6b6302b4a4b194cf7f35f";

    private const string ChineseEnglishArchiveFileName =
        "sherpa-onnx-kws-zipformer-zh-en-3M-2025-12-20.tar.bz2";
    private const string ChineseEnglishDownloadUrl =
        "https://github.com/k2-fsa/sherpa-onnx/releases/download/kws-models/" +
        ChineseEnglishArchiveFileName;
    private const long ChineseEnglishApproximateDownloadBytes = 32_885_699;
    private const string ChineseEnglishArchiveSha256 =
        "68447f4fbc67e70eee3a93961f36e81e98f47aef73ce7e7ca00885c6cd3616a6";

    public static IReadOnlyList<WakeWordModelOption> Options { get; } =
    [
        new(
            ChineseModelId,
            WakeWordModelLanguage.Chinese,
            ChineseModelId,
            ArchiveFileName,
            DownloadUrl,
            ApproximateDownloadBytes,
            ArchiveSha256,
            "encoder-epoch-12-avg-2-chunk-16-left-64.int8.onnx",
            "decoder-epoch-12-avg-2-chunk-16-left-64.int8.onnx",
            "joiner-epoch-12-avg-2-chunk-16-left-64.int8.onnx",
            "tokens.txt",
            null),
        new(
            ChineseEnglishModelId,
            WakeWordModelLanguage.ChineseEnglish,
            ChineseEnglishModelId,
            ChineseEnglishArchiveFileName,
            ChineseEnglishDownloadUrl,
            ChineseEnglishApproximateDownloadBytes,
            ChineseEnglishArchiveSha256,
            "encoder-epoch-13-avg-2-chunk-16-left-64.int8.onnx",
            "decoder-epoch-13-avg-2-chunk-16-left-64.onnx",
            "joiner-epoch-13-avg-2-chunk-16-left-64.int8.onnx",
            "tokens.txt",
            "en.phone")
    ];

    public static WakeWordModelOption DefaultOption => Options[0];

    public static IReadOnlyList<WakeWordDownloadSource> DownloadSources =>
        GetDownloadSources(DefaultOption);

    public const string EncoderFileName = "encoder-epoch-12-avg-2-chunk-16-left-64.int8.onnx";
    public const string DecoderFileName = "decoder-epoch-12-avg-2-chunk-16-left-64.int8.onnx";
    public const string JoinerFileName = "joiner-epoch-12-avg-2-chunk-16-left-64.int8.onnx";
    public const string TokensFileName = "tokens.txt";
    public const string KeywordsFileName = "keywords.txt";

    // 单个固定词表减少解码分支；较长的四音节短语也比二音节词更不易被日常对话误触发。
    public const string KeywordsFileContent = "n ǐ h ǎo x ī x ī :1.5 #0.35 @你好_Cici\n";

    public static string ModelsDirectory => Path.Combine(VoiceModelCatalog.ModelsDirectory, "WakeWord");

    public static string ModelDirectory => GetModelDirectory(DefaultOption);

    public static WakeWordModelOption FindById(string? id) =>
        Options.FirstOrDefault(option =>
            string.Equals(option.Id, id?.Trim(), StringComparison.OrdinalIgnoreCase)) ??
        DefaultOption;

    public static string GetModelDirectory(WakeWordModelOption option) =>
        Path.Combine(ModelsDirectory, option.ModelDirectoryName);

    public static WakeWordModelReadiness Evaluate() => Evaluate(DefaultOption);

    public static WakeWordModelReadiness Evaluate(WakeWordModelOption? option)
    {
        var selected = option ?? DefaultOption;
        return EvaluateDirectory(GetModelDirectory(selected), selected);
    }

    public static WakeWordModelReadiness EvaluateDirectory(string directory)
    {
        return EvaluateDirectory(directory, DefaultOption);
    }

    public static WakeWordModelReadiness EvaluateDirectory(
        string directory,
        WakeWordModelOption option)
    {
        var files = ResolveFiles(directory, option);
        if (files == null)
        {
            return new WakeWordModelReadiness(
                false,
                directory,
                null,
                $"唤醒模型“{option.DisplayName}”尚未下载。");
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

            return new WakeWordModelReadiness(
                true,
                directory,
                files,
                "唤醒模型已就绪，可使用自定义唤醒词。");
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
        return ResolveFiles(directory, DefaultOption);
    }

    public static WakeWordModelFiles? ResolveFiles(
        string directory,
        WakeWordModelOption option)
    {
        if (!Directory.Exists(directory))
        {
            return null;
        }

        var encoder = Path.Combine(directory, option.EncoderFileName);
        var decoder = Path.Combine(directory, option.DecoderFileName);
        var joiner = Path.Combine(directory, option.JoinerFileName);
        var tokens = Path.Combine(directory, option.TokensFileName);
        var englishPhone = string.IsNullOrWhiteSpace(option.EnglishPhoneFileName)
            ? null
            : Path.Combine(directory, option.EnglishPhoneFileName);
        return File.Exists(encoder) &&
               File.Exists(decoder) &&
               File.Exists(joiner) &&
               File.Exists(tokens) &&
               (englishPhone == null || File.Exists(englishPhone))
            ? new WakeWordModelFiles(
                directory,
                encoder,
                decoder,
                joiner,
                tokens,
                string.Empty,
                new Dictionary<string, WakeWordKeywordDefinition>(StringComparer.OrdinalIgnoreCase)
                {
                    ["legacy"] = new("legacy", WakePhraseDisplay)
                },
                option.Language,
                englishPhone)
            : null;
    }

    public static IReadOnlyList<WakeWordDownloadSource> GetDownloadSources(
        WakeWordModelOption option)
    {
        return
        [
            new("GitHub 官方源", option.DownloadUrl),
            new("GitHub 加速源 1", $"https://ghfast.top/{option.DownloadUrl}"),
            new("GitHub 加速源 2", $"https://gh-proxy.com/{option.DownloadUrl}")
        ];
    }

    public static WakeWordConfiguration CreateDefaultConfiguration()
    {
        return new WakeWordConfiguration
        {
            Phrase = WakePhraseDisplay,
            Pinyin = WakePhrasePinyin,
            IsDefault = true,
            IsEnabled = true,
            Action = WakeWordActionKind.ChatSession
        };
    }

    public static WakeWordConfiguredModel BuildConfiguredFiles(
        string directory,
        IReadOnlyList<WakeWordConfiguration> configurations,
        string? activeKeywordsDirectory = null,
        WakeWordModelOption? option = null)
    {
        var selectedOption = option ?? DefaultOption;
        var baseFiles = ResolveFiles(directory, selectedOption);
        if (baseFiles == null)
        {
            return new WakeWordConfiguredModel(
                false,
                null,
                EvaluateDirectory(directory).Message,
                Array.Empty<WakeWordConfigurationValidation>());
        }

        var vocabulary = LoadVocabulary(baseFiles.Tokens);
        var lines = new List<string>();
        var keywordDefinitions = new Dictionary<string, WakeWordKeywordDefinition>(
            StringComparer.OrdinalIgnoreCase);
        var validations = new List<WakeWordConfigurationValidation>();
        var seenPhrases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var englishLexicon = baseFiles.EnglishPhoneLexicon == null
            ? null
            : LoadEnglishPhoneLexicon(baseFiles.EnglishPhoneLexicon);
        foreach (var configuration in configurations.Where(configuration => configuration != null))
        {
            var phrase = configuration.Phrase?.Trim() ?? string.Empty;
            if (!configuration.IsEnabled)
            {
                validations.Add(new WakeWordConfigurationValidation(
                    configuration.Id,
                    true,
                    "此唤醒词已关闭。",
                    configuration.Pinyin?.Trim() ?? string.Empty));
                continue;
            }

            if (phrase.Length == 0)
            {
                validations.Add(new WakeWordConfigurationValidation(
                    configuration.Id,
                    false,
                    "唤醒短语不能为空，已跳过该词条。",
                    string.Empty));
                continue;
            }

            if (!seenPhrases.Add(phrase))
            {
                validations.Add(new WakeWordConfigurationValidation(
                    configuration.Id,
                    false,
                    "与另一条已启用的唤醒短语重复，已跳过该词条。",
                    configuration.Pinyin?.Trim() ?? string.Empty));
                continue;
            }

            var alias = GetKeywordAlias(configuration);
            if (!TryBuildKeywordLine(
                    configuration,
                    alias,
                    vocabulary,
                    selectedOption,
                    englishLexicon,
                    out var line,
                    out var effectivePinyin,
                    out var error))
            {
                validations.Add(new WakeWordConfigurationValidation(
                    configuration.Id,
                    false,
                    error,
                    effectivePinyin));
                continue;
            }

            lines.Add(line);
            keywordDefinitions[alias] = new WakeWordKeywordDefinition(alias, phrase);
            validations.Add(new WakeWordConfigurationValidation(
                configuration.Id,
                true,
                "已加入本机唤醒监听。",
                effectivePinyin));
        }

        if (lines.Count == 0)
        {
            return new WakeWordConfiguredModel(
                false,
                null,
                "没有有效的唤醒词，请修正标记为错误的词条。",
                validations);
        }

        var content = string.Join(Environment.NewLine, lines) + Environment.NewLine;
        var fingerprint = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(content)))
            .ToLowerInvariant()[..16];
        var outputDirectory = string.IsNullOrWhiteSpace(activeKeywordsDirectory)
            ? ModelsDirectory
            : activeKeywordsDirectory;
        var keywordPath = Path.Combine(outputDirectory, $".keywords-{fingerprint}.txt");
        Directory.CreateDirectory(outputDirectory);
        File.WriteAllText(keywordPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        return new WakeWordConfiguredModel(
            true,
            baseFiles with
            {
                Keywords = keywordPath,
                KeywordDefinitions = keywordDefinitions
            },
            validations.Any(validation => !validation.IsValid)
                ? $"已加载 {keywordDefinitions.Count} 个唤醒词；无效词条已跳过，不影响其他唤醒词。"
                : $"已加载 {keywordDefinitions.Count} 个自定义唤醒词。",
            validations);
    }

    internal static bool TryGetSuggestedPinyin(
        string? phrase,
        out string pinyin,
        out string error) =>
        TryDeriveChinesePinyin(phrase?.Trim() ?? string.Empty, out pinyin, out error);

    internal static bool HasToneInformation(string? pinyin) =>
        !string.IsNullOrWhiteSpace(pinyin) && ContainsToneInformation(pinyin);

    private static bool TryBuildKeywordLine(
        WakeWordConfiguration configuration,
        string alias,
        IReadOnlySet<string> vocabulary,
        WakeWordModelOption option,
        IReadOnlyDictionary<string, string[]>? englishLexicon,
        out string line,
        out string effectivePinyin,
        out string error)
    {
        line = string.Empty;
        effectivePinyin = string.Empty;
        error = string.Empty;
        var phrase = configuration.Phrase.Trim();
        var pinyin = configuration.Pinyin?.Trim();
        if (option.SupportsEnglish && ContainsLatinLetters(phrase))
        {
            return TryBuildEnglishOrMixedKeywordLine(
                configuration,
                alias,
                phrase,
                pinyin,
                vocabulary,
                englishLexicon,
                out line,
                out effectivePinyin,
                out error);
        }

        if (!option.SupportsEnglish &&
            ContainsLatinLetters(phrase) &&
            string.IsNullOrWhiteSpace(pinyin))
        {
            error = "当前中文唤醒模型不支持英文，请在上方切换到“中文 + English”模型。";
            return false;
        }

        var canDerivePinyin = TryDeriveChinesePinyin(phrase, out var derivedPinyin, out var derivationError);
        if (string.IsNullOrWhiteSpace(pinyin))
        {
            if (!canDerivePinyin)
            {
                error = derivationError;
                return false;
            }

            pinyin = derivedPinyin;
        }
        else if (!ContainsToneInformation(pinyin) && canDerivePinyin)
        {
            // 中文短语的无声调手填拼音无法直接匹配声调词表，优先从原文推导。
            pinyin = derivedPinyin;
        }

        effectivePinyin = pinyin;
        var modelTokens = new List<string>();
        foreach (var syllable in pinyin
                     .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                     .Select(NormalizeManualPinyinSyllable))
        {
            if (!TryTokenizeSyllable(syllable, vocabulary, modelTokens))
            {
                effectivePinyin = string.Join(' ', pinyin
                    .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                    .Select(NormalizeManualPinyinSyllable));
                error = $"“{syllable}”不在当前唤醒模型词表中，请填写带声调拼音，例如 nǐ hǎo。";
                return false;
            }
        }

        if (modelTokens.Count == 0)
        {
            error = "没有生成有效的拼音音素。";
            return false;
        }

        effectivePinyin = string.Join(' ', pinyin
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(NormalizeManualPinyinSyllable));
        line = $"{string.Join(' ', modelTokens)} :1.5 #0.35 @{alias}";
        return true;
    }

    private static bool TryBuildEnglishOrMixedKeywordLine(
        WakeWordConfiguration configuration,
        string alias,
        string phrase,
        string? manualPhones,
        IReadOnlySet<string> vocabulary,
        IReadOnlyDictionary<string, string[]>? englishLexicon,
        out string line,
        out string effectivePinyin,
        out string error)
    {
        line = string.Empty;
        effectivePinyin = string.Empty;
        error = string.Empty;
        if (englishLexicon == null)
        {
            error = "当前模型没有英文发音词典，请重新下载中文 + English 模型。";
            return false;
        }

        var tokens = new List<string>();
        var effectiveParts = new List<string>();
        foreach (var part in phrase.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (ContainsHanCharacters(part))
            {
                if (!TryDeriveChinesePinyin(
                        part,
                        out var chinesePinyin,
                        out var chineseError))
                {
                    error = chineseError;
                    return false;
                }

                foreach (var syllable in chinesePinyin.Split(
                             (char[]?)null,
                             StringSplitOptions.RemoveEmptyEntries))
                {
                    var normalizedSyllable = NormalizeManualPinyinSyllable(syllable);
                    if (!TryTokenizeSyllable(normalizedSyllable, vocabulary, tokens))
                    {
                        error = $"中文音节“{normalizedSyllable}”不在双语模型词表中。";
                        return false;
                    }

                    effectiveParts.Add(normalizedSyllable);
                }

                continue;
            }

            var word = NormalizeEnglishWord(part);
            if (word.Length == 0)
            {
                continue;
            }

            if (!englishLexicon.TryGetValue(word, out var phones))
            {
                if (string.IsNullOrWhiteSpace(manualPhones))
                {
                    error = $"英文单词“{part}”不在本地发音词典中，请填写音素或换用常见英文短语。";
                    return false;
                }

                phones = manualPhones
                    .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                    .ToArray();
            }

            foreach (var phone in phones)
            {
                if (!vocabulary.Contains(phone))
                {
                    error = $"英文音素“{phone}”不在双语模型词表中。";
                    return false;
                }

                tokens.Add(phone);
            }

            effectiveParts.Add(string.Join(' ', phones));
        }

        if (tokens.Count == 0)
        {
            error = "没有生成有效的英文或中文唤醒音素。";
            return false;
        }

        effectivePinyin = string.Join(" | ", effectiveParts);
        line = $"{string.Join(' ', tokens)} @{alias}";
        return true;
    }

    private static Dictionary<string, string[]> LoadEnglishPhoneLexicon(string path)
    {
        var lexicon = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in File.ReadLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var parts = line.Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                continue;
            }

            var word = parts[0].ToUpperInvariant();
            var pronunciationIndex = word.IndexOf('(');
            if (pronunciationIndex > 0)
            {
                word = word[..pronunciationIndex];
            }

            if (!lexicon.ContainsKey(word))
            {
                lexicon[word] = parts[1..];
            }
        }

        return lexicon;
    }

    private static string NormalizeEnglishWord(string value)
    {
        var chars = value
            .Where(character => char.IsLetter(character) || character == '\'')
            .ToArray();
        return new string(chars).Trim('\'').ToUpperInvariant();
    }

    private static bool ContainsLatinLetters(string value) =>
        value.Any(character => character is >= 'A' and <= 'Z' or >= 'a' and <= 'z');

    private static bool ContainsHanCharacters(string value) =>
        value.Any(PinyinUtil.IsHanzi);

    private static bool TryDeriveChinesePinyin(
        string phrase,
        out string pinyin,
        out string error)
    {
        var syllables = new List<string>();
        foreach (var character in phrase)
        {
            if (char.IsWhiteSpace(character) || char.IsPunctuation(character))
            {
                continue;
            }

            if (!PinyinUtil.IsHanzi(character))
            {
                pinyin = string.Empty;
                error = "包含非中文字符，请在“拼音”字段填写对应的带声调拼音。";
                return false;
            }

            try
            {
                var generatedPinyin = Pinyin4Net.GetFirstPinyin(
                    character,
                    PinyinFormat.WITH_TONE_MARK |
                    PinyinFormat.LOWERCASE |
                    PinyinFormat.WITH_U_UNICODE);
                syllables.Add(NormalizeKwsToneMarks(generatedPinyin));
            }
            catch (Exception ex)
            {
                pinyin = string.Empty;
                error = $"字符“{character}”没有可用拼音：{ex.Message}";
                return false;
            }
        }

        pinyin = string.Join(' ', syllables);
        error = pinyin.Length == 0 ? "没有可转换的中文短语。" : string.Empty;
        return pinyin.Length > 0;
    }

    private static bool ContainsToneInformation(string pinyin)
    {
        return pinyin.Any(character =>
            character is >= '1' and <= '5' ||
            "āáǎàēéěèīíǐìōóǒòūúǔùǖǘǚǜ".Contains(character));
    }

    private static string NormalizeManualPinyinSyllable(string value)
    {
        var syllable = value.Trim().ToLowerInvariant();
        var normalized = syllable.Length > 0 &&
                         syllable[^1] is >= '1' and <= '5'
            ? PinyinUtil.Format(
                syllable,
                PinyinFormat.WITH_TONE_MARK |
                PinyinFormat.LOWERCASE |
                PinyinFormat.WITH_U_UNICODE)
            : syllable;
        return NormalizeKwsToneMarks(normalized);
    }

    private static string NormalizeKwsToneMarks(string pinyin)
    {
        return pinyin
            .Replace('ă', 'ǎ')
            .Replace('ĕ', 'ě')
            .Replace('ĭ', 'ǐ')
            .Replace('ŏ', 'ǒ')
            .Replace('ŭ', 'ǔ');
    }

    private static bool TryTokenizeSyllable(
        string syllable,
        IReadOnlySet<string> vocabulary,
        ICollection<string> output)
    {
        if (vocabulary.Contains(syllable))
        {
            output.Add(syllable);
            return true;
        }

        foreach (var initial in Initials)
        {
            if (!syllable.StartsWith(initial, StringComparison.Ordinal) ||
                syllable.Length == initial.Length)
            {
                continue;
            }

            var final = syllable[initial.Length..];
            if (!vocabulary.Contains(initial) || !vocabulary.Contains(final))
            {
                continue;
            }

            output.Add(initial);
            output.Add(final);
            return true;
        }

        return false;
    }

    private static HashSet<string> LoadVocabulary(string tokensPath)
    {
        var vocabulary = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(tokensPath))
        {
            var separator = line.LastIndexOf(' ');
            if (separator <= 0)
            {
                continue;
            }

            var token = line[..separator].Trim();
            if (token.Length > 0)
            {
                vocabulary.Add(token);
            }
        }

        return vocabulary;
    }

    internal static string GetKeywordAlias(WakeWordConfiguration configuration)
    {
        var id = string.IsNullOrWhiteSpace(configuration.Id)
            ? Guid.NewGuid().ToString("N")
            : configuration.Id.Trim();
        var safe = new string(id
            .Where(character => char.IsLetterOrDigit(character) || character == '_')
            .ToArray());
        return $"wake_{(safe.Length > 24 ? safe[..24] : safe)}";
    }

    private static readonly string[] Initials =
    [
        "zh", "ch", "sh",
        "b", "p", "m", "f", "d", "t", "n", "l", "g", "k", "h",
        "j", "q", "x", "r", "z", "c", "s", "y", "w"
    ];
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
               // Agent 回复期间仍保留唤醒监听；新的语音指令会在当前回复结束后排队发送。
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
    string Keywords,
    IReadOnlyDictionary<string, WakeWordKeywordDefinition>? KeywordDefinitions = null,
    WakeWordModelLanguage Language = WakeWordModelLanguage.Chinese,
    string? EnglishPhoneLexicon = null);

internal sealed record WakeWordKeywordDefinition(string Alias, string Phrase);

internal sealed record WakeWordConfiguredModel(
    bool IsReady,
    WakeWordModelFiles? Files,
    string Message,
    IReadOnlyList<WakeWordConfigurationValidation> Validations);

internal sealed record WakeWordConfigurationValidation(
    string ConfigurationId,
    bool IsValid,
    string Message,
    string EffectivePinyin);

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
