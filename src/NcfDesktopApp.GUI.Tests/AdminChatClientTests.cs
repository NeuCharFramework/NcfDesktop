using System.Net;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NcfDesktopApp.GUI.Models;
using NcfDesktopApp.GUI.Services;

namespace NcfDesktopApp.GUI.Tests;

[TestClass]
public sealed class AdminChatClientTests
{
    private const string SiteUrl = "http://localhost:5123";

    [TestMethod]
    public async Task AuthenticateAsync_UsesLoginThenAdminOnlyApi_AndKeepsOnlyToken()
    {
        var requests = new List<(string Path, string? Authorization, string Body)>();
        var requestIndex = 0;
        var client = CreateClient(request =>
        {
            requests.Add((
                request.RequestUri?.PathAndQuery ?? string.Empty,
                request.Headers.Authorization?.ToString(),
                request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty));

            return Interlocked.Increment(ref requestIndex) == 1
                ? JsonResponse("""
                    {
                      "success": true,
                      "data": {
                        "userName": "admin",
                        "token": "jwt-in-memory",
                        "tokenExpiresUtc": "2099-01-01T00:00:00Z"
                      }
                    }
                    """)
                : JsonResponse("""
                    {
                      "success": true,
                      "data": { "sessions": [], "totalCount": 0 }
                    }
                    """);
        });

        var result = await client.AuthenticateAsync(SiteUrl, "admin", "secret");

        Assert.AreEqual("admin", result.UserName);
        Assert.AreEqual("jwt-in-memory", client.Authentication?.AccessToken);
        Assert.IsTrue(client.IsAuthenticated);
        Assert.AreEqual(2, requests.Count);
        StringAssert.Contains(requests[0].Path, "AdminUserInfoAppService.LoginAsync");
        StringAssert.Contains(requests[0].Body, "secret");
        StringAssert.Contains(requests[1].Path, "AdminChatAppService.GetSessionListAsync");
        Assert.AreEqual("Bearer jwt-in-memory", requests[1].Authorization);
    }

    [TestMethod]
    public async Task AuthenticateAsync_WhenAdminOnlyCheckIsForbidden_ClearsAuthentication()
    {
        var requestIndex = 0;
        var client = CreateClient(_ => Interlocked.Increment(ref requestIndex) == 1
            ? JsonResponse("""
                {
                  "success": true,
                  "data": {
                    "userName": "limited-user",
                    "token": "limited-token",
                    "tokenExpiresUtc": "2099-01-01T00:00:00Z"
                  }
                }
                """)
            : new HttpResponseMessage(HttpStatusCode.Forbidden));

        var exception = await Assert.ThrowsExceptionAsync<AdminChatApiException>(
            () => client.AuthenticateAsync(SiteUrl, "limited-user", "secret"));

        Assert.IsTrue(exception.IsAuthenticationFailure);
        Assert.IsNull(client.Authentication);
        Assert.IsFalse(client.IsAuthenticated);
    }

    [TestMethod]
    public async Task AuthenticateAsync_WhenRemoteSiteUsesPlainHttp_DoesNotSendCredentials()
    {
        var requestSent = false;
        var client = CreateClient(_ =>
        {
            requestSent = true;
            return JsonResponse("{}");
        });

        await Assert.ThrowsExceptionAsync<AdminChatApiException>(
            () => client.AuthenticateAsync("http://example.com", "admin", "secret"));

        Assert.IsFalse(requestSent);
    }

    [TestMethod]
    public async Task AuthenticateWithAccessTokenAsync_ValidatesAdminOnlyWithoutPasswordLogin()
    {
        var paths = new List<string>();
        var client = CreateClient(request =>
        {
            paths.Add(request.RequestUri?.PathAndQuery ?? string.Empty);
            Assert.AreEqual("Bearer handoff-jwt", request.Headers.Authorization?.ToString());
            return EmptySessionsResponse();
        });

        var authentication = await client.AuthenticateWithAccessTokenAsync(
            SiteUrl,
            "admin",
            "handoff-jwt",
            DateTimeOffset.UtcNow.AddMinutes(10));

        Assert.AreEqual("admin", authentication.UserName);
        Assert.AreEqual("handoff-jwt", client.Authentication?.AccessToken);
        Assert.AreEqual(1, paths.Count);
        StringAssert.Contains(paths[0], "AdminChatAppService.GetSessionListAsync");
        Assert.IsFalse(paths.Any(path => path.Contains("LoginAsync", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task CreateSessionAsync_SendsSelectedModelAndModules()
    {
        var createBody = string.Empty;
        var client = CreateClient(request =>
        {
            var path = request.RequestUri?.PathAndQuery ?? string.Empty;
            if (path.Contains("AdminUserInfoAppService.LoginAsync", StringComparison.Ordinal))
            {
                return LoginResponse();
            }

            if (path.Contains("GetSessionListAsync", StringComparison.Ordinal))
            {
                return EmptySessionsResponse();
            }

            if (path.Contains("CreateSessionAsync", StringComparison.Ordinal))
            {
                createBody = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty;
                return JsonResponse("""
                    { "success": true, "data": { "sessionId": 42, "title": "新会话" } }
                    """);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        await client.AuthenticateAsync(SiteUrl, "admin", "secret");

        var sessionId = await client.CreateSessionAsync(
            SiteUrl,
            7,
            new[] { "module-a", "module-a", "module-b" });

        Assert.AreEqual(42, sessionId);
        StringAssert.Contains(createBody, "\"aiModelId\":7");
        StringAssert.Contains(createBody, "module-a");
        StringAssert.Contains(createBody, "module-b");
        Assert.AreEqual(1, createBody.Split("module-a", StringSplitOptions.None).Length - 1);
    }

    [TestMethod]
    public async Task SendMessageStreamingAsync_SendsSelectedModel()
    {
        var sendBody = string.Empty;
        var client = CreateClient(request =>
        {
            var path = request.RequestUri?.PathAndQuery ?? string.Empty;
            if (path.Contains("AdminUserInfoAppService.LoginAsync", StringComparison.Ordinal))
            {
                return LoginResponse();
            }

            if (path.Contains("GetSessionListAsync", StringComparison.Ordinal))
            {
                return EmptySessionsResponse();
            }

            if (path.Contains("/AdminChatStream/send", StringComparison.Ordinal))
            {
                sendBody = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                        event: user-message
                        data: {"id":1,"sessionId":12,"roleType":0,"content":"hello","sequence":1,"addTime":"2026-08-04T00:00:00Z","modelIdentifier":null}

                        event: assistant-message
                        data: {"id":2,"sessionId":12,"roleType":1,"content":"world","sequence":2,"addTime":"2026-08-04T00:00:01Z","modelIdentifier":"custom"}

                        """, Encoding.UTF8, "text/event-stream")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        await client.AuthenticateAsync(SiteUrl, "admin", "secret");

        var result = await client.SendMessageStreamingAsync(SiteUrl, 12, "hello", aiModelId: 7);

        Assert.AreEqual("hello", result.UserMessage?.Content);
        Assert.AreEqual("world", result.AssistantMessage?.Content);
        StringAssert.Contains(sendBody, "\"aiModelId\":7");
    }

    [TestMethod]
    public async Task GetOptionsAsync_ReadsModelsAndOpenModules()
    {
        var client = CreateClient(request =>
        {
            var path = request.RequestUri?.PathAndQuery ?? string.Empty;
            if (path.Contains("AdminUserInfoAppService.LoginAsync", StringComparison.Ordinal))
            {
                return LoginResponse();
            }

            if (path.Contains("GetSessionListAsync", StringComparison.Ordinal))
            {
                return EmptySessionsResponse();
            }

            if (path.Contains("GetAiModelOptionsAsync", StringComparison.Ordinal))
            {
                return JsonResponse("""
                    {
                      "success": true,
                      "data": {
                        "aiKernelAvailable": true,
                        "models": [
                          { "id": 9, "name": "Custom", "description": "custom model", "isDefault": false },
                          { "id": 0, "name": "Default", "description": "site model", "isDefault": true }
                        ]
                      }
                    }
                    """);
            }

            if (path.Contains("GetAvailableModulesAsync", StringComparison.Ordinal))
            {
                return JsonResponse("""
                    {
                      "success": true,
                      "data": {
                        "modules": [
                          {
                            "uid": "module-a",
                            "name": "Module.A",
                            "displayName": "模块 A",
                            "version": "1.2.3",
                            "description": "module description",
                            "icon": "fa fa-cube",
                            "isRequired": true
                          }
                        ]
                      }
                    }
                    """);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        await client.AuthenticateAsync(SiteUrl, "admin", "secret");

        var models = await client.GetAiModelOptionsAsync(SiteUrl);
        var modules = await client.GetAvailableModulesAsync(SiteUrl);

        Assert.AreEqual(0, models[0].Id);
        Assert.AreEqual(9, models[1].Id);
        Assert.AreEqual("module-a", modules.Single().Uid);
        Assert.IsTrue(modules.Single().IsRequired);
    }

    [TestMethod]
    public async Task DeleteAndSetModulesAsync_UseProtectedMutationEndpoints()
    {
        var requests = new List<(HttpMethod Method, string Path, string Body, string? Authorization)>();
        var client = CreateClient(request =>
        {
            var path = request.RequestUri?.PathAndQuery ?? string.Empty;
            if (path.Contains("AdminUserInfoAppService.LoginAsync", StringComparison.Ordinal))
            {
                return LoginResponse();
            }

            if (path.Contains("GetSessionListAsync", StringComparison.Ordinal))
            {
                return EmptySessionsResponse();
            }

            requests.Add((
                request.Method,
                path,
                request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty,
                request.Headers.Authorization?.ToString()));
            return JsonResponse("{ \"success\": true, \"data\": \"ok\" }");
        });
        await client.AuthenticateAsync(SiteUrl, "admin", "secret");

        await client.DeleteSessionAsync(SiteUrl, 12);
        await client.DeleteMessagesAsync(SiteUrl, 12, new[] { 3, 4, 4 });
        await client.SetModulesForSessionAsync(
            SiteUrl,
            12,
            new[] { new AdminChatAvailableModule("module-a", "Module.A", "模块 A", "1.0", "", "", false) });

        Assert.AreEqual(3, requests.Count);
        Assert.AreEqual(HttpMethod.Delete, requests[0].Method);
        StringAssert.Contains(requests[0].Path, "DeleteSessionAsync?sessionId=12");
        Assert.AreEqual(HttpMethod.Delete, requests[1].Method);
        StringAssert.Contains(requests[1].Path, "messageIds=3,4");
        Assert.AreEqual(HttpMethod.Post, requests[2].Method);
        StringAssert.Contains(requests[2].Path, "SetSessionModulesAsync");
        StringAssert.Contains(requests[2].Body, "module-a");
        Assert.IsTrue(requests.All(request => request.Authorization == "Bearer jwt-in-memory"));
    }

    private static AdminChatClient CreateClient(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
    {
        return new AdminChatClient(new HttpClient(new StubHttpMessageHandler(responseFactory))
        {
            Timeout = Timeout.InfiniteTimeSpan
        });
    }

    private static HttpResponseMessage JsonResponse(string json)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private static HttpResponseMessage LoginResponse()
    {
        return JsonResponse("""
            {
              "success": true,
              "data": {
                "userName": "admin",
                "token": "jwt-in-memory",
                "tokenExpiresUtc": "2099-01-01T00:00:00Z"
              }
            }
            """);
    }

    private static HttpResponseMessage EmptySessionsResponse()
    {
        return JsonResponse("""
            { "success": true, "data": { "sessions": [], "totalCount": 0 } }
            """);
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responseFactory;

        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        {
            _responseFactory = responseFactory;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(_responseFactory(request));
        }
    }
}
