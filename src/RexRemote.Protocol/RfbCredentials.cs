namespace RexRemote.Protocol;

/// <summary>
/// 接続に使う資格情報。ToString() はパスワードを出力しない（ログ混入防止）。
/// </summary>
public sealed class RfbCredentials
{
    public RfbCredentials(string? userName, string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        UserName = userName;
        Password = password;
    }

    /// <summary>macOS のユーザー名。ARD 認証（タイプ 30）で使う。VNC パスワード認証のみなら null。</summary>
    public string? UserName { get; }

    public string Password { get; }

    public bool HasUserName => !string.IsNullOrEmpty(UserName);

    public override string ToString() => $"RfbCredentials(UserName={UserName ?? "(none)"}, Password=***)";
}
