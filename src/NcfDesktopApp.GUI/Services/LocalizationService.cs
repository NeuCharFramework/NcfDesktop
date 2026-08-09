/*----------------------------------------------------------------
    Copyright (C) 2026 Senparc

    文件名：LocalizationService.cs
    文件功能描述：桌面端中英界面文案加载、切换与查询

    创建标识：Senparc - 20260808

----------------------------------------------------------------*/

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Text.Json;
using Avalonia.Platform;

namespace NcfDesktopApp.GUI.Services;

/// <summary>
/// 界面语言本地化服务。默认中文；切换语言后通过绑定与事件刷新界面。
/// </summary>
public sealed class LocalizationService : INotifyPropertyChanged
{
    public const string Chinese = "zh";
    public const string English = "en";

    private static readonly Lazy<LocalizationService> LazyInstance = new(() => new LocalizationService());

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private readonly object _syncRoot = new();
    private Dictionary<string, string> _zh = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string> _en = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string> _current = new(StringComparer.OrdinalIgnoreCase);
    private string _language = Chinese;
    private bool _initialized;

    public static LocalizationService Instance => LazyInstance.Value;

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler? LanguageChanged;

    public string Language
    {
        get
        {
            lock (_syncRoot)
            {
                return _language;
            }
        }
    }

    public bool IsEnglish => string.Equals(Language, English, StringComparison.OrdinalIgnoreCase);

    public string this[string key] => Get(key);

    public static string NormalizeLanguage(string? language)
    {
        return string.Equals(language?.Trim(), English, StringComparison.OrdinalIgnoreCase)
            ? English
            : Chinese;
    }

    public void Initialize(string? language)
    {
        lock (_syncRoot)
        {
            _zh = LoadCatalog("zh-CN");
            _en = LoadCatalog("en-US");
            _current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _language = string.Empty;
            _initialized = true;
        }

        SetLanguage(language, raiseEvent: false);
    }

    /// <summary>
    /// 测试用：直接注入词条，避免依赖 Avalonia AssetLoader。
    /// </summary>
    internal void InitializeForTests(
        IDictionary<string, string> zh,
        IDictionary<string, string> en,
        string? language = Chinese)
    {
        lock (_syncRoot)
        {
            _zh = new Dictionary<string, string>(zh, StringComparer.OrdinalIgnoreCase);
            _en = new Dictionary<string, string>(en, StringComparer.OrdinalIgnoreCase);
            _current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _language = string.Empty;
            _initialized = true;
        }

        SetLanguage(language, raiseEvent: false);
    }

    public void SetLanguage(string? language, bool raiseEvent = true)
    {
        var normalized = NormalizeLanguage(language);
        lock (_syncRoot)
        {
            if (!_initialized)
            {
                _zh = LoadCatalog("zh-CN");
                _en = LoadCatalog("en-US");
                _initialized = true;
            }

            if (string.Equals(_language, normalized, StringComparison.OrdinalIgnoreCase) &&
                _current.Count > 0)
            {
                return;
            }

            _language = normalized;
            _current = normalized == English
                ? new Dictionary<string, string>(_en, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(_zh, StringComparer.OrdinalIgnoreCase);
        }

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        if (raiseEvent)
        {
            LanguageChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public string Get(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return string.Empty;
        }

        lock (_syncRoot)
        {
            EnsureInitializedUnlocked();
            if (_current.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value))
            {
                return value;
            }

            if (_zh.TryGetValue(key, out var zhValue) && !string.IsNullOrEmpty(zhValue))
            {
                System.Diagnostics.Debug.WriteLine($"[Localization] Missing key in '{_language}': {key}");
                return zhValue;
            }
        }

        System.Diagnostics.Debug.WriteLine($"[Localization] Missing key: {key}");
        return key;
    }

    public string Get(string key, params object[] args)
    {
        var format = Get(key);
        if (args == null || args.Length == 0)
        {
            return format;
        }

        try
        {
            return string.Format(format, args);
        }
        catch (FormatException)
        {
            return format;
        }
    }

    public static string T(string key) => Instance.Get(key);

    public static string T(string key, params object[] args) => Instance.Get(key, args);

    private void EnsureInitializedUnlocked()
    {
        if (_initialized)
        {
            return;
        }

        _zh = LoadCatalog("zh-CN");
        _en = LoadCatalog("en-US");
        _current = new Dictionary<string, string>(_zh, StringComparer.OrdinalIgnoreCase);
        _language = Chinese;
        _initialized = true;
    }

    private static Dictionary<string, string> LoadCatalog(string culture)
    {
        try
        {
            var uri = new Uri($"avares://NcfDesktopApp.GUI/Assets/Localization/{culture}.json");
            using var stream = AssetLoader.Open(uri);
            return DeserializeCatalog(stream);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Localization] AssetLoader failed for {culture}: {ex.Message}");
        }

        var fromFile = LoadCatalogFromFile(culture);
        if (fromFile.Count > 0)
        {
            return fromFile;
        }

        return LoadCatalogFromEmbeddedFallback(culture);
    }

    private static Dictionary<string, string> LoadCatalogFromFile(string culture)
    {
        foreach (var candidate in EnumerateCatalogFileCandidates(culture))
        {
            try
            {
                if (!File.Exists(candidate))
                {
                    continue;
                }

                using var stream = File.OpenRead(candidate);
                var loaded = DeserializeCatalog(stream);
                if (loaded.Count > 0)
                {
                    return loaded;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Localization] File load failed ({candidate}): {ex.Message}");
            }
        }

        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> EnumerateCatalogFileCandidates(string culture)
    {
        var fileName = $"{culture}.json";
        yield return Path.Combine(AppContext.BaseDirectory, "Assets", "Localization", fileName);

        var assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        if (!string.IsNullOrWhiteSpace(assemblyDir))
        {
            yield return Path.Combine(assemblyDir, "Assets", "Localization", fileName);

            var probe = new DirectoryInfo(assemblyDir);
            for (var i = 0; i < 8 && probe != null; i++, probe = probe.Parent)
            {
                yield return Path.Combine(probe.FullName, "Assets", "Localization", fileName);
                yield return Path.Combine(
                    probe.FullName,
                    "src",
                    "NcfDesktopApp.GUI",
                    "Assets",
                    "Localization",
                    fileName);
            }
        }
    }

    private static Dictionary<string, string> LoadCatalogFromEmbeddedFallback(string culture)
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            var resourceName = $"NcfDesktopApp.GUI.Assets.Localization.{culture}.json";
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null)
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            return DeserializeCatalog(stream);
        }
        catch
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static Dictionary<string, string> DeserializeCatalog(Stream stream)
    {
        using var reader = new StreamReader(stream);
        var json = reader.ReadToEnd();
        var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonOptions);
        return loaded == null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(loaded, StringComparer.OrdinalIgnoreCase);
    }
}
