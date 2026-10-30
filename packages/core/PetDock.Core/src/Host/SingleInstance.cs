// 单实例约束（RFC-32 §3）：同一用户同一时刻只允许一个常驻 PetDock。
//
// 为什么用命名互斥体而不是「找同名进程」：进程名可以被改、可以有多个副本（便携 + 安装版）
// 同时存在，而「数据区只有一个」这件事由 `PetDataStore.Resolve()` 决定 —— 真正要互斥的是
// 那个数据区，只是互斥体的名字按用户会话取（Local\ 作用域），不跨会话抢。
//
// 只接收一次失败、不抛：拿不到互斥体（另一个实例在跑）与拿不到权限（ACL 拒绝）在用户眼里
// 是同一件事 —— 不该悄悄再开一个，也不该崩掉。调用方据此直接退出 0。
using System;
using System.Threading;
using PetDock.Core.Compositor;

namespace PetDock.Hosting;

/// <summary>进程级单实例锁。第一次 Acquire 成功即持锁，之后同名的第二次必然失败。</summary>
internal sealed class SingleInstance : IDisposable
{
    /// <summary>生产名字：Local\ 前缀 = 只在当前登录会话内互斥（多用户同时登录各自一只宠物）。</summary>
    public const string DefaultName = @"Local\PetDock.SingleInstance";

    private readonly Mutex _mutex;

    /// <summary>本进程是否抢到了锁（false = 已经有一个实例在跑，或权限不足）。</summary>
    public bool IsFirst { get; }

    /// <summary>抢到的互斥体名字（写日志用，便于判断「被谁挡了」）。</summary>
    public string Name { get; }

    public SingleInstance(string name = DefaultName)
    {
        Name = name;
        try
        {
            _mutex = new Mutex(initiallyOwned: true, name, out bool created);
            IsFirst = created;
        }
        catch (Exception error)
        {
            // 名字格式非法 / 权限不足：按「已有实例」处理——宁可少开一只，不可多开一只。
            KernelLog.Write($"single-instance-open-failed name={Name} {error.GetType().Name}: {error.Message}");
            _mutex = new Mutex();
            IsFirst = false;
        }
    }

    public void Dispose()
    {
        if (IsFirst)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // 不是持锁线程在释放（正常路径不会发生）：互斥体随进程销毁，行为上等价。
            }
        }

        _mutex.Dispose();
    }
}
