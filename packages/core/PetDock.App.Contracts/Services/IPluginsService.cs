using System.Collections.Generic;
using System.Threading.Tasks;

namespace PetDock.App;

/// <summary>
/// 插件管理：删除、回收站（列出/恢复/清空单条）。
///
/// 刻意**不含**三件事，理由各自不同：
///   · 「启用/停用」：旧实现是 void，失败只写日志。要进契约，先让它返回成败 —— 那是 R5 的重构，
///     现在包一层「永远成功」等于骗调用方。
///   · 「授权复核」：它要弹窗，是界面职责；服务只该回答「这个插件当前拿到了什么」。
///   · 「安装」：长任务（后台、进度、取消），等 R3 的 IWorkService。
/// </summary>
public interface IPluginsService
{
    /// <summary>回收站里的插件（按删除时间从新到旧）。只读扫描，可在任意线程调用。</summary>
    IReadOnlyList<PluginTrashItem> Trash();

    /// <summary>删除一只用户安装的插件（进回收站，可恢复）。</summary>
    AppResult Remove(string pluginId);

    /// <summary>把回收站里的一只恢复并启用。</summary>
    Task<AppResult> RestoreAsync(string dirName);

    /// <summary>彻底删除回收站里的一只（不可逆；确认由界面层负责）。</summary>
    Task<AppResult> PurgeAsync(string dirName);
}
