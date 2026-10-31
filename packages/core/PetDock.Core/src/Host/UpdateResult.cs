// 搬运工留下的那一行结局的形状。
//
// 从 UpdateStage.cs 拆出来：那个文件已经顶到 D-03c 的 400 行上限，而这里说的
// 是「结局有哪几种」这件事本身 —— 与暂存目录的读写不是同一件事。
//
// 为什么要有 Kind 而不是一个 bool：几种结局对用户的下一步完全不同 ——
// 装好了（不用做什么）、装好了但没能自己开起来（这次是用户手动点开的）、
// 装失败了（再点一次下载）、回滚了（旧版本已经回来，可以重试）、
// 回滚也只回了一半（要人工拿 zip 重新覆盖）。把回滚说成「安装失败」是在撒谎。
using System;

namespace PetDock.Hosting;

/// <summary>
/// 搬运工留下的那一行结局：<c>ok 0.4.1</c> / <c>fail sha256</c> /
/// <c>rolled-back 12</c> / <c>rollback-failed 3</c>。
/// </summary>
internal readonly record struct UpdateResult(string Kind, string Text)
{
    /// <summary>装好了，而且新版本是自己起来的。</summary>
    internal const string Applied = "ok";

    /// <summary>装好了，但搬运工没能把它开回来（用户接着会自己点开一次）。</summary>
    internal const string AppliedNotRestarted = "ok-not-restarted";

    /// <summary>装成功了 —— 不管最后有没有自己开起来。</summary>
    internal bool Ok => Kind is Applied or AppliedNotRestarted;

    /// <summary>装完之后那个新进程是自己起来的（不是用户手动点开的）。</summary>
    internal bool Restarted => string.Equals(Kind, Applied, StringComparison.Ordinal);

    /// <summary>这是回滚的结局（不是安装的结局）。</summary>
    internal bool RollBack => Kind is "rolled-back" or "rollback-failed";

    /// <summary>回滚把每一个要换回去的文件都换回去了。</summary>
    internal bool Complete => string.Equals(Kind, "rolled-back", StringComparison.Ordinal);
}
