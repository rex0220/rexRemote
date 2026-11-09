using System.Windows;
using RexRemote.Protocol;

namespace RexRemote.App;

/// <summary>
/// macOS のユーザー名とパスワードを入力するダイアログ。
/// 「パスワードを保存する」を選んだときだけ、認証成功後に呼び出し側が資格情報マネージャーへ保存する。
/// </summary>
public partial class CredentialsDialog : Window
{
    public CredentialsDialog(string target, string? userName)
    {
        InitializeComponent();
        TargetText.Text = $"{target} に接続します。macOS のユーザー名とパスワードを入力してください。";
        UserNameBox.Text = userName ?? string.Empty;
        if (!string.IsNullOrEmpty(userName))
        {
            Loaded += (_, _) => PasswordBox.Focus();
        }
    }

    public RfbCredentials? Credentials { get; private set; }

    /// <summary>認証に成功したら資格情報マネージャーに保存する。</summary>
    public bool SavePassword { get; private set; }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        string user = UserNameBox.Text.Trim();
        Credentials = new RfbCredentials(user.Length == 0 ? null : user, PasswordBox.Password);
        SavePassword = SavePasswordCheckBox.IsChecked == true;
        PasswordBox.Clear();
        DialogResult = true;
    }
}
