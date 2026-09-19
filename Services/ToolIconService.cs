using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace NasToolbox.Services;

/// <summary>
/// 从 exe/.lnk 提取图标,缓存为 PNG 到 %LocalAppData%/MyToolbox/IconCache。
/// 对应 TubaWinUi3 的 ToolIconService(其发布版会由 CI 预生成 IconCache 随包分发)。
/// </summary>
public static class ToolIconService
{
    private static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MyToolbox", "IconCache");

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfoW(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private const uint SHGFI_ICON = 0x00000100;
    private const uint SHGFI_LARGEICON = 0x00000000;

    public static string? GetIconPng(string filePath)
    {
        try
        {
            Directory.CreateDirectory(CacheDir);
            var key = Convert.ToHexString(SHA1.HashData(Encoding.Unicode.GetBytes(filePath.ToLowerInvariant())))[..12];
            var png = Path.Combine(CacheDir, key + ".png");
            if (File.Exists(png)) return png;

            var shfi = new SHFILEINFO();
            SHGetFileInfoW(filePath, 0, ref shfi, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_ICON | SHGFI_LARGEICON);
            if (shfi.hIcon == IntPtr.Zero) return null;

            try
            {
                using var icon = Icon.FromHandle(shfi.hIcon);
                using var bmp = icon.ToBitmap();
                bmp.Save(png, ImageFormat.Png);
            }
            finally
            {
                DestroyIcon(shfi.hIcon);
            }
            return png;
        }
        catch
        {
            return null;
        }
    }
}
