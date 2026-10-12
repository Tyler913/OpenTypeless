using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using OpenTypeless.Native;

namespace OpenTypeless.Services;

/// <summary>
/// All provider keys in one Windows Credential Manager entry ("OpenTypeless/credentials"), so the app reads the
/// credential store once at launch (and again only to merge before saving a change). The counterpart of the macOS
/// <c>credentials.json</c>.
/// </summary>
public static class Credentials
{
    private const string Target = "OpenTypeless/credentials";

    public static Dictionary<string, string> Load()
    {
        if (!Win32.CredRead(Target, Win32.CRED_TYPE_GENERIC, 0, out var pointer)) return new();
        try
        {
            var credential = Marshal.PtrToStructure<Win32.CREDENTIAL>(pointer);
            if (credential.CredentialBlobSize == 0 || credential.CredentialBlob == 0) return new();
            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(bytes) ?? new();
        }
        catch (JsonException)
        {
            return new();
        }
        finally
        {
            Win32.CredFree(pointer);
        }
    }

    /// <summary>False when Credential Manager refused the change (the error is logged), so the caller can say so.</summary>
    public static bool Save(Dictionary<string, string> keys)
    {
        if (keys.Count == 0)
        {
            if (Win32.CredDelete(Target, Win32.CRED_TYPE_GENERIC, 0)) return true;
            var deleteError = Marshal.GetLastWin32Error();
            if (deleteError == ErrorNotFound) return true;
            AppLog.Debug("credentials", $"CredDelete failed: error {deleteError}");
            return false;
        }
        var bytes = JsonSerializer.SerializeToUtf8Bytes(keys);
        var blob = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new Win32.CREDENTIAL
            {
                Type = Win32.CRED_TYPE_GENERIC,
                TargetName = Target,
                Comment = "OpenTypeless API keys",
                CredentialBlobSize = bytes.Length,
                CredentialBlob = blob,
                Persist = Win32.CRED_PERSIST_LOCAL_MACHINE,
                UserName = Environment.UserName,
            };
            if (Win32.CredWrite(ref credential, 0)) return true;
            // E.g. keys past the 2,560-byte limit of one entry, or no logon session to store them in.
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
