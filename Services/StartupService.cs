using NasToolbox.Models;

namespace NasToolbox.Services;

/// <summary>
/// 启动阶段任务编排:把原本散落在 MainWindow 构造函数里的后台任务
/// (设备在线探测、NAS 状态预取)收拢成有顺序、可上报进度、带超时兜底的流程,
/// 供启动画面(SplashWindow)显示真实进度。
/// </summary>
public static class StartupService
{
    /// <summary>设备在线探测的等待上限(毫秒):超时则后台继续跑,不卡启动画面。</summary>
    private const int ProbeTimeoutMs = 6000;

    /// <summary>NAS 状态预取的等待上限(毫秒)。</summary>
    private const int PrefetchTimeoutMs = 4000;

    /// <summary>
    /// 执行启动流程。<paramref name="report"/> 的第一个参数是百分比(0-100),第二个是当前步骤描述。
    /// 任何一步抛异常都不会中断启动,最多让进度条停在最后一步。
    /// </summary>
    public static async Task RunAsync(Action<int, string> report)
    {
        void Step(int percent, string text)
        {
            try { report(percent, text); } catch { /* 窗口已关闭等场景:忽略 */ }
        }

        // 给启动画面留出首次渲染时间,否则进度条会直接跳到 100%
        Step(6, "正在启动…");
        await Task.Delay(180).ConfigureAwait(true);

        Step(18, "正在读取设备台账…");
        var devices = LoadDevices();
        await Task.Delay(80).ConfigureAwait(true);

        if (devices.Count > 0)
        {
            Step(34, $"正在检测 {devices.Count} 台设备的在线状态…");
            await WaitOrBackground(DeviceProbeService.ProbeAndPersistAsync(), ProbeTimeoutMs)
                .ConfigureAwait(true);
            Step(68, "设备在线状态已就绪");
        }
        else
        {
            Step(68, "尚未添加设备,可到「设备管理」添加");
        }

        Step(82, "正在预取 NAS 运行状态…");
        await WaitOrBackground(NasStatusService.PrefetchOnStartupAsync(), PrefetchTimeoutMs)
            .ConfigureAwait(true);

        Step(100, "就绪");
        await Task.Delay(220).ConfigureAwait(true);
    }

    private static List<NasDevice> LoadDevices()
    {
        try
        {
            return NasDeviceStore.Load();
        }
        catch
        {
            return new List<NasDevice>();
        }
    }

    /// <summary>
    /// 等待任务完成,但最多等 <paramref name="timeoutMs"/> 毫秒。
    /// 超时不取消任务 —— 让它在后台继续跑(结果照样会落盘/进缓存),启动流程直接往下走,
    /// 避免某台 NAS 连不上时启动画面被拖死。
    /// </summary>
    private static async Task WaitOrBackground(Task task, int timeoutMs)
    {
        try
        {
            var finished = await Task.WhenAny(task, Task.Delay(timeoutMs)).ConfigureAwait(true);
            if (finished != task) return;     // 超时:任务仍在后台运行
            await task.ConfigureAwait(true);  // 完成:把内部异常吃掉
        }
        catch
        {
            // 探测/预取自身失败不影响启动
        }
    }
}
