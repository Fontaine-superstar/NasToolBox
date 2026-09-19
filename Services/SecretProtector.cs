using System.Security.Cryptography;
using System.Text;

namespace NasToolbox.Services;

/// <summary>
/// DPAPI(CurrentUser)加解密:SSH 密码不以明文落盘。
/// 密文与当前 Windows 用户 + 本机绑定,拷贝 devices.json 到别的机器/用户无法解出。
/// </summary>
public static class SecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("NasToolbox.Ssh.v1");

    public static string Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain)) return string.Empty;
        var bytes = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(bytes);
    }

    public static string Unprotect(string? cipher)
    {
        if (string.IsNullOrWhiteSpace(cipher)) return string.Empty;
        try
        {
            var bytes = ProtectedData.Unprotect(
                Convert.FromBase64String(cipher), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            // 密文损坏 / 跨用户拷贝:视为无密码
            return string.Empty;
        }
    }
}