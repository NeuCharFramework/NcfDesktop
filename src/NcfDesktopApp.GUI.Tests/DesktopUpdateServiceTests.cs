using System.Net;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NcfDesktopApp.GUI.Services;

namespace NcfDesktopApp.GUI.Tests;

[TestClass]
public sealed class DesktopUpdateServiceTests
{
    [TestMethod]
    public async Task CheckForUpdateAsync_WhenWebsiteHasNewerRelease_ReturnsUpdateAvailable()
    {
        var requests = new List<Uri>();
        using var httpClient = new HttpClient(new StubHttpMessageHandler(request =>
        {
            requests.Add(request.RequestUri!);
            return JsonResponse("desktop-v0.8.0-build10040");
        }));
        var service = new DesktopUpdateService(httpClient);

        var result = await service.CheckForUpdateAsync("0.7.0");

        Assert.IsTrue(result.IsUpdateAvailable);
        Assert.AreEqual("0.8.0-build10040", result.LatestVersion);
        Assert.AreEqual("NCF 官网", result.SourceName);
        Assert.AreEqual(1, requests.Count);
        Assert.AreEqual(DesktopUpdateService.NcfReleaseEndpoint, requests[0].AbsoluteUri);
    }

    [TestMethod]
    public async Task CheckForUpdateAsync_WhenBuildTagMatchesCurrentPackage_ReturnsCurrent()
    {
        using var httpClient = new HttpClient(new StubHttpMessageHandler(_ =>
            JsonResponse("desktop-v0.7.0-build10031")));
        var service = new DesktopUpdateService(httpClient);

        var result = await service.CheckForUpdateAsync("0.7.0-build10031+source-commit");

        Assert.IsFalse(result.IsUpdateAvailable);
        Assert.AreEqual("0.7.0-build10031", result.CurrentVersion);
        Assert.AreEqual("0.7.0-build10031", result.LatestVersion);
    }

    [TestMethod]
    public async Task CheckForUpdateAsync_WhenSameProductVersionHasNewerBuild_ReturnsUpdateAvailable()
    {
        using var httpClient = new HttpClient(new StubHttpMessageHandler(_ =>
            JsonResponse("desktop-v0.7.0-build10031")));
        var service = new DesktopUpdateService(httpClient);

        var result = await service.CheckForUpdateAsync("0.7.0-build10030");

        Assert.IsTrue(result.IsUpdateAvailable);
        Assert.AreEqual("0.7.0-build10031", result.LatestVersion);
    }

    [TestMethod]
    public async Task CheckForUpdateAsync_WhenWebsiteFails_FallsBackToGitHub()
    {
        var requestCount = 0;
        using var httpClient = new HttpClient(new StubHttpMessageHandler(_ =>
        {
            requestCount++;
            return requestCount == 1
                ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                : JsonResponse("desktop-v0.7.1-build10050");
        }));
        var service = new DesktopUpdateService(httpClient);

        var result = await service.CheckForUpdateAsync("0.7.0");

        Assert.IsTrue(result.IsUpdateAvailable);
        Assert.AreEqual("0.7.1-build10050", result.LatestVersion);
        Assert.AreEqual("GitHub", result.SourceName);
        Assert.AreEqual(2, requestCount);
    }

    [TestMethod]
    public async Task CheckForUpdateAsync_WhenAllSourcesFail_ThrowsClearError()
    {
        using var httpClient = new HttpClient(new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        var service = new DesktopUpdateService(httpClient);

        var exception = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            service.CheckForUpdateAsync("0.7.0"));

        StringAssert.Contains(exception.Message, "NCF 官网或 GitHub");
    }

    private static HttpResponseMessage JsonResponse(string tagName)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                $$"""{"tag_name":"{{tagName}}"}""",
                Encoding.UTF8,
                "application/json")
        };
    }

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(responseFactory(request));
        }
    }
}
