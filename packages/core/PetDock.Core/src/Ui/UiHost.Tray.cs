// 托盘命令的实现（docs/18 §1 的五项固定入口）。
// 为什么单独一个文件：这些命令是「用户的一次点击」到「内核的一次动作」的映射，
// 与授权链路无关，混在一起会让人读授权代码时读到托盘逻辑。
using System.Windows.Forms;
using PetDock.Core.Compositor;
using PetDock.Core.PetRuntime;

namespace PetDock.Core.Ui;

internal sealed partial class UiHost
{
    /// <summary>显示/隐藏宠物窗口。</summary>
    private void ToggleAll()
    {
        if (_window.Visible)
        {
            _window.Hide();
        }
        else
        {
            _window.Show();
            _window.BringToFrontOfTopmost();
        }
    }

    /// <summary>暂停/恢复宠物实例。</summary>
    private void PausePet()
    {
        if (_pet is null)
        {
            return;
        }

        if (_pet.State == InstanceState.Running)
        {
            _pet.Suspend("tray-pause");
            return;
        }

        if (_pet.State == InstanceState.Suspended)
        {
            _pet.Resume("tray-resume");
        }
    }

    /// <summary>隐藏宠物窗口（窗口仍在，只是不可见）。</summary>
    private void HidePet() => _window.Hide();

    /// <summary>
    /// 移除宠物。真正释放实例由进程退出负责（实例持有 CAS 引用）——
    /// 所以这里只是让窗口从界面消失，不去碰实例的生命周期。
    /// </summary>
    private void RemovePet()
    {
        _window.Hide();
    }

    /// <summary>
    /// 停止全局输入钩子（docs/18 §1 强制项，不可隐藏）。
    /// docs/18:22 的字面要求是「立即卸载钩子线程（不可由插件恢复）」——所以这里是 Uninstall
    /// 而不是 Pause：暂停仍然把回调注册在 OS 里，那不叫卸载。
    /// 不可恢复：_keyHook 置空，且没有任何代码路径会再装回钩子（滴答器里的安装点也认
    /// _hooksPaused 这个标记）。插件即便重载也不会让钩子回来，重启宿主才会。
    /// </summary>
    private void PauseHooks()
    {
        int installed = _keyHook?.IsInstalled == true ? 1 : 0;
        _hooksPaused = true;
        _keyHook?.Dispose();
        _keyHook = null;
        KernelLog.Write($"tray-pause-hooks uninstalled={installed} resumable=0");
    }

    /// <summary>退出确认（docs/18 §1：退出是固定项，但要有一次确认）。</summary>
    private void ExitApp()
    {
        DialogResult answer = MessageBox.Show(
            "确定退出 PetDock？宠物会从桌面消失。",
            "PetDock — 退出确认",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button2);
        if (answer == DialogResult.Yes)
        {
            ExitThread();
        }
    }
}
