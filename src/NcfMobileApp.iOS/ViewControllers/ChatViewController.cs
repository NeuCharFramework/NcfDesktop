using System.Text;
using NcfMobileApp.Core.Models;
using NcfMobileApp.iOS.Services;
using UIKit;

namespace NcfMobileApp.iOS.ViewControllers;

public sealed class ChatViewController : UIViewController
{
    private readonly MobileAppServices _services;
    private readonly string _siteUrl;
    private readonly UITextView _transcript = new()
    {
        Editable = false,
        Selectable = true,
        Font = UIFont.SystemFontOfSize(16),
        BackgroundColor = UIColor.SecondarySystemGroupedBackground,
        TextContainerInset = new UIEdgeInsets(14, 12, 14, 12),
        TranslatesAutoresizingMaskIntoConstraints = false
    };
    private readonly UITextView _input = new()
    {
        Font = UIFont.SystemFontOfSize(17),
        BackgroundColor = UIColor.SecondarySystemGroupedBackground,
        TextContainerInset = new UIEdgeInsets(10, 10, 10, 10),
        KeyboardDismissMode = UIScrollViewKeyboardDismissMode.Interactive,
        TranslatesAutoresizingMaskIntoConstraints = false
    };
    private readonly UIButton _sendButton = ViewControllerSupport.CreatePrimaryButton("发送");
    private readonly UILabel _statusLabel = new()
    {
        TextColor = UIColor.SecondaryLabel,
        Font = UIFont.SystemFontOfSize(13),
        Lines = 1,
        TranslatesAutoresizingMaskIntoConstraints = false
    };
    private readonly UISwitch _autoSpeakSwitch = new()
    {
        TranslatesAutoresizingMaskIntoConstraints = false
    };
    private CancellationTokenSource? _requestCancellation;
    private int _sessionId;

    public ChatViewController(MobileAppServices services, string siteUrl, int sessionId)
    {
        _services = services;
        _siteUrl = siteUrl;
        _sessionId = sessionId;
        Title = "Admin Chat";
    }

    public override void ViewDidLoad()
    {
        base.ViewDidLoad();
        View.BackgroundColor = UIColor.SystemGroupedBackground;
        _transcript.Layer.CornerRadius = 12;
        _input.Layer.CornerRadius = 10;
        _autoSpeakSwitch.On = MobileSettings.AutoSpeak;

        var speakLabel = new UILabel
        {
            Text = "自动朗读",
            TextColor = UIColor.SecondaryLabel,
            Font = UIFont.SystemFontOfSize(14),
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        var speakStack = new UIStackView(new UIView[] { speakLabel, _autoSpeakSwitch })
        {
            Axis = UILayoutConstraintAxis.Horizontal,
            Alignment = UIStackViewAlignment.Center,
            Spacing = 8,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        var actionStack = new UIStackView(new UIView[] { _input, _sendButton })
        {
            Axis = UILayoutConstraintAxis.Horizontal,
            Alignment = UIStackViewAlignment.Fill,
            Spacing = 10,
            TranslatesAutoresizingMaskIntoConstraints = false
        };

        View.AddSubviews(_statusLabel, speakStack, _transcript, actionStack);
        NSLayoutConstraint.ActivateConstraints(new[]
        {
            _statusLabel.TopAnchor.ConstraintEqualTo(View.SafeAreaLayoutGuide.TopAnchor, 8),
            _statusLabel.LeadingAnchor.ConstraintEqualTo(View.LayoutMarginsGuide.LeadingAnchor),
            speakStack.CenterYAnchor.ConstraintEqualTo(_statusLabel.CenterYAnchor),
            speakStack.TrailingAnchor.ConstraintEqualTo(View.LayoutMarginsGuide.TrailingAnchor),

            _transcript.TopAnchor.ConstraintEqualTo(_statusLabel.BottomAnchor, 8),
            _transcript.LeadingAnchor.ConstraintEqualTo(View.LayoutMarginsGuide.LeadingAnchor),
            _transcript.TrailingAnchor.ConstraintEqualTo(View.LayoutMarginsGuide.TrailingAnchor),

            actionStack.TopAnchor.ConstraintEqualTo(_transcript.BottomAnchor, 10),
            actionStack.LeadingAnchor.ConstraintEqualTo(View.LayoutMarginsGuide.LeadingAnchor),
            actionStack.TrailingAnchor.ConstraintEqualTo(View.LayoutMarginsGuide.TrailingAnchor),
            actionStack.BottomAnchor.ConstraintEqualTo(View.KeyboardLayoutGuide.TopAnchor, -10),
            _input.HeightAnchor.ConstraintGreaterThanOrEqualTo(48),
            _input.HeightAnchor.ConstraintLessThanOrEqualTo(96),
            _sendButton.WidthAnchor.ConstraintEqualTo(78)
        });

        NavigationItem.RightBarButtonItem = new UIBarButtonItem(
            "新会话",
            UIBarButtonItemStyle.Plain,
            async (_, _) => await CreateSessionAsync());
        _sendButton.TouchUpInside += async (_, _) => await SendAsync();
        _autoSpeakSwitch.ValueChanged += (_, _) =>
        {
            MobileSettings.AutoSpeak = _autoSpeakSwitch.On;
            MobileSettings.Synchronize();
            if (!_autoSpeakSwitch.On)
            {
                _services.SpeechPlayback.Stop();
            }
        };

        _ = RefreshMessagesAsync();
    }

    public override void ViewDidDisappear(bool animated)
    {
        if (NavigationController == null || !NavigationController.ViewControllers.Contains(this))
        {
            _requestCancellation?.Cancel();
            _services.SpeechPlayback.Stop();
        }

        base.ViewDidDisappear(animated);
    }

    private async Task RefreshMessagesAsync()
    {
        SetBusy(true, $"正在加载会话 {_sessionId}…");
        try
        {
            var messages = await _services.AdminChat.GetSessionMessagesAsync(_siteUrl, _sessionId);
            RenderMessages(messages);
            _statusLabel.Text = $"会话 {_sessionId} · {_siteUrl}";
        }
        catch (Exception exception)
        {
            ViewControllerSupport.ShowError(this, "加载会话失败", exception);
        }
        finally
        {
            SetBusy(false, $"会话 {_sessionId} · 已连接");
        }
    }

    private async Task CreateSessionAsync()
    {
        SetBusy(true, "正在创建新会话…");
        try
        {
            _sessionId = await _services.AdminChat.CreateSessionAsync(_siteUrl);
            _transcript.Text = string.Empty;
            _statusLabel.Text = $"会话 {_sessionId} · 已连接";
        }
        catch (Exception exception)
        {
            ViewControllerSupport.ShowError(this, "创建会话失败", exception);
        }
        finally
        {
            SetBusy(false, $"会话 {_sessionId} · 已连接");
        }
    }

    private async Task SendAsync()
    {
        var content = _input.Text?.Trim() ?? string.Empty;
        if (content.Length == 0)
        {
            return;
        }

        _input.Text = string.Empty;
        _input.ResignFirstResponder();
        _services.SpeechPlayback.Stop();
        _requestCancellation?.Cancel();
        _requestCancellation?.Dispose();
        _requestCancellation = new CancellationTokenSource();
        var cancellationToken = _requestCancellation.Token;
        var existing = _transcript.Text ?? string.Empty;
        var prefix = existing.Length == 0 ? string.Empty : existing.TrimEnd() + "\n\n";
        var assistantText = new StringBuilder();
        _transcript.Text = $"{prefix}我\n{content}\n\nNCF Agent\n";
        ScrollTranscriptToEnd();
        SetBusy(true, "NCF Agent 正在回复…");

        try
        {
            var result = await _services.AdminChat.SendMessageStreamingAsync(
                _siteUrl,
                _sessionId,
                content,
                onToken: token =>
                {
                    assistantText.Append(token);
                    var snapshot = assistantText.ToString();
                    BeginInvokeOnMainThread(() =>
                    {
                        _transcript.Text = $"{prefix}我\n{content}\n\nNCF Agent\n{snapshot}";
                        ScrollTranscriptToEnd();
                    });
                },
                cancellationToken: cancellationToken);

            var messages = await _services.AdminChat.GetSessionMessagesAsync(
                _siteUrl,
                _sessionId,
                cancellationToken);
            RenderMessages(messages);
            if (_autoSpeakSwitch.On)
            {
                _services.SpeechPlayback.Speak(result.AssistantMessage?.Content);
            }
        }
        catch (OperationCanceledException)
        {
            _statusLabel.Text = $"会话 {_sessionId} · 已取消";
        }
        catch (Exception exception)
        {
            ViewControllerSupport.ShowError(this, "发送失败", exception);
        }
        finally
        {
            SetBusy(false, $"会话 {_sessionId} · 已连接");
        }
    }

    private void RenderMessages(IEnumerable<AdminChatMessage> messages)
    {
        _transcript.Text = string.Join(
            "\n\n",
            messages.Select(message => $"{message.SenderName}\n{message.Content}"));
        ScrollTranscriptToEnd();
    }

    private void ScrollTranscriptToEnd()
    {
        if (_transcript.Text.Length > 0)
        {
            _transcript.ScrollRangeToVisible(new Foundation.NSRange(_transcript.Text.Length - 1, 1));
        }
    }

    private void SetBusy(bool busy, string status)
    {
        _sendButton.Enabled = !busy;
        NavigationItem.RightBarButtonItem!.Enabled = !busy;
        _statusLabel.Text = status;
    }
}
