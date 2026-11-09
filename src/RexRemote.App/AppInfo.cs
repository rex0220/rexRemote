using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace RexRemote.App;

/// <summary>バージョンなどアプリの情報（ヘルプ画面と --version / --help で使う）。</summary>
internal static class AppInfo
{
    public const string Name = "rexRemote";

    /// <summary>例: "0.1.0"。Directory.Build.props の Version。ビルド時に付く "+コミット" は表示しない。</summary>
    public static string Version { get; } = GetVersion();

    public static string Runtime => RuntimeInformation.FrameworkDescription;

    /// <summary>例: "Copyright (c) 2026 rex0220"。Directory.Build.props の Copyright。</summary>
    public static string Copyright { get; } =
        typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright ?? string.Empty;

    public const string License = "MIT License";

    public static string VersionLine =>
        $"{Name} {Version}（{Runtime}、{RuntimeInformation.OSDescription}）\n{Copyright} - {License}";

    public static string LogFile => AppLog.FilePath;

    public static string SettingsFile => AppSettings.FilePath;

    public static string ScreenshotFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "rexRemote");

    public static string CommandLineHelp =>
        $"""
        {Name} {Version} - Windows から macOS の画面共有（VNC）に接続するクライアント

        使い方:
          RexRemote.exe [host[:port]]   接続先を指定して起動し、すぐに接続する（ポートの既定は 5900）
          RexRemote.exe --help          この説明を表示する（-h, /? も可）
          RexRemote.exe --version       バージョンを表示する（-v も可）

        例:
          RexRemote.exe mac-mini
          RexRemote.exe localhost:5900

        資格情報は環境変数 REXREMOTE_USER / REXREMOTE_PASSWORD、保存したもの、入力ダイアログの順に使います。
        操作方法はアプリのツールバーの「？」を参照してください。
        """;

    private static string GetVersion()
    {
        string? informational = typeof(AppInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrEmpty(informational))
        {
            return typeof(AppInfo).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        }

        int plus = informational.IndexOf('+');
        return plus >= 0 ? informational[..plus] : informational;
    }
}
