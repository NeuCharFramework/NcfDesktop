using AVFoundation;

namespace NcfMobileApp.iOS.Services;

public sealed class SpeechPlaybackService : IDisposable
{
    private readonly AVSpeechSynthesizer _synthesizer = new();

    public void Speak(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        Stop();
        var utterance = new AVSpeechUtterance(text.Trim())
        {
            Voice = AVSpeechSynthesisVoice.FromLanguage("zh-CN"),
            Rate = AVSpeechUtterance.DefaultSpeechRate
        };
        _synthesizer.SpeakUtterance(utterance);
    }

    public void Stop()
    {
        if (_synthesizer.Speaking)
        {
            _synthesizer.StopSpeaking(AVSpeechBoundary.Immediate);
        }
    }

    public void Dispose()
    {
        Stop();
        _synthesizer.Dispose();
    }
}
