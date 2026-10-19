// 常驻 UI 宿主（RFC-12 §2.2）：把托盘、主控面板、宠物窗口串成一条生命周期。
// 为什么是 ApplicationContext 而不是 Application.Run(Form)：宠物窗口只是众多窗口之一，
// 用某个窗体当消息循环的宿主会让「关闭面板 = 退出程序」——docs/18 §2.5 明确禁止这种行为。
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using PetDock.Core.Compositor;
using PetDock.Core.Input;
using PetDock.Core.PetRuntime;
using PetDock.Core.Sandbox;
using PetDock.Hosting;

namespace PetDock.Core.Ui;

internal sealed class UiHost : ApplicationContext
{
    private readonly PetInstance? _pet;
    private readonly PluginHost? _host;
    private readonly LayeredFrameWindow _window;
    private readonly PetFrameSource? _windowSource;
    private readonly TrayHost _tray;
    private ControlPanel? _panel;
    private PerfSummary _perf = PerfSummary.Empty;
    private KeyboardHook? _keyHook;
    private bool _hooksPaused;
    private bool _disposed;

    public UiHost(LayeredFrameWindow window, PetInstance? pet, PluginHost? host, PetFrameSource? source = null)
    {
        _window = window;
        _pet = pet;
        _host = host;
        _windowSource = source;

        _tray = new TrayHost(
            petName: pet?.Id ?? "(无)",
            toggleAll: ToggleAll,
            openPanel: OpenPanel,
            movePet: () => _window.MoveToCenter(),
            pausePet: PausePet,
            hidePet: HidePet,
            removePet: RemovePet,
            pauseHooks: PauseHooks,
            exit: ExitApp);

        _tray.SetPetCount(pet is null ? 0 : 1);

        // 钩子必须在窗口句柄已创建、且有消息泵之后装：低级钩子回调由安装线程的消息循环派发。
        // 这里用 HandleCreated 的等价时机——UiHost 构造时窗口句柄已经存在（Program 里先建窗口）。
        InstallKeyboardHookIfGranted();
    }

    /// <summary>键盘钩子是否装着（自检与诊断用；不暴露可写入口）。</summary>
    public bool KeyboardHookInstalled => _keyHook?.IsInstalled == true;

    /// <summary>托盘（自检用）。</summary>
    public TrayHost Tray => _tray;

    /// <summary>当前面板实例（未打开时为 null；自检用它断言单实例）。</summary>
    public ControlPanel? Panel => _panel;

    /// <summary>面板是否打开（自检用）。</summary>
    public bool PanelOpen => _panel is not null && !_panel.IsDisposed;

    /// <summary>全局输入钩子是否已按用户要求停止（docs/18 §1 强制项）。</summary>
    public bool HooksPaused => _hooksPaused;

    /// <summary>打开主控面板；已存在则只激活（§2.5 单实例）。</summary>
    public void OpenPanel()
    {
        if (_panel is not null && !_panel.IsDisposed)
        {
            _panel.Activate();
            return;
        }

        _panel = new ControlPanel(Snapshot, showWindow: false);
        _panel.FormClosed += (_, _) =>
        {
            // 关闭面板不退出程序（§2.5）：只是把它丢掉，托盘还在。
            _panel?.Dispose();
            _panel = null;
        };
        _panel.Show();
    }

    /// <summary>取一次快照（面板每次重绘都调用它，不缓存）。</summary>
    public UiSnapshot Snapshot() =>
        UiRoster.Build(_pet, _host, _perf);

    /// <summary>触发一次性能采样并把结果留在快照里（面板的采样按钮走这里）。</summary>
    public void NotePerf(PerfSummary perf) => _perf = perf ?? PerfSummary.Empty;

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

    private void HidePet() => _window.Hide();

    private void RemovePet()
    {
        // 移除 = 关掉窗口并从界面消失。真正释放实例由进程退出负责（实例持有 CAS 引用）。
        _window.Hide();
    }

    private void PauseHooks()
    {
        // docs/18:22 的字面要求是「立即卸载钩子线程（不可由插件恢复）」——所以这里是 Uninstall
        // 而不是 Pause：暂停仍然把回调注册在 OS 里，那不叫卸载。
        // 不可恢复：_keyHook 置空，且没有任何代码路径会再调用 InstallKeyboardHookIfGranted
        // （它只在构造期跑一次）。插件即便重载也不会让钩子回来，重启宿主才会。
        int installed = _keyHook?.IsInstalled == true ? 1 : 0;
        _hooksPaused = true;
        _keyHook?.Dispose();
        _keyHook = null;
        KernelLog.Write($"tray-pause-hooks uninstalled={installed} resumable=0");
    }

    /// <summary>
    /// 按 docs/16:56「设备钩子只在插件拥有权限后激活」安装键盘钩子。
    /// 没有任何插件持 `input.keyboardHook` 时**不装**——一个装了却没人要的全局钩子，
    /// 只是白拿用户的每一次按键。
    /// </summary>
    private void InstallKeyboardHookIfGranted()
    {
        if (_keyHook is not null || _pet is null || _host is null)
        {
            return;
        }

        bool granted = false;
        try
        {
            foreach (PluginHandle handle in _host.HandlesSnapshot())
            {
                if (handle.Leases.Any(l => l.Active
                    && string.Equals(l.Id, CapabilityIds.InputKeyboardHook, StringComparison.Ordinal)))
                {
                    granted = true;
                    break;
                }
            }
        }
        catch (TimeoutException)
        {
            KernelLog.Write("key-hook-skip reason=scheduler-stalled");
            return;
        }

        if (!granted)
        {
            KernelLog.Write("key-hook-skip reason=no-grantee");
            return;
        }

        if (_windowSource is null)
        {
            KernelLog.Write("key-hook-skip reason=no-frame-source");
            return;
        }

        var hook = new KeyboardHook(stroke => _windowSource.PublishKeyStroke(stroke.VKey, stroke.Modifiers, stroke.Down));
        if (!hook.Install())
        {
            KernelLog.Write("key-hook-install-failed (docs/14:110 的固定项仍在，用户可随时停止)");
            hook.Dispose();
            return;
        }

        _keyHook = hook;
        KernelLog.Write("key-hook-installed reason=capability-granted");
    }

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

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _keyHook?.Dispose();
            _keyHook = null;
            _panel?.Dispose();
            _panel = null;
            _tray.Dispose();
        }

        base.Dispose(disposing);
    }
}
