using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace ListeningSubtitles.Core;

/// <summary>Protects the API key with Windows DPAPI for the current Windows user.</summary>
public sealed class SecretStore
{
    private readonly string _path;
    private const int CryptProtectUiForbidden = 1;

    public SecretStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _path = Path.Combine(Path.GetFullPath(directory), "api-key.bin");
    }

    public bool HasKey => File.Exists(_path);

    public string? Read()
    {
        if (!HasKey) return null;
        if (new FileInfo(_path).Length > 1024 * 1024)
            throw new CryptographicException(AppText.T("已保存的 API 密钥文件无效，请重新输入密钥。", "The saved API key file is invalid. Enter your key again."));
        var protectedBytes = File.ReadAllBytes(_path);
        var plain = Transform(protectedBytes, protect: false);
        try
        {
            var key = new UTF8Encoding(false, true).GetString(plain);
            ValidateKey(key);
            return key;
        }
        catch (Exception ex) when (ex is DecoderFallbackException or ArgumentException)
        {
            throw new CryptographicException(AppText.T("已保存的 API 密钥无效，请重新输入密钥。", "The saved API key is invalid. Enter your key again."), ex);
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public void Save(string apiKey)
    {
        ValidateKey(apiKey);
        var plain = Encoding.UTF8.GetBytes(apiKey);
        try
        {
            var encrypted = Transform(plain, protect: true);
            StorageFiles.WriteAtomic(_path, encrypted, CreatePrivateFile);
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public void Delete() => File.Delete(_path);

    internal static void ValidateKey(string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey) || apiKey.Length > 8192 || apiKey.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)))
            throw new ArgumentException(AppText.T("API 密钥不能为空，且不能包含空格或换行。", "The API key cannot be empty or contain spaces or line breaks."), nameof(apiKey));
    }

    internal static FileStream CreatePrivateFile(string path)
    {
        var user = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException(AppText.T("无法确定当前 Windows 用户。", "The current Windows user could not be identified."));
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(user);
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        return new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.FullControl, FileShare.None, 4096, FileOptions.None, security);
    }

    internal static byte[] Transform(byte[] data, bool protect, string label = "Listening Subtitles API key", int maximumBytes = 1024 * 1024)
    {
        var input = new DataBlob { Length = data.Length, Data = Marshal.AllocHGlobal(Math.Max(data.Length, 1)) };
        DataBlob output = default;
        IntPtr description = IntPtr.Zero;
        try
        {
            Marshal.Copy(data, 0, input.Data, data.Length);
            var ok = protect
                ? CryptProtectData(ref input, label, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out output)
                : CryptUnprotectData(ref input, out description, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out output);
            if (!ok)
                throw new CryptographicException(protect ? AppText.T("Windows 无法加密数据。", "Windows could not encrypt the data.") : AppText.T("Windows 无法解密已保存的数据。请使用原 Windows 账户运行。", "Windows could not decrypt the saved data. Run the app under the original Windows account."), new Win32Exception(Marshal.GetLastWin32Error()));
            if (output.Length < 0 || output.Length > maximumBytes)
                throw new CryptographicException(AppText.T("Windows 返回的加密数据无效。", "Windows returned invalid protected data."));
            var result = new byte[output.Length];
            Marshal.Copy(output.Data, result, 0, result.Length);
            return result;
        }
        finally
        {
            ZeroNative(input.Data, input.Length);
            Marshal.FreeHGlobal(input.Data);
            if (output.Data != IntPtr.Zero)
            {
                if (!protect) ZeroNative(output.Data, output.Length);
                LocalFree(output.Data);
            }
            if (description != IntPtr.Zero) LocalFree(description);
        }
    }

    private static void ZeroNative(IntPtr memory, int length)
    {
        if (length <= 0) return;
        var zeros = new byte[Math.Min(length, 16384)];
        for (var offset = 0; offset < length; offset += zeros.Length)
            Marshal.Copy(zeros, 0, IntPtr.Add(memory, offset), Math.Min(zeros.Length, length - offset));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob { public int Length; public IntPtr Data; }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob input, string description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob input, out IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
