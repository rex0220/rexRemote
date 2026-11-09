using System.Diagnostics;
using System.IO;
using System.Windows;

namespace RexRemote.App;

/// <summary>ヘルプとバージョン情報。</summary>
public partial class HelpWindow : Window
{
    public HelpWindow()
    {
        InitializeComponent();
        VersionText.Text = $"バージョン {AppInfo.Version}　（{AppInfo.Runtime}）";
        LicenseText.Text = $"{AppInfo.Copyright}　{AppInfo.License}";
        LogPathBox.Text = AppInfo.LogFile;
        SettingsPathBox.Text = AppInfo.SettingsFile;
        ScreenshotPathBox.Text = AppInfo.ScreenshotFolder;
        CommandLineBox.Text =
            "RexRemote.exe [host[:port]]   接続先を指定して起動し、すぐに接続する\n" +
            "RexRemote.exe --help          使い方を表示する\n" +
            "RexRemote.exe --version       バージョンを表示する";
    }

    private void OnOpenLogFolder(object sender, RoutedEventArgs e)
    {
        string folder = Path.GetDirectoryName(AppInfo.LogFile)!;
        Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
