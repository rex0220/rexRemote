using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RexRemote.App;

/// <summary>接続先 1 件（履歴の要素）。</summary>
public sealed class ConnectionEntry
{
    public string Host { get; set; } = string.Empty;

    public int Port { get; set; } = AppSettings.DefaultPort;

    /// <summary>一覧での表示用。既定ポートなら空、それ以外は ":5999" のように付ける。</summary>
    [JsonIgnore]
    public string PortSuffix => Port == AppSettings.DefaultPort ? string.Empty : $":{Port}";

    public override string ToString() => Host + PortSuffix;
}

/// <summary>
/// アプリの設定。%LOCALAPPDATA%\RexRemote\settings.json に保存する。
/// 資格情報はここに入れない（パスワードは <see cref="CredentialStore"/>）。
/// </summary>
internal sealed class AppSettings
{
    public const int DefaultPort = 5900;
    public const int MaxRecentConnections = 10;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RexRemote", "settings.json");

    /// <summary>接続に成功した接続先。新しい順。</summary>
    public List<ConnectionEntry> RecentConnections { get; set; } = [];

    /// <summary>表示倍率（前回選んだもの）。</summary>
    public DisplayScale Scale { get; set; } = DisplayScale.Fit;

    /// <summary>Ctrl ⇔ Command を入れ替える。</summary>
    public bool SwapControlAndCommand { get; set; }

    /// <summary>
    /// Windows と Mac でクリップボード（文字列）を共有する（RFB 標準の ClientCutText / ServerCutText）。
    /// macOS 27 の画面共有サーバーでは画面上のクリップボードに反映されない（実機で確認）ので既定はオフ。
    /// </summary>
    public bool ClipboardSharing { get; set; }

    /// <summary>回線の瞬断などで切れたら自動で再接続する。</summary>
    public bool AutoReconnect { get; set; } = true;

    /// <summary>履歴の先頭に追加する（同じ接続先は先頭へ移動し、上限を超えた古いものは捨てる）。</summary>
    public void AddRecentConnection(string host, int port)
    {
        RecentConnections.RemoveAll(e => e.Port == port && string.Equals(e.Host, host, StringComparison.OrdinalIgnoreCase));
        RecentConnections.Insert(0, new ConnectionEntry { Host = host, Port = port });
        if (RecentConnections.Count > MaxRecentConnections)
        {
            RecentConnections.RemoveRange(MaxRecentConnections, RecentConnections.Count - MaxRecentConnections);
        }
    }

    /// <summary>履歴から取り除く。</summary>
    public bool RemoveRecentConnection(string host, int port) =>
        RecentConnections.RemoveAll(e => e.Port == port && string.Equals(e.Host, host, StringComparison.OrdinalIgnoreCase)) > 0;

    /// <summary>読めない・壊れている場合は既定値を返す（起動は止めない）。</summary>
    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions) ?? new AppSettings();
                settings.RecentConnections.RemoveAll(e => string.IsNullOrWhiteSpace(e.Host) || e.Port is < 1 or > 65535);
                return settings;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            AppLog.Write($"設定を読めませんでした（既定値で起動）: {ex.Message}");
        }

        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            // 書き込み途中で落ちても壊れないよう、一時ファイルに書いてから置き換える
            string temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, JsonOptions));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Write($"設定を保存できませんでした: {ex.Message}");
        }
    }
}
