namespace PetDock.App;

/// <summary>
/// 回收站里的一只插件。<see cref="DirName"/> 是恢复/彻底删除的稳定键 ——
/// 它来自回收站目录名，而不是插件清单（清单可能已不可读）。
/// </summary>
public sealed record PluginTrashItem(
    string DirName,
    string Id,
    string Name,
    string Version,
    long Bytes,
    string RemovedAt,
    bool Restorable);
