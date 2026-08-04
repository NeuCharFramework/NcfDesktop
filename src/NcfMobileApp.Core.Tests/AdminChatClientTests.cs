using System.Net;
using System.Text;
using NcfMobileApp.Core.Services;

namespace NcfMobileApp.Core.Tests;

[TestClass]
public sealed class AdminChatClientTests
{
    [TestMethod]
    public async Task AuthenticateAndStream_UsesBearerTokenAndParsesEvents()
    {
        var handler = new StubHandler();
        using var httpClient = new HttpClient(handler);
        var client = new AdminChatClient(httpClient);

        var authentication = await client.AuthenticateAsync(
            "https://ncf.example.com",
            "admin",
            "password");
        var tokens = new StringBuilder();
        var result = await client.SendMessageStreamingAsync(
            "https://ncf.example.com",
            7,
            "你好",
            onToken: token => tokens.Append(token));

        Assert.AreEqual("admin", authentication.UserName);
        Assert.AreEqual("你好！", tokens.ToString());
        Assert.AreEqual("你好！", result.AssistantMessage?.Content);
        Assert.IsTrue(handler.StreamRequestHadBearerToken);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        public bool StreamRequestHadBearerToken { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (path.EndsWith(".LoginAsync", StringComparison.Ordinal))
            {
                return Json("""
                    {"success":true,"data":{"userName":"admin","token":"test-token","tokenExpiresUtc":"2099-01-01T00:00:00Z"}}
                    """);
            }

            if (path.EndsWith(".GetSessionListAsync", StringComparison.Ordinal))
            {
                return Json("""
                    {"success":true,"data":{"sessions":[{"id":7,"title":"测试","lastMessageTime":"2026-08-04T00:00:00Z"}]}}
                    """);
            }

            if (path.EndsWith("/AdminChatStream/send", StringComparison.Ordinal))
            {
                StreamRequestHadBearerToken = request.Headers.Authorization?.Parameter == "test-token";
                var sse = """
                    event: user-message
                    data: {"id":1,"sessionId":7,"roleType":0,"content":"你好","sequence":1,"addTime":"2026-08-04T00:00:00Z","modelIdentifier":null}

                    event: token
                    data: {"text":"你好"}

                    event: token
                    data: {"text":"！"}

                    event: assistant-message
                    data: {"id":2,"sessionId":7,"roleType":1,"content":"你好！","sequence":2,"addTime":"2026-08-04T00:00:01Z","modelIdentifier":"test"}

                    """;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static Task<HttpResponseMessage> Json(string json)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }
}
