using NcfMobileApp.Core.Services;
using NcfMobileApp.iOS.Services;
using UIKit;

namespace NcfMobileApp.iOS.ViewControllers;

public sealed class ConnectionViewController : UIViewController
{
    private readonly MobileAppServices _services;
    private readonly UITextField _siteField = ViewControllerSupport.CreateTextField(
        "https://your-ncf-site.example.com",
        UIKeyboardType.Url);
    private readonly UITextField _userField = ViewControllerSupport.CreateTextField("管理员账号");
    private readonly UITextField _passwordField = ViewControllerSupport.CreateTextField("管理员密码", secure: true);
    private readonly UILabel _statusLabel = new()
    {
        Text = "移动端只连接远程 NCF；登录令牌和密码不会持久化。",
        TextColor = UIColor.SecondaryLabel,
        Lines = 0,
        Font = UIFont.SystemFontOfSize(14),
        TranslatesAutoresizingMaskIntoConstraints = false
    };
    private readonly UIActivityIndicatorView _activity = new(UIActivityIndicatorViewStyle.Medium)
    {
        HidesWhenStopped = true,
        TranslatesAutoresizingMaskIntoConstraints = false
    };
    private readonly UIButton _connectButton = ViewControllerSupport.CreatePrimaryButton("连接并登录");

    public ConnectionViewController(MobileAppServices services)
    {
        _services = services;
        Title = "NCF Mobile";
    }

    public override void ViewDidLoad()
    {
        base.ViewDidLoad();
        View.BackgroundColor = UIColor.SystemGroupedBackground;
        _siteField.Text = MobileSettings.SiteUrl;
        _userField.Text = MobileSettings.UserName;

        var title = new UILabel
        {
            Text = "连接远程 NCF",
            Font = UIFont.BoldSystemFontOfSize(24),
            TextColor = UIColor.Label,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        var subtitle = new UILabel
        {
            Text = "需要已启用 AdminChat API 的 HTTPS 站点",
            Font = UIFont.SystemFontOfSize(15),
            TextColor = UIColor.SecondaryLabel,
            Lines = 0,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        var stack = new UIStackView(new UIView[]
        {
            title,
            subtitle,
            _siteField,
            _userField,
            _passwordField,
            _connectButton,
            _activity,
            _statusLabel
        })
        {
            Axis = UILayoutConstraintAxis.Vertical,
            Spacing = 14,
            TranslatesAutoresizingMaskIntoConstraints = false
        };

        View.AddSubview(stack);
        NSLayoutConstraint.ActivateConstraints(new[]
        {
            stack.TopAnchor.ConstraintEqualTo(View.SafeAreaLayoutGuide.TopAnchor, 28),
            stack.LeadingAnchor.ConstraintEqualTo(View.LayoutMarginsGuide.LeadingAnchor),
            stack.TrailingAnchor.ConstraintEqualTo(View.LayoutMarginsGuide.TrailingAnchor),
            _siteField.HeightAnchor.ConstraintEqualTo(48),
            _userField.HeightAnchor.ConstraintEqualTo(48),
            _passwordField.HeightAnchor.ConstraintEqualTo(48),
            _connectButton.HeightAnchor.ConstraintEqualTo(50)
        });

        _connectButton.TouchUpInside += async (_, _) => await ConnectAsync();
        _passwordField.ShouldReturn = _ =>
        {
            _passwordField.ResignFirstResponder();
            _ = ConnectAsync();
            return true;
        };
    }

    private async Task ConnectAsync()
    {
        var siteUrl = _siteField.Text?.Trim() ?? string.Empty;
        var userName = _userField.Text?.Trim() ?? string.Empty;
        var password = _passwordField.Text ?? string.Empty;
        if (!SiteEndpointPolicy.TryNormalizeSiteUrl(siteUrl, out var normalized, out var validationError))
        {
            ViewControllerSupport.ShowError(this, "站点地址不可用", new InvalidOperationException(validationError));
            return;
        }

        SetBusy(true, "正在验证站点和管理员权限…");
        View.EndEditing(true);
        try
        {
            var authentication = await _services.AdminChat.AuthenticateAsync(
                normalized.AbsoluteUri,
                userName,
                password);
            var sessions = await _services.AdminChat.GetSessionsAsync(normalized.AbsoluteUri);
            var sessionId = sessions.FirstOrDefault()?.Id ??
                            await _services.AdminChat.CreateSessionAsync(normalized.AbsoluteUri);

            MobileSettings.SiteUrl = normalized.AbsoluteUri;
            MobileSettings.UserName = authentication.UserName;
            MobileSettings.Synchronize();
            _passwordField.Text = string.Empty;

            NavigationController?.PushViewController(
                new ChatViewController(_services, normalized.AbsoluteUri, sessionId),
                true);
        }
        catch (Exception exception)
        {
            ViewControllerSupport.ShowError(this, "连接 NCF 失败", exception);
        }
        finally
        {
            SetBusy(false, "移动端只连接远程 NCF；登录令牌和密码不会持久化。");
        }
    }

    private void SetBusy(bool busy, string status)
    {
        _connectButton.Enabled = !busy;
        _siteField.Enabled = !busy;
        _userField.Enabled = !busy;
        _passwordField.Enabled = !busy;
        _statusLabel.Text = status;
        if (busy)
        {
            _activity.StartAnimating();
        }
        else
        {
            _activity.StopAnimating();
        }
    }
}
