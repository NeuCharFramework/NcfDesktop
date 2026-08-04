using NcfMobileApp.Core.Services;

namespace NcfMobileApp.Core.Tests;

[TestClass]
public sealed class SiteEndpointPolicyTests
{
    [TestMethod]
    public void Normalize_AcceptsHttpsAndRemovesQueryAndFragment()
    {
        var success = SiteEndpointPolicy.TryNormalizeSiteUrl(
            "https://ncf.example.com/root?secret=1#fragment",
            out var result,
            out var error);

        Assert.IsTrue(success, error);
        Assert.AreEqual("https://ncf.example.com/root/", result.AbsoluteUri);
    }

    [TestMethod]
    public void Normalize_RejectsRemoteHttpAndCredentials()
    {
        Assert.IsFalse(SiteEndpointPolicy.TryNormalizeSiteUrl(
            "http://ncf.example.com",
            out _,
            out var httpError));
        StringAssert.Contains(httpError, "HTTPS");

        Assert.IsFalse(SiteEndpointPolicy.TryNormalizeSiteUrl(
            "https://admin:secret@ncf.example.com",
            out _,
            out var credentialError));
        StringAssert.Contains(credentialError, "账号密码");
    }

    [TestMethod]
    public void CreateEndpoint_KeepsSameOrigin()
    {
        var success = SiteEndpointPolicy.TryCreateEndpoint(
            "https://ncf.example.com/nested/",
            "/api/chat",
            out var endpoint,
            out var error);

        Assert.IsTrue(success, error);
        Assert.AreEqual("https://ncf.example.com/api/chat", endpoint.AbsoluteUri);
    }
}
