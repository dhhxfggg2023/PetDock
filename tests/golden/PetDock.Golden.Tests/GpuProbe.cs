// docs/41 D-12c：基准元数据必须记录 GPU 型号与驱动版本。
// 用 WMI（System.Management，无原生依赖）读 Win32_VideoController；读不到时如实返回
// 空串并在 meta.json 里标 "unavailable"，绝不编造一个型号冒充已认证。
namespace PetDock.Golden.Tests;

internal static class GpuProbe
{
    /// <summary>本机 GPU 型号（WMI 第一张卡的名；取不到返回空串）。</summary>
    public static string GpuModel()
    {
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT Name FROM Win32_VideoController");
            using System.Management.ManagementObjectCollection results = searcher.Get();
            foreach (System.Management.ManagementObject controller in results)
            {
                object? name = controller["Name"];
                if (name is string text && !string.IsNullOrWhiteSpace(text))
                {
                    return text.Trim();
                }
            }
        }
        catch (System.Management.ManagementException)
        {
            // WMI 不可用（策略 / 服务停）：如实留空。
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // COM 初始化失败（非交互会话）：如实留空。
        }

        return string.Empty;
    }

    /// <summary>本机驱动版本（WMI DriverVersion；取不到返回空串）。</summary>
    public static string DriverVersion()
    {
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT DriverVersion FROM Win32_VideoController");
            using System.Management.ManagementObjectCollection results = searcher.Get();
            foreach (System.Management.ManagementObject controller in results)
            {
                object? version = controller["DriverVersion"];
                if (version is string text && !string.IsNullOrWhiteSpace(text))
                {
                    return text.Trim();
                }
            }
        }
        catch (System.Management.ManagementException)
        {
        }
        catch (System.Runtime.InteropServices.COMException)
        {
        }

        return string.Empty;
    }
}
