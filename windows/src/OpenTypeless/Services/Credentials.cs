using System.Runtime.InteropServices;
using OpenTypeless.Native;

namespace OpenTypeless.Services;

/// <summary>
/// All provider keys in Windows Credential Manager, read once at launch (and again only to merge before saving a change).
/// The counterpart of the macOS <c>credentials.json</c>. They go in one entry ("OpenTypeless/credentials") as long as
/// they fit its 2,560 bytes; with more providers set up the rest go in "OpenTypeless/credentials/2", "/3"… (see
/// <see cref="CredentialBlobs"/>), and a version that knows only the first entry still finds the original providers'
/// keys there.
/// </summary>
public static class Credentials
{
    private const string Target = "OpenTypeless/credentials";
    /// <summary>More entries than any number of providers needs; reading and deleting stop here at the latest.</summary>
    private const int MaxEntries = 16;

    private static string EntryName(int index) => index == 0 ? Target : $"{Target}/{index + 1}";

    public static Dictionary<string, string> Load()
    {
        var blobs = new List<byte[]>();
        for (var i = 0; i < MaxEntries; i++)
        {
            // Entries are written in order, so the first one missing ends the list.
            if (Read(EntryName(i)) is not { } blob) break;
            blobs.Add(blob);
        }
        return CredentialBlobs.Merge(blobs);
    }

    private static byte[]? Read(string target)
    {
        if (!Win32.CredRead(target, Win32.CRED_TYPE_GENERIC, 0, out var pointer)) return null;
        try
        {
            var credential = Marshal.PtrToStructure<Win32.CREDENTIAL>(pointer);
            if (credential.CredentialBlobSize == 0 || credential.CredentialBlob == 0) return [];
            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            return bytes;
        }
        finally
        {
            Win32.CredFree(pointer);
        }
    }

    /// <summary>False when Credential Manager refused the change (the error is logged), so the caller can say so.</summary>
    public static bool Save(Dictionary<string, string> keys)
    {
        if (CredentialBlobs.Split(keys) is not { } blobs)
        {
            AppLog.Debug("credentials", "a key is too long for a Credential Manager entry");
            return false;
        }
        for (var i = 0; i < blobs.Count; i++)
        {
            if (!Write(EntryName(i), blobs[i])) return false;
        }
        // Entries the keys no longer need (all of them once there are no keys).
        var saved = true;
        for (var i = blobs.Count; i < MaxEntries; i++)
        {
            if (Win32.CredDelete(EntryName(i), Win32.CRED_TYPE_GENERIC, 0)) continue;
            var deleteError = Marshal.GetLastWin32Error();
            if (deleteError == ErrorNotFound) break;
            AppLog.Debug("credentials", $"CredDelete failed: error {deleteError}");
            saved = false;
            break;
        }
        return saved;
    }

    private static bool Write(string target, byte[] bytes)
    {
        var blob = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new Win32.CREDENTIAL
            {
                Type = Win32.CRED_TYPE_GENERIC,
                TargetName = target,
                Comment = "OpenTypeless API keys",
                CredentialBlobSize = bytes.Length,
                CredentialBlob = blob,
                Persist = Win32.CRED_PERSIST_LOCAL_MACHINE,
                UserName = Environment.UserName,
            };
            if (Win32.CredWrite(ref credential, 0)) return true;
            // E.g. no logon session to store them in.
            var writeError = Marshal.GetLastWin32Error();
            AppLog.Debug("credentials", $"CredWrite failed: error {writeError}");
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(blob);
        }
    }

    private const int ErrorNotFound = 1168;
}
