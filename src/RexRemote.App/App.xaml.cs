using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace RexRemote.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // --help / --version はウィンドウを開かずに表示して終わる
        switch (e.Args.FirstOrDefault())
        {
            case "--version" or "-v" or "/v":
                ShowCommandLineText(AppInfo.VersionLine, "バージョン");
                Shutdown();
                return;

            case "--help" or "-h" or "/?" or "-?":
                ShowCommandLineText(AppInfo.CommandLineHelp, "使い方");
                Shutdown();
                return;
        }

        new MainWindow().Show();
    }

    /// <summary>
    /// ターミナルから起動されたらそのターミナルに出力し、ダブルクリックなどで起動されたらダイアログで表示する
    /// （WinExe なので自分のコンソールを持たない）。
    /// </summary>
    private static void ShowCommandLineText(string text, string title)
    {
        if (AttachConsole(AttachParentProcess))
        {
            Console.WriteLine();
            Console.WriteLine(text);
            return;
        }

        MessageBox.Show(text, $"{AppInfo.Name} - {title}", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.Message, "rexRemote - 予期しないエラー", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    private const int AttachParentProcess = -1;

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(int processId);
}
