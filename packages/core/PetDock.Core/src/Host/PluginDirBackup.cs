// 覆盖安装时那份「旧目录」的落点与识别（2026-10-31）。
//
// TryCommit 换新之前先把旧目录搬成 <id>.bak-<时间戳>；把它搬回去失败时，它就留在插件根下。
// 于是有两件事必须只有一个属主：
//   * 名字怎么写 —— 只在 PathFor 一处；
//   * 怎么认出来 —— IsLeftover 看的就是同一个后缀，免得发现逻辑把一份「失败的备份」
//     当成另一只插件装进宿主（同 id 两只，其中一只永远删不掉、也永远不会再被更新到）。
using System;
using System.Globalization;
using System.IO;

namespace PetDock.Hosting;

internal static class PluginDirBackup
{
    /// <summary>后缀形如 <c>.bak-yyyy-MM-dd-HHmmss</c>（与回收站目录名里的时间戳同一口径）。</summary>
    private const string Marker = ".bak-";

    private const int StampLength = 17;

    /// <summary>旧目录要搬去的地方：插件根下的同级目录（同卷才搬得动，跨卷由调用方兜底）。</summary>
    internal static string PathFor(string pluginDir) =>
        pluginDir + Marker + DateTime.Now.ToString("yyyy-MM-dd-HHmmss", CultureInfo.InvariantCulture);

    /// <summary>这个目录是不是一次覆盖安装留下的备份（是备份就不是一只插件）。</summary>
    internal static bool IsLeftover(string dir)
    {
        string name = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        int at = name.LastIndexOf(Marker, StringComparison.Ordinal);
        return at > 0 && name.Length - at - Marker.Length == StampLength;
    }
}
