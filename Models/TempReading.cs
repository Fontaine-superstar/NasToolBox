namespace NasToolbox.Models;

/// <summary>NAS 的一个温度传感器读数(thermal zone 或 hwmon)。</summary>
public sealed class TempReading
{
    /// <summary>传感器显示名(hwmon 优先取 label,否则芯片名;thermal zone 取 type)。</summary>
    public string Name { get; set; } = "";

    /// <summary>温度(摄氏度,整数值;来源数据为毫摄氏度,四舍五入)。</summary>
    public int Celsius { get; set; }

    public string Text => $"{Celsius} °C";
}
