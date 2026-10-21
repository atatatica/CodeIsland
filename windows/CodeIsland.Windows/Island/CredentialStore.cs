using System.Runtime.InteropServices;
using System.Text;

namespace CodeIsland.Windows.Island;

/// Generic credentials in Windows Credential Manager (the user's own vault).
public static class CredentialStore
{
    public const string CalendarTarget = "hermes/icloud-caldav";

    public static (string User, string Secret)? Read(string target)
    {
        if (!CredRead(target, 1, 0, out var pointer)) return null;
        try
        {
            var credential = Marshal.PtrToStructure<Credential>(pointer);
            var secret = credential.CredentialBlobSize == 0
                ? string.Empty
                : Marshal.PtrToStringUni(credential.CredentialBlob, (int)credential.CredentialBlobSize / 2);
            return (credential.UserName ?? string.Empty, secret);
        }
        finally
        {
            CredFree(pointer);
        }
    }

    public static void Write(string target, string user, string secret)
    {
        var bytes = Encoding.Unicode.GetBytes(secret);
        var blob = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new Credential
            {
                Type = 1,
                TargetName = target,
                UserName = user,
                CredentialBlob = blob,
                CredentialBlobSize = (uint)bytes.Length,
                Persist = 2
            };
            if (!CredWrite(ref credential, 0))
                throw new InvalidOperationException($"Credential Manager refused the write (error {Marshal.GetLastWin32Error()}).");
        }
        finally
        {
            Marshal.FreeHGlobal(blob);
        }
    }

    public static void Delete(string target) => CredDelete(target, 1, 0);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
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

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CredReadW")]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CredWriteW")]
    private static extern bool CredWrite(ref Credential credential, uint flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CredDeleteW")]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}
