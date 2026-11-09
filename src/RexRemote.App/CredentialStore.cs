using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using RexRemote.Protocol;

namespace RexRemote.App;

/// <summary>
/// 接続先ごとの資格情報を Windows 資格情報マネージャー（汎用資格情報）に保存する。
/// Windows がユーザーごとに暗号化して保管する。「コントロール パネル → 資格情報マネージャー」で確認・削除できる。
/// 利用者が「パスワードを保存する」を選んだときだけ使う。パスワードはログに出さない。
/// </summary>
internal static class CredentialStore
{
    private const uint CRED_TYPE_GENERIC = 1;
    private const uint CRED_PERSIST_LOCAL_MACHINE = 2;

    public static string TargetName(string host, int port) => $"rexRemote:{host}:{port}";

    public static RfbCredentials? Load(string host, int port)
    {
        if (!CredReadW(TargetName(host, port), CRED_TYPE_GENERIC, 0, out IntPtr pointer))
        {
            return null; // 未保存（ERROR_NOT_FOUND）
        }

        try
        {
            var credential = Marshal.PtrToStructure<CREDENTIAL>(pointer);
            string password = credential.CredentialBlobSize == 0
                ? string.Empty
                : Marshal.PtrToStringUni(credential.CredentialBlob, (int)credential.CredentialBlobSize / 2);
            return new RfbCredentials(string.IsNullOrEmpty(credential.UserName) ? null : credential.UserName, password);
        }
        finally
        {
            CredFree(pointer);
        }
    }

    public static void Save(string host, int port, RfbCredentials credentials)
    {
        byte[] blob = Encoding.Unicode.GetBytes(credentials.Password);
        IntPtr blobPointer = Marshal.AllocHGlobal(Math.Max(blob.Length, 1));
        try
        {
            Marshal.Copy(blob, 0, blobPointer, blob.Length);
            var credential = new CREDENTIAL
            {
                Type = CRED_TYPE_GENERIC,
                TargetName = TargetName(host, port),
                Comment = "rexRemote（Mac 画面共有）",
                CredentialBlobSize = (uint)blob.Length,
                CredentialBlob = blobPointer,
                Persist = CRED_PERSIST_LOCAL_MACHINE,
                UserName = credentials.UserName ?? string.Empty,
            };

            if (!CredWriteW(ref credential, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "資格情報を保存できませんでした。");
            }
        }
        finally
        {
            Marshal.Copy(new byte[blob.Length], 0, blobPointer, blob.Length);
            Marshal.FreeHGlobal(blobPointer);
            CryptographicOperations.ZeroMemory(blob);
        }
    }

    public static bool Delete(string host, int port) => CredDeleteW(TargetName(host, port), CRED_TYPE_GENERIC, 0);

    /// <summary>保存されているか（パスワードは読み出さない）。</summary>
    public static bool Exists(string host, int port)
    {
        if (!CredReadW(TargetName(host, port), CRED_TYPE_GENERIC, 0, out IntPtr pointer))
        {
            return false;
        }

        CredFree(pointer);
        return true;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredReadW(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWriteW(ref CREDENTIAL credential, uint flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDeleteW(string target, uint type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}
