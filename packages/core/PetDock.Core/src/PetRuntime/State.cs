// 原规格 11 §6「State 接口（状态层）」的 C# 绑定。IPetState 同名。
// 三条约束（原规格 11:211-214）逐条落在注释上，且都由宿主强制执行：
// - 单键值不得超过 8 KiB（超出应转资源库、只写引用）—— 超限时宿主拒绝 Commit（见 StateStore）；
// - write 之后到 commit 之间是内存中的修改；崩溃 = 缓冲区全丢、磁盘无影响（这就是 ABI 的崩溃恢复语义）；
// - migrate 必须是纯函数：允许返回新对象，不允许 mutate 入参。
using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace PetDock.Core.PetRuntime;

/// <summary>原规格 11 §6 IPetState：每实例恰好 1 个（可为 NullState）。</summary>
public interface IPetState
{
    /// <summary>原规格 11:198 `readonly schemaVersion`。</summary>
    int SchemaVersion { get; }

    /// <summary>快照读（原规格 11:200）。返回的载荷不得被调用方 mutate（实现可返回副本）。</summary>
    IReadOnlyDictionary<string, JsonNode?> ReadAll();

    /// <summary>按 key 读；不存在返回 null（原规格 11:201）。</summary>
    JsonNode? Read(string key);

    /// <summary>写入补丁 —— **只在缓冲区**，不落盘（原规格 11:202）。</summary>
    void Write(IReadOnlyDictionary<string, JsonNode?> patch);

    /// <summary>
    /// 唯一被允许的持久化入口（原规格 11:203）。原规格 11:212：单键值 &gt; 8 KiB 时**宿主拒绝 commit**
    /// （不是实现方自行裁剪）—— 所以实现抛 <see cref="StateLimitException"/> 即宿主拒绝的落点。
    /// </summary>
    System.Threading.Tasks.Task CommitAsync();

    /// <summary>丢弃自上次 commit 以来的全部缓冲（原规格 11:204）。</summary>
    void Rollback();

    /// <summary>
    /// 跨会话进化（原规格 11:207）：1 ≤ to ≤ current，宿主保证串行调用。
    /// 纯函数：返回新载荷，不 mutate 入参（原规格 11:214）。
    /// </summary>
    IReadOnlyDictionary<string, JsonNode?> Migrate(
        int from, int to, IReadOnlyDictionary<string, JsonNode?> snapshot);
}

/// <summary>
/// 原规格 11:212 的 8 KiB 上限：宿主强制执行，越限即拒绝 commit。
/// 抛它的实现方是在**如实上报**「宿主必须拒绝」，不是自己在裁剪数据。
/// </summary>
public sealed class StateLimitException : System.Exception
{
    public StateLimitException(string key, int bytes, int limit)
        : base($"state key '{key}' value is {bytes} bytes, exceeding the {limit}-byte per-key limit (原规格 11 §6)")
    {
        Key = key;
        Bytes = bytes;
        Limit = limit;
    }

    public string Key { get; }

    public int Bytes { get; }

    public int Limit { get; }
}
