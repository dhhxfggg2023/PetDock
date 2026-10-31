using System.Threading.Tasks;

namespace PetDock.App;

/// <summary>装更新停在哪一步（三种必须分开：把「已登记」显示成「装好了」会让用户空等一次开机）。</summary>
public enum UpdateInstallStage
{
    /// <summary>下载/校验/解压任意一步没成：Message 是原因，磁盘上什么都没变。</summary>
    Failed = 0,

    /// <summary>已经解好、标记也写下了，等一次重启（立即，或用户下次自己启动）。</summary>
    Queued = 1,

    /// <summary>用户选了「立即重启更新」：交接完成，进程正在退出。</summary>
    Restarting = 2,
}

/// <summary>一次「检查更新」的结局。三种互斥：有新版本 / 已是最新 / 没查到。</summary>
public sealed record UpdateCheck(string Current, string? Latest, long Bytes, string Notes, string? Error)
{
    /// <summary>确实有新版本（且没有查询错误）。</summary>
    public bool HasUpdate => Error is null && Latest is not null;

    /// <summary>查到了、而且就是当前版本。</summary>
    public bool UpToDate => Error is null && Latest is null;
}

/// <summary>「下载 / 装」这一步的结局。</summary>
public sealed record UpdateInstall(UpdateInstallStage Stage, string? Message, UpdateCheck? Check)
{
    public static UpdateInstall Fail(string message) => new(UpdateInstallStage.Failed, message, null);

    /// <summary>下载并解好了：这时「立即重启」与「稍后更新」两个按钮才该出现。</summary>
    public static UpdateInstall Staged(UpdateCheck check) => new(UpdateInstallStage.Queued, null, check);

    /// <summary>交接已完成，进程即将退出。</summary>
    public static UpdateInstall Restart(UpdateCheck check) => new(UpdateInstallStage.Restarting, null, check);
}

/// <summary>
/// 程序自身更新。三条命令都**只在被调用时联网**，且都在后台跑（调用方负责 await）。
///
/// 与旧层 UpdateCheckLine / UpdateInstallLine 的关系：这里是一份**独立**的值类型，
/// 旧层的同名类型会在 R7 一并删除；过渡期两边各自持有，映射只发生在宿主那一层（UiHost.AppContracts.cs）。
/// 这么做的理由：把旧类型搬过来的代价是 10 个文件、70 处引用，风险远大于收益，
/// 而双轨期本来就允许两份形状同时存在 —— 前提是**同一时刻只有一个实现**。
/// </summary>
public interface IUpdateService
{
    /// <summary>已下载好、等着装的那一份版本号（没有就 null）。来自磁盘上的事实，不是界面记忆。</summary>
    string? PendingVersion { get; }

    /// <summary>上一次启动装没装上（null = 没有可说的）。</summary>
    string? LastReport { get; }

    /// <summary>检查有没有新版本（联网一次）。</summary>
    Task<UpdateCheck> CheckAsync();

    /// <summary>下载 → 校验 → 解压到暂存区 → 记下待安装的那一份（**不替换任何正在运行的文件**）。</summary>
    Task<UpdateInstall> DownloadAsync();

    /// <summary>装：restartNow=true 立即交接并退出；false 只留标记，等下次启动。</summary>
    Task<UpdateInstall> InstallAsync(bool restartNow);

    /// <summary>在资源管理器里打开下载好的新版本所在目录。</summary>
    AppResult OpenFolder();
}
