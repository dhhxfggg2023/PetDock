// 素材包导入器的 ABI 角色（2026-10-28）：第三方格式（桌面宠物素材包等）在导入前
// 由插件转换成 PetDock 标准包，再走 PetDataStore 的落盘/登记/换源同一条路。
// 内核仍然不解析素材格式：它只问「这个目录你认不认识」，然后给一个输出目录。
using System;

namespace PetDock.Hosting;

/// <summary>
/// 一个能认领第三方素材包目录的导入器。实现方负责把 <paramref name="sourceDir"/>
/// 转成 <paramref name="outDir"/> 下的标准包（petdock-sample.json + anim/*.json + 相对帧路径）。
/// 抛异常 = 导入失败；调用方会删掉输出目录并回滚。
/// </summary>
public interface IPackImporter
{
    /// <summary>插件 id（日志/诊断用）。</summary>
    string Id { get; }

    /// <summary>这个目录是不是本导入器认识的格式。必须只读，不改动 sourceDir。</summary>
    bool CanImport(string sourceDir);

    /// <summary>转换到 outDir。outDir 由宿主创建；实现方可以清空/覆盖其中的内容。</summary>
    void Import(string sourceDir, string outDir);
}
