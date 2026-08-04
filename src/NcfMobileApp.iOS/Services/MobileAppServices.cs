using NcfMobileApp.Core.Services;

namespace NcfMobileApp.iOS.Services;

public sealed class MobileAppServices : IDisposable
{
    private readonly HttpClient _httpClient;

    public MobileAppServices()
    {
        _httpClient = new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("NCF-Mobile/0.1");
        AdminChat = new AdminChatClient(_httpClient);
        SpeechPlayback = new SpeechPlaybackService();
    }

    public AdminChatClient AdminChat { get; }

    public SpeechPlaybackService SpeechPlayback { get; }

    public void Dispose()
    {
        SpeechPlayback.Dispose();
        _httpClient.Dispose();
    }
}
