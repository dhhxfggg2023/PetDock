// 主题订阅（R2c）：控件不许自己订阅 Theme.Changed，否则「打开 20 次页面，事件处理器 20 份」
// 这条旧层的老毛病会原封不动长回来（见 research/ui-lessons.md 的订阅类教训）。
//
// 做法：控件在构造函数里 ThemeHub.Attach(this)，之后只实现 ApplyTheme。
// 这里是**弱引用**表：控件被丢掉之后下一次 Repaint 会自动摘除它 ——
// 判据 ui-controls-subscriptions-bounded 拿 200 个控件来回建/丢，看这张表会不会长大。
using System;
using System.Collections.Generic;

namespace PetDock.Ui.Design;

/// <summary>跟着主题重画的东西。</summary>
public interface IThemed
{
    /// <summary>按一套配色重刷自己的观感（不重建控件树）。</summary>
    void ApplyTheme(Palette palette);
}

/// <summary>主题改变时统一通知所有活着的控件；弱引用，不延长它们的寿命。</summary>
public static class ThemeHub
{
    private static readonly List<WeakReference<IThemed>> Watchers = new();

    static ThemeHub()
    {
        Theme.Changed += (_, _) => Repaint();
    }

    /// <summary>活着的订阅者数量（判据用：它必须随手里的控件一起降下来）。</summary>
    public static int Count => Alive();

    /// <summary>登记一个控件。重复登记同一个实例不再加一条。</summary>
    public static void Attach(IThemed target)
    {
        ArgumentNullException.ThrowIfNull(target);
        Prune();
        foreach (WeakReference<IThemed> watcher in Watchers)
        {
            if (watcher.TryGetTarget(out IThemed? existing) && ReferenceEquals(existing, target))
            {
                return;
            }
        }

        Watchers.Add(new WeakReference<IThemed>(target));
    }

    /// <summary>按当前配色重刷所有活着的订阅者。</summary>
    public static void Repaint()
    {
        Palette palette = Theme.Current;
        int kept = 0;
        for (int i = 0; i < Watchers.Count; i++)
        {
            if (Watchers[i].TryGetTarget(out IThemed? target))
            {
                Watchers[kept++] = Watchers[i];
                target.ApplyTheme(palette);
            }
        }

        Watchers.RemoveRange(kept, Watchers.Count - kept);
    }

    private static int Alive()
    {
        int alive = 0;
        foreach (WeakReference<IThemed> watcher in Watchers)
        {
            if (watcher.TryGetTarget(out _))
            {
                alive++;
            }
        }

        return alive;
    }

    private static void Prune()
    {
        int kept = 0;
        for (int i = 0; i < Watchers.Count; i++)
        {
            if (Watchers[i].TryGetTarget(out _))
            {
                Watchers[kept++] = Watchers[i];
            }
        }

        Watchers.RemoveRange(kept, Watchers.Count - kept);
    }
}
