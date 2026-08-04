using UIKit;

namespace NcfMobileApp.iOS.ViewControllers;

internal static class ViewControllerSupport
{
    public static UITextField CreateTextField(
        string placeholder,
        UIKeyboardType keyboardType = UIKeyboardType.Default,
        bool secure = false)
    {
        return new UITextField
        {
            Placeholder = placeholder,
            BorderStyle = UITextBorderStyle.RoundedRect,
            KeyboardType = keyboardType,
            SecureTextEntry = secure,
            AutocapitalizationType = UITextAutocapitalizationType.None,
            AutocorrectionType = UITextAutocorrectionType.No,
            ClearButtonMode = UITextFieldViewMode.WhileEditing,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
    }

    public static UIButton CreatePrimaryButton(string title)
    {
        var button = new UIButton(UIButtonType.System)
        {
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        button.SetTitle(title, UIControlState.Normal);
        button.TitleLabel.Font = UIFont.BoldSystemFontOfSize(17);
        button.BackgroundColor = UIColor.SystemBlue;
        button.SetTitleColor(UIColor.White, UIControlState.Normal);
        button.Layer.CornerRadius = 10;
        return button;
    }

    public static void ShowError(UIViewController owner, string title, Exception exception)
    {
        var alert = UIAlertController.Create(title, exception.Message, UIAlertControllerStyle.Alert);
        alert.AddAction(UIAlertAction.Create("好", UIAlertActionStyle.Default, null));
        owner.PresentViewController(alert, true, null);
    }
}
