/*----------------------------------------------------------------
    Copyright (C) 2026 Senparc

    文件名：DesktopUpdateService.cs
    文件功能描述：检查 NCF Desktop 自身版本并提供官网更新入口


    创建标识：Senparc - 20260804

----------------------------------------------------------------*/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace NcfDesktopApp.GUI.Services;

public sealed class DesktopUpdateService
{
    public const string DownloadPageUrl = "https://www.ncf.pub/Download";
    public const string NcfReleaseEndpoint = "https://www.ncf.pub/NcfPackages/latest-desktop-release.json";
    public const string GitHubReleaseEndpoint = "https://api.github.com/repos/NeuCharFramework/NcfDesktop/releases/latest";

    private static readonly UpdateSource[] UpdateSources =
    [
        new("NCF 官网", NcfReleaseEndpoint),
        new("GitHub", GitHubReleaseEndpoint)
    ];

    private readonly HttpClient _httpClient;

    public DesktopUpdateService(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public static string GetCurrentVersion()
    {
        var assembly = typeof(DesktopUpdateService).Assembly;
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        if (TryParseVersion(informationalVersion, out var parsed))
        {
            return parsed.DisplayVersion;
        }

        var assemblyVersion = assembly.GetName().Version;
        return assemblyVersion == null
            ? "未知"
            : $"{assemblyVersion.Major}.{assemblyVersion.Minor}.{Math.Max(0, assemblyVersion.Build)}";
    }

    public async Task<DesktopUpdateCheckResult> CheckForUpdateAsync(
        string currentVersion,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseVersion(currentVersion, out var current))
        {
            throw new ArgumentException("当前桌面应用版本号无效。", nameof(currentVersion));
        }

        var failures = new List<Exception>();
        foreach (var source in UpdateSources)
        {
            try
            {
                var latest = await GetLatestVersionAsync(source, cancellationToken).ConfigureAwait(false);
                return new DesktopUpdateCheckResult(
                    current.DisplayVersion,
                    latest.DisplayVersion,
                    latest.CompareTo(current) > 0,
                    source.DisplayName);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                failures.Add(ex);
            }
        }

        throw new InvalidOperationException(
            "无法从 NCF 官网或 GitHub 获取 NCF Desktop 最新版本。",
            new AggregateException(failures));
    }

    private async Task<ParsedVersion> GetLatestVersionAsync(
        UpdateSource source,
        CancellationToken cancellationToken)
    {
        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(TimeSpan.FromSeconds(10));

        using var request = new HttpRequestMessage(HttpMethod.Get, source.Endpoint);
        request.Headers.UserAgent.ParseAdd("NCF-Desktop-Update-Check");

        using var response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCancellation.Token)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content
            .ReadAsStreamAsync(timeoutCancellation.Token)
            .ConfigureAwait(false);
        var release = await JsonSerializer
            .DeserializeAsync<DesktopReleaseEnvelope>(stream, cancellationToken: timeoutCancellation.Token)
            .ConfigureAwait(false);

        if (!TryParseVersion(release?.TagName, out var latest))
        {
            throw new InvalidOperationException($"{source.DisplayName} 返回了无效的桌面版本号。");
        }

        return latest;
    }

    private static bool TryParseVersion(string? value, out ParsedVersion parsed)
    {
        parsed = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var candidate = value.Trim();
        if (candidate.StartsWith("desktop-v", StringComparison.OrdinalIgnoreCase))
        {
            candidate = candidate["desktop-v".Length..];
        }
        else if (candidate.StartsWith('v'))
        {
            candidate = candidate[1..];
        }

        var metadataMarker = candidate.IndexOf('+');
        if (metadataMarker >= 0)
        {
            candidate = candidate[..metadataMarker];
        }

        var versionParts = candidate.Split('-', 2, StringSplitOptions.TrimEntries);
        var numericParts = versionParts[0].Split('.', StringSplitOptions.TrimEntries);
        if (numericParts.Length is < 2 or > 4 ||
            numericParts.Any(part => !int.TryParse(part, out var number) || number < 0))
        {
            return false;
        }

        var numbers = numericParts.Select(int.Parse).ToArray();
        var qualifier = versionParts.Length == 2 ? versionParts[1] : null;
        var hasBuildNumber = false;
        var buildNumber = 0;
        string? prerelease = null;
        if (!string.IsNullOrWhiteSpace(qualifier))
        {
            const string buildPrefix = "build";
            if (qualifier.StartsWith(buildPrefix, StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(qualifier[buildPrefix.Length..], out buildNumber) &&
                buildNumber >= 0)
            {
                hasBuildNumber = true;
            }
            else
            {
                prerelease = qualifier;
            }
        }

        parsed = new ParsedVersion(
            numbers.ElementAtOrDefault(0),
            numbers.ElementAtOrDefault(1),
            numbers.ElementAtOrDefault(2),
            numbers.ElementAtOrDefault(3),
            hasBuildNumber,
            buildNumber,
            prerelease,
            candidate);
        return true;
    }

    private sealed record UpdateSource(string DisplayName, string Endpoint);

    private sealed class DesktopReleaseEnvelope
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; init; }
    }

    private readonly record struct ParsedVersion(
        int Major,
        int Minor,
        int Patch,
        int Revision,
        bool HasBuildNumber,
        int BuildNumber,
        string? Prerelease,
        string DisplayVersion) : IComparable<ParsedVersion>
    {
        public int CompareTo(ParsedVersion other)
        {
            var numericComparison = Major.CompareTo(other.Major);
            if (numericComparison == 0) numericComparison = Minor.CompareTo(other.Minor);
            if (numericComparison == 0) numericComparison = Patch.CompareTo(other.Patch);
            if (numericComparison == 0) numericComparison = Revision.CompareTo(other.Revision);
            if (numericComparison != 0) return numericComparison;

            if (Prerelease == null && other.Prerelease != null) return 1;
            if (Prerelease != null && other.Prerelease == null) return -1;
            var prereleaseComparison = string.Compare(
                Prerelease,
                other.Prerelease,
                StringComparison.OrdinalIgnoreCase);
            if (prereleaseComparison != 0) return prereleaseComparison;

            if (HasBuildNumber && other.HasBuildNumber)
            {
                return BuildNumber.CompareTo(other.BuildNumber);
            }

            if (HasBuildNumber != other.HasBuildNumber)
            {
                return HasBuildNumber ? 1 : -1;
            }

            return 0;
        }
    }
}

public sealed record DesktopUpdateCheckResult(
    string CurrentVersion,
    string LatestVersion,
    bool IsUpdateAvailable,
    string SourceName);
