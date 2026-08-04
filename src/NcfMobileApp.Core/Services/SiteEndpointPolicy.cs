namespace NcfMobileApp.Core.Services;

public static class SiteEndpointPolicy
{
    public static bool TryNormalizeSiteUrl(string? siteUrl, out Uri siteUri, out string errorMessage)
    {
        siteUri = null!;
        errorMessage = string.Empty;

        if (!Uri.TryCreate(siteUrl?.Trim(), UriKind.Absolute, out var candidate) ||
            candidate.Scheme is not ("http" or "https") ||
            string.IsNullOrWhiteSpace(candidate.Host) ||
            !string.IsNullOrEmpty(candidate.UserInfo))
        {
            errorMessage = "站点地址必须是有效且不包含账号密码的 http:// 或 https:// URL。";
            return false;
        }

        if (candidate.Scheme == Uri.UriSchemeHttp && !IsLoopback(candidate))
        {
            errorMessage = "远程 NCF 站点必须使用 HTTPS；HTTP 仅允许 iOS 模拟器访问 localhost/回环地址。";
            return false;
        }

        var builder = new UriBuilder(candidate)
        {
            Path = candidate.AbsolutePath.TrimEnd('/') + "/",
            Query = string.Empty,
            Fragment = string.Empty
        };
        siteUri = builder.Uri;
        return true;
    }

    public static bool TryCreateEndpoint(
        string? siteUrl,
        string relativePath,
        out Uri endpoint,
        out string errorMessage)
    {
        endpoint = null!;
        if (!TryNormalizeSiteUrl(siteUrl, out var baseUri, out errorMessage))
        {
            return false;
        }

        if (!Uri.TryCreate(baseUri, relativePath, out var resolved) ||
            !HasSameOrigin(baseUri, resolved))
        {
            errorMessage = "NCF 接口地址无效或跳转到了其他站点。";
            return false;
        }

        endpoint = resolved;
        return true;
    }

    private static bool IsLoopback(Uri uri)
    {
        return uri.IsLoopback ||
               string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasSameOrigin(Uri left, Uri right)
    {
        return string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(left.Host, right.Host, StringComparison.OrdinalIgnoreCase) &&
               left.Port == right.Port;
    }
}
