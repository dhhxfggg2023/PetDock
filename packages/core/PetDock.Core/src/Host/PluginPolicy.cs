// 插件装载与授权策略（2026-10-28 用户要求：双击 exe 的第一屏是宠物，不是授权对话框）。
//
// 内核不知道任何具体生态的名字。两件事都由通用规则和插件自己的清单决定：
//
//   1. 默认装不装：manifest.defaultEnabled（缺省 true）。外部生态的适配器在清单里写 false，
//      装是装了，启动时不装载；用户在面板里显式打开才装载 —— 启动期于是没有任何授权请求。
//   2. 能不能静默授予：只给**随包发行**的插件，且只给无害能力（见 Harmless）。键盘钩子、
//      持久化写入这类能力第一次用时仍然要问；那是用户按下的那一次，不是启动打扰。
//
// 用户装进来的插件永远走询问：安装动作不是能力授权。名单因此不需要维护，也不会漂移。
using System;
using System.Collections.Generic;

namespace PetDock.Hosting;

internal static class PluginPolicy
{
    /// <summary>无需打断用户就能授予的能力：只读、进程内、不落盘。</summary>
    private static readonly HashSet<string> Harmless = new(StringComparer.Ordinal)
    {
        "input.mousePosition",
    };

    /// <summary>
    /// 没有任何用户记录时的默认装载决定：只有随包发行、且清单没写 false 的插件才默认开。
    /// 用户自己装进来的插件默认停用 —— 启动静默，装完那一刻由安装流程显式启用。
    /// </summary>
    public static bool DefaultEnabled(PluginManifest manifest, bool builtin) => builtin && manifest.DefaultEnabled;

    /// <summary>清单声明的能力是否全都无需询问。</summary>
    public static bool CanGrantSilently(IReadOnlyList<string> capabilities)
    {
        foreach (string id in capabilities)
        {
            if (!Harmless.Contains(id))
            {
                return false;
            }
        }
        return true;
    }
}
