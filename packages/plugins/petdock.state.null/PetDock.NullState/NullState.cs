using System.Text.Json.Nodes;
using PetDock.Core.PetRuntime;

namespace PetDock.Builtins;

/// <summary>
/// 内置 NullState（贡献点 id "petdock.state.null"，原规格 11 §13：「全部状态丢弃」）。
/// 可判定形态（原规格 11:312）：Read 恒 null、ReadAll 恒空、Write 丢弃补丁、
/// CommitAsync 无副作用、Migrate 把迁移前快照一并丢弃——
/// 宿主持久层可以据此断言「真的什么都不留」。
/// </summary>
public sealed class NullState : IPetState
{
    private static readonly IReadOnlyDictionary<string, JsonNode?> Empty =
        new Dictionary<string, JsonNode?>();

    public int SchemaVersion => 1;

    public IReadOnlyDictionary<string, JsonNode?> ReadAll() => Empty;

    public JsonNode? Read(string key) => null;

    public void Write(IReadOnlyDictionary<string, JsonNode?> patch)
    {
        // 「全部状态丢弃」：补丁不落任何内存结构。
    }

    public Task CommitAsync() => Task.CompletedTask;

    public void Rollback()
    {
        // 无状态可回滚。
    }

    public IReadOnlyDictionary<string, JsonNode?> Migrate(
        int from, int to, IReadOnlyDictionary<string, JsonNode?> snapshot) => Empty;
}
