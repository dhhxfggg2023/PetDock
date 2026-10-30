// M1 契约 §5（交接档案）：沙箱事件是内核与上层之间唯一的
// 异步通报通道。事件只携带「发生了什么」，不携带任何素材/插件生态词汇（原规格 40 D-03b）。
// P-05（原规格 01 §5）：沙箱内的任何异常都必须在这里落成事件，绝不能穿透进宿主事件循环。
using System.Text;

namespace PetDock.Core.Sandbox;

/// <summary>
/// 沙箱事件类别。语义与 原规格 14 §5、原规格 11 §6 对齐：
/// 审计（每次授予/拒绝各一条）、越限（预算超出一律先中断再上报）、客体故障。
/// </summary>
public enum SandboxEventKind
{
    /// <summary>审计事件：能力授予/拒绝/撤销各记一条（原规格 14 §2「每次授予、拒绝都记审计」）。</summary>
    Audit,

    /// <summary>未声明能力被调用：拒绝 + 审计（契约 §5 验收点 2）。</summary>
    CapabilityDenied,

    /// <summary>预算越限：内存/每片 CPU/墙钟任一超出（契约 §5 验收点 3）。</summary>
    BudgetExceeded,

    /// <summary>客体侧故障：模块校验失败、实例化失败、运行期 trap、宿主回调内异常。</summary>
    GuestFault,
}

/// <summary>
/// 一条沙箱事件。payload 为纯文本键值（"k=v; k=v"），由产生方保证不含黑名单词汇。
/// </summary>
public readonly record struct SandboxEvent(
    SandboxEventKind Kind,
    string Detail);

/// <summary>
/// 事件落点。宿主（t4）实现它把事件接到 KernelLog / 审计文件；
/// 沙箱本身永远只调 Emit，不直接写日志 —— 落点属于宿主策略，不属于基座。
/// </summary>
public interface ISandboxEventSink
{
    void Emit(SandboxEvent evt);
}

/// <summary>
/// 内部落点工具：把键值对格式化成统一 payload，供本命名空间内各类型复用。
/// </summary>
internal static class SandboxEventPayload
{
    public static string Build(params (string Key, object? Value)[] pairs)
    {
        var sb = new StringBuilder();
        foreach (var (key, value) in pairs)
        {
            if (sb.Length > 0) sb.Append("; ");
            sb.Append(key).Append('=').Append(value ?? "<null>");
        }
        return sb.ToString();
    }
}
