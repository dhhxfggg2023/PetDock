// 素材包导入器的 ABI 角色（2026-10-28）：第三方格式（桌面宠物素材包等）在导入前
// 由插件转换成 PetDock 标准包，再走 PetDataStore 的落盘/登记/换源同一条路。
// 内核仍然不解析素材格式：它只问「这个目录你认不认识」，然后给一个输出目录。
using System;
using System.Threading;

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

/// <summary>转换进度（按帧/条目计）：Done 从 0 开始，Done == Total 表示转换完成。</summary>
public readonly record struct PackImportProgress(int Done, int Total);

/// <summary>转换进度回调（自声明委托，理由同 HostingTypes.cs 的先例）。</summary>
public delegate void PackImportProgressNotify(PackImportProgress progress);

/// <summary>
/// 可选能力：导入器愿意报告转换进度并响应取消时实现它。宿主按接口检测，
/// 不实现 IPackImporterProgress 的旧导入器仍走原来的 <see cref="IPackImporter.Import(string, string)"/>。
/// 契约只增不改：这是给第三方兼容面留的旁路，不是 IPackImporter 的新必选成员。
/// </summary>
public interface IPackImporterProgress
{
    /// <summary>
    /// 转换到 outDir，并按帧/条目报告进度；token 必须在安全点检查。
    /// 抛 OperationCanceledException = 用户取消（宿主会删掉输出目录）。
    /// </summary>
    void Import(string sourceDir, string outDir, PackImportProgressNotify report, CancellationToken token);
}
