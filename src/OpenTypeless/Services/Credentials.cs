using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using OpenTypeless.Native;

namespace OpenTypeless.Services;

/// <summary>
/// All provider keys in one Windows Credential Manager entry ("OpenTypeless/credentials"), so the app touches
/// the credential store once at launch. The counterpart of the single macOS Keychain item.
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

    public static void Save(Dictionary<string, string> keys)
    {
        if (keys.Count == 0)
        {
            Win32.CredDelete(Target, Win32.CRED_TYPE_GENERIC, 0);
            return;
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
            Win32.CredWrite(ref credential, 0);
        }
        finally
        {
            Marshal.FreeHGlobal(blob);
        }
    }
}
