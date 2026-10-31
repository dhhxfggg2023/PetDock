// 覆盖安装那份旧目录的去处（2026-10-31，从 PluginInstaller.cs 拆出：那里已到 400 行上限）。
//
// 换新失败、且旧目录搬不回去时，这份备份不能留在插件根下 —— 那里它会被当成同 id 的
// 第二只插件背后的幽灵副本；唯一还说得过去的地方是回收站：用户看得见、也拿得回。
using System;
using System.IO;
using PetDock.Core.Compositor;

namespace PetDock.Hosting;

internal sealed partial class PluginInstaller
{
    /// <summary>把一份不该留在插件根下的旧目录挪进回收站；挪不动就如实记一行（不假装成功）。</summary>
    private string TryPark(string pluginId, string dir)
    {
        try
        {
            return PluginInventory.Park(dir, pluginId, _userRoot);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            KernelLog.Write($"plugin-install-park-failed id={pluginId} {error.GetType().Name}");
            return "failed";
        }
    }
}
