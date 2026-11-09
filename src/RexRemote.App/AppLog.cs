using System.Diagnostics;
using System.IO;

namespace RexRemote.App;

/// <summary>
/// 診断ログ。%LOCALAPPDATA%\RexRemote\rexremote.log に追記する。
/// 資格情報は Protocol 側でも App 側でも決して渡さないこと。
/// </summary>
internal static class AppLog
{
    private static readonly object Gate = new();

    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RexRemote", "rexremote.log");

    public static void Write(string message)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}";
        Debug.WriteLine(line);
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.AppendAllText(FilePath, line + Environment.NewLine);
            }
        }
        catch (IOException)
        {
            // ログが書けなくても動作は続ける
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
