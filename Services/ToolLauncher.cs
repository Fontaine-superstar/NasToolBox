using System.Diagnostics;
using NasToolbox.Models;

namespace NasToolbox.Services;

/// <summary>以独立进程启动第三方工具(对应 TubaWinUi3 的工具启动逻辑,简化版)。</summary>
public static class ToolLauncher
{
    public static void Launch(ToolItem tool, bool asAdmin = false)
    {
        var psi = new ProcessStartInfo
        {
            FileName = tool.ExePath,
            WorkingDirectory = Path.GetDirectoryName(tool.ExePath)!,
            UseShellExecute = true,   // .bat/.lnk/.msc 等也走 Shell 执行
        };
        if (asAdmin) psi.Verb = "runas";

        Process.Start(psi);
    }
}
