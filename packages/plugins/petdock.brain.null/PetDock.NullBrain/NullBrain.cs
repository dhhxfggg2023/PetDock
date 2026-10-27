using PetDock.Core.PetRuntime;

namespace PetDock.Builtins;

/// <summary>
/// 内置 NullBrain（贡献点 id "petdock.brain.null"，原规格 11 §13：「不做任何事」）。
/// 可判定形态（原规格 11:311）：Tick 恒返回空 BrainDirective[]，OnEvent 丢弃，无任何副作用——
/// 宿主仲裁器的 Received/Applied 计数可以据此断言「真的什么都没做」。
/// </summary>
public sealed class NullBrain : IBrain
{
    /// <summary>运行期贡献点 id：逐字等于 原规格 11:311（连字符在这里合法，不是 manifest id）。</summary>
    public InterfaceMeta Meta => new("petdock.brain.null", "1.0.0");

    public Task InitAsync(BrainContext context) => Task.CompletedTask;

    public IReadOnlyList<BrainDirective> Tick(int dtMs, WorldSnapshot world) =>
        Array.Empty<BrainDirective>();

    public void OnEvent(PetEvent evt)
    {
        // 「不做任何事」：事件直接丢弃。
    }

    public Task DisposeAsync() => Task.CompletedTask;
}
