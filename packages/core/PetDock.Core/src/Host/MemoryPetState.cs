// RFC-24 §6.2：没有状态插件时宿主用的内建内存态（schema 1）。
// 它不是「NullState 的别名」：NullState 是插件声明的「全部丢弃」，本类是宿主自己的缓冲区 ——
// 落盘层需要一个能读回自己写过东西的内层实现，而 NullState 的读恒空。
using System.Collections.Generic;
using System.Text.Json.Nodes;
using PetDock.Core.PetRuntime;

namespace PetDock.Hosting;

/// <summary>宿主内建的内存态：只在本进程内存里，落盘交给 <see cref="PersistentPetState"/>。</summary>
internal sealed class MemoryPetState : IPetState
{
    private readonly Dictionary<string, JsonNode?> _values = new(StringComparer.Ordinal);

    /// <summary>docs/11:198：内建实现的版本号固定为 1，升级它要走迁移（RFC-24 §6.4）。</summary>
    public int SchemaVersion => 1;

    public IReadOnlyDictionary<string, JsonNode?> ReadAll() => new Dictionary<string, JsonNode?>(_values, StringComparer.Ordinal);

    public JsonNode? Read(string key) => !string.IsNullOrEmpty(key) && _values.TryGetValue(key, out JsonNode? value) ? value : null;

    public void Write(IReadOnlyDictionary<string, JsonNode?> patch)
    {
        System.ArgumentNullException.ThrowIfNull(patch);
        foreach (KeyValuePair<string, JsonNode?> pair in patch)
        {
            if (string.IsNullOrEmpty(pair.Key))
            {
                throw new System.ArgumentException("state key must not be empty", nameof(patch));
            }

            _values[pair.Key] = pair.Value?.DeepClone();
        }
    }

    public System.Threading.Tasks.Task CommitAsync() => System.Threading.Tasks.Task.CompletedTask;

    public void Rollback()
    {
        // 缓冲区由外层（PersistentPetState）持有，本层没有独立事务可回滚。
    }

    /// <summary>schema 1 是当前版本，恒等式迁移：返回入参的拷贝，不 mutate。</summary>
    public IReadOnlyDictionary<string, JsonNode?> Migrate(
        int from, int to, IReadOnlyDictionary<string, JsonNode?> snapshot)
    {
        System.ArgumentNullException.ThrowIfNull(snapshot);
        var copy = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, JsonNode?> pair in snapshot)
        {
            copy[pair.Key] = pair.Value?.DeepClone();
        }

        return copy;
    }
}
