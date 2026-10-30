// 开机自启（原规格 18 §2.3 设置页的第一项）。
//
// 事实源是注册表，不是设置文件：Windows 自己读的就是注册表这一处。
// 同一个事实在两处存，迟早漂移（用户手动在 msconfig 里删了那一项，设置文件还写着「开」）。
// 所以设置页显示的勾选状态从注册表读，设置文件里**不**保存这一项。
//
// 落点：HKCU\Software\Microsoft\Windows\CurrentVersion\Run 下的 "PetDock" 值。
// 选 HKCU 而不是 HKLM：HKLM 要管理员权限，普通用户装一个桌宠不该被要求提权；
// HKCU 的 Run 键在登录时由当前用户启动程序，语义正好。
using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using PetDock.Core.Compositor;
using PetDock.Core.Ui;

namespace PetDock.Hosting;

/// <summary>开机自启的注册表读写。</summary>
internal static class StartupRegistration
{
    /// <summary>Run 键的路径（当前用户）。</summary>
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>值名。</summary>
    public const string ValueName = "PetDock";

    /// <summary>当前是否已登记开机自启（读注册表，不读设置文件）。</summary>
    public static bool IsEnabled() => IsEnabledAt(ValueName);

    /// <summary>
    /// 读某个值名是否登记（自检用别的值名，避免动用户真的那一项）。
    /// </summary>
    internal static bool IsEnabledAt(string valueName)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            string? value = key?.GetValue(valueName) as string;
            return !string.IsNullOrWhiteSpace(value);
        }
        catch (Exception error)
        {
            KernelLog.Write($"startup-read-failed {error.GetType().Name}: {error.Message}");
            return false;
        }
    }

    /// <summary>
    /// 登记开机自启。值内容 = 当前进程的可执行文件路径（带引号，路径里有空格时必需）。
    /// 返回 null = 成功；返回字符串 = 失败原因（调用方原样告诉用户，不假装成功）。
    /// </summary>
    public static string? Enable() => EnableAt(ValueName);

    /// <summary>登记某个值名（自检用别的值名）。</summary>
    internal static string? EnableAt(string valueName)
    {
        string? exe = CurrentExecutable();
        if (exe is null)
        {
            return I18n.Get("host.startup.err.no-exe");
        }

        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            key.SetValue(valueName, $"\"{exe}\"", RegistryValueKind.String);
            KernelLog.Write($"startup-enabled path={exe}");
            return null;
        }
        catch (Exception error)
        {
            KernelLog.Write($"startup-enable-failed {error.GetType().Name}: {error.Message}");
            return I18n.Get("host.startup.err.write-failed", error.Message);
        }
    }

    /// <summary>取消开机自启（删掉那个值）。返回 null = 成功；返回字符串 = 失败原因。</summary>
    public static string? Disable() => DisableAt(ValueName);

    /// <summary>删掉某个值名（自检用别的值名）。</summary>
    internal static string? DisableAt(string valueName)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key is not null && key.GetValue(valueName) is not null)
            {
                key.DeleteValue(valueName, throwOnMissingValue: false);
            }

            KernelLog.Write("startup-disabled");
            return null;
        }
        catch (Exception error)
        {
            KernelLog.Write($"startup-disable-failed {error.GetType().Name}: {error.Message}");
            return I18n.Get("host.startup.err.remove-failed", error.Message);
        }
    }

    /// <summary>
    /// 当前进程的可执行文件路径。
    /// 优先用 AppContext.BaseDirectory 拼出同名 exe（WinExe 的产物目录里就是它），
    /// 退回命令行首参（进程启动时的实际路径），再退回进程模块路径。
    /// 三个来源都拿不到就返回 null —— 宁可说「找不到路径」，也不写一个错的启动项。
    /// </summary>
    private static string? CurrentExecutable()
    {
        string? main = AppContext.BaseDirectory;
        if (!string.IsNullOrWhiteSpace(main))
        {
            string candidate = Path.Combine(main, "PetDock.Core.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        string[] argv = Environment.GetCommandLineArgs();
        if (argv.Length > 0 && File.Exists(argv[0]))
        {
            return argv[0];
        }

        try
        {
            using System.Diagnostics.Process self = System.Diagnostics.Process.GetCurrentProcess();
            string? module = self.MainModule?.FileName;
            return !string.IsNullOrWhiteSpace(module) && File.Exists(module) ? module : null;
        }
        catch (Exception error)
        {
            KernelLog.Write($"startup-path-unknown {error.GetType().Name}: {error.Message}");
            return null;
        }
    }
}
