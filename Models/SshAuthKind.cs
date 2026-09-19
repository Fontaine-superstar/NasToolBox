namespace NasToolbox.Models;

/// <summary>SSH 认证方式。</summary>
public enum SshAuthKind
{
    /// <summary>用户名 + 密码(同时尝试键盘交互,兼容群晖 / 威联通)。</summary>
    Password = 0,

    /// <summary>用户名 + 私钥文件(OpenSSH / PEM 格式),私钥自身可另设口令。</summary>
    PrivateKey = 1,
}

public static class SshAuthKindExtensions
{
    /// <summary>下拉列表展示用的中文名称。</summary>
    public static string Display(this SshAuthKind kind) => kind switch
    {
        SshAuthKind.PrivateKey => "私钥文件 (id_rsa / .pem)",
        _ => "密码",
    };
}
