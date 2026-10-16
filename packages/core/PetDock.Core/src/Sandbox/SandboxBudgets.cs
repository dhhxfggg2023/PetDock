// M1 契约 §4.6 / docs/14 §5：Tier-1 预算值冻结。
// 三个维度映射到 Wasmtime 的两层机制（契约 §4.6 已冻结此映射）：
//   内存      → Store.SetLimits(memorySize:)           —— 只限未来增长，单位字节
//   每片 CPU  → Store.Fuel（Config.WithFuelConsumption）—— 每片重置，耗尽即 trap
//   墙钟      → Store.SetEpochDeadline（阈值已设）—— 全 core 无 Engine.IncrementEpoch 调用点，
//               该层当前惰性；M1 的每片有界完全由 Fuel 承担（t8 评审 low 发现 / M2 标定项）
// 数值取 docs/14 §5 的验收值：内存 150MB；CPU 每活跃帧 8.3ms。
// Fuel/纪元刻度与毫秒之间没有通用换算 —— 宿主在 Load 时按本机标定把
// MillisecondsPerSlice 换算成 FuelPerSlice / EpochTicksPerSlice（见 PluginSandbox）。
namespace PetDock.Core.Sandbox;

/// <summary>
/// 一个沙箱实例的资源预算。所有字段为每实例独立（契约 §4.6：每插件独立 Engine/Store）。
/// </summary>
public readonly record struct SandboxBudgets
{
    /// <summary>docs/14 §5 内存上限：150MB。</summary>
    public const long DefaultMemoryBytes = 150L * 1024 * 1024;

    /// <summary>docs/14 §5 每活跃帧 CPU 预算：8.3ms。</summary>
    public const double DefaultMillisecondsPerSlice = 8.3;

    /// <summary>内存上限（字节）。映射到 Store.SetLimits。</summary>
    public long MemoryBytes { get; init; }

    /// <summary>每片墙钟毫秒数。宿主据此标定 FuelPerSlice（M1 尚无纪元推进点，见文件头）。</summary>
    public double MillisecondsPerSlice { get; init; }

    /// <summary>
    /// 每片燃料量。0 = 未标定（宿主尚未调用 Calibrate）——此时**没有任何一层在限制执行**
    /// （纪元计数无推进点，见文件头），所以运行前必须标定为 &gt; 0。
    /// </summary>
    public ulong FuelPerSlice { get; init; }

    /// <summary>每片纪元刻度数（默认 1，即 SetEpochDeadline 的阈值）。M1 无 IncrementEpoch 调用点，该层当前惰性。</summary>
    public ulong EpochTicksPerSlice { get; init; }

    public SandboxBudgets()
    {
        MemoryBytes = DefaultMemoryBytes;
        MillisecondsPerSlice = DefaultMillisecondsPerSlice;
        FuelPerSlice = 0;
        EpochTicksPerSlice = 1;
    }

    /// <summary>docs/14 §5 默认预算。</summary>
    public static SandboxBudgets Default => new();
}
