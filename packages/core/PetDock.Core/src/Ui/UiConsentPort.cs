// UI 线程授权队列泵（RFC-13 §2.4）：弹窗只能在 UI 线程开，而装载期的询问发生在宿主调度线程。
// 为什么必须有这个泵：授权不是「问一句」的函数调用，而是「弹一个模态窗等人回答」——
// 窗体只能在自己的线程上创建，而问它的人（宿主调度线程）是后台线程。这里把「问」拆成
// 「排队 + 阻塞等待」，答案由 UI 线程的 Pump 弹出弹窗取得，再交回等待方。
// 三条硬约束（RFC-13 §2.2/§2.4）：
//   ① 不设超时：宿主活着就必须有人回答，半开的授权等于没回答；
//   ② 没有回答 = 按拒绝处理（端口契约）：UI 已退出时等待方醒来拿到 null；
//   ③ 模态弹窗自带消息循环，计时器在模态期间仍会触发 —— 用 _showing 守卫挡住第二次弹窗。
using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows.Forms;
using PetDock.Core.Compositor;
using PetDock.Hosting;

namespace PetDock.Core.Ui;

/// <summary>UI 侧授权端口：宿主问，UI 答（RFC-13 §2.1 的端口实现在 UI 侧的形态）。</summary>
internal sealed class UiConsentPort : ICapabilityConsentPort
{
    /// <summary>UI 线程的标识。构造时所在线程就是 UI 线程（Program 在主线程上建它）。</summary>
    private readonly int _uiThreadId = Environment.CurrentManagedThreadId;

    private readonly object _lock = new();
    private readonly Queue<Pending> _queue = new();
    private bool _closed;

    /// <summary>正在弹窗。模态弹窗有自己的消息循环，外面的计时器在期间仍会触发 ——
    /// 这个守卫就是用来挡掉那一次触发（否则同一次询问会被弹两遍）。</summary>
    private volatile bool _showing;

    /// <summary>问一次授权。UI 线程上直接弹窗；别的线程上排队并阻塞到有人回答。</summary>
    public ConsentAnswer? Ask(ConsentRequest request)
    {
        if (Environment.CurrentManagedThreadId == _uiThreadId)
        {
            // 已经在 UI 线程：不用排队，直接弹窗（复核走的就是这条路）。
            lock (_lock)
            {
                if (_closed)
                {
                    return null; // UI 已经退出：没有回答 = 按拒绝处理
                }
            }

            return ShowDialog(request);
        }

        var pending = new Pending(request);
        lock (_lock)
        {
            if (_closed)
            {
                return null; // UI 已经退出：没有回答 = 按拒绝处理
            }

            _queue.Enqueue(pending);
        }

        // 阻塞到有人回答，不设超时：宿主活着就必须有人回答。
        pending.Completed.Wait();
        return pending.Answer;
    }

    /// <summary>弹出队首的待答询问。<b>必须在 UI 线程调用。</b></summary>
    public void Pump()
    {
        if (_showing)
        {
            return; // 已经有一个弹窗在模态循环里：不能再弹第二个
        }

        Pending? pending = TakePending();
        if (pending is null)
        {
            return;
        }

        ConsentAnswer? answer;
        try
        {
            answer = ShowDialog(pending.Request);
        }
        catch (Exception ex)
        {
            // 弹窗自己炸了也不能让等待方永远挂着：没有回答 = 按拒绝处理。
            KernelLog.Write($"consent-dialog-failed {ex.GetType().Name}: {ex.Message}");
            answer = null;
        }

        pending.Complete(answer);
    }

    /// <summary>关闭端口：让所有等待方醒来并拿到 null（= 没有回答 = 全拒）。</summary>
    public void Close()
    {
        List<Pending> pending;
        lock (_lock)
        {
            if (_closed)
            {
                return;
            }

            _closed = true;
            pending = new List<Pending>(_queue);
            _queue.Clear();
        }

        foreach (Pending item in pending)
        {
            item.Complete(null);
        }
    }

    /// <summary>当前是否有弹窗在模态循环里（UI 线程自检用）。</summary>
    public bool Showing => _showing;

    /// <summary>排队待答的询问数（UI 线程自检用）。</summary>
    public int PendingCount
    {
        get
        {
            lock (_lock)
            {
                return _queue.Count;
            }
        }
    }

    /// <summary>取队首的待答询问（没有就返回 null）。</summary>
    private Pending? TakePending()
    {
        lock (_lock)
        {
            if (_closed || _queue.Count == 0)
            {
                return null;
            }

            return _queue.Dequeue();
        }
    }

    /// <summary>弹一次授权窗并把回答映射成端口契约的形状。只能在 UI 线程调用。</summary>
    private ConsentAnswer? ShowDialog(ConsentRequest request)
    {
        _showing = true;
        try
        {
            using var dialog = new AuthorizationDialog(
                request.PluginName,
                request.PluginId,
                request.Tier,
                request.Items,
                request.IsReview);
            dialog.ShowDialog();

            // 关窗（「取消」与右上角 ×）= 没有回答 = 按拒绝处理：默认拒绝比默认允许安全
            // （授权弹窗自己的规则也是「取消按拒绝处理」）。
            if (dialog.DialogResult != DialogResult.OK)
            {
                return ConsentAnswer.Reject;
            }

            return dialog.Answer switch
            {
                GrantAnswer.Deny => ConsentAnswer.Reject,
                GrantAnswer.Once => new ConsentAnswer(ConsentScope.Once, dialog.SelectedIds),
                _ => new ConsentAnswer(ConsentScope.Session, dialog.SelectedIds),
            };
        }
        finally
        {
            _showing = false;
        }
    }

    /// <summary>一次排队中的询问：请求 + 交回答案的槽。</summary>
    private sealed class Pending
    {
        public Pending(ConsentRequest request) => Request = request;

        public ConsentRequest Request { get; }

        /// <summary>等待方被唤醒的信号。不设超时：宿主活着就必须有人回答。</summary>
        public ManualResetEventSlim Completed { get; } = new(false);

        /// <summary>答案（先写答案再唤醒，等待方读到的一定是写好的值）。</summary>
        public ConsentAnswer? Answer { get; private set; }

        /// <summary>交回答案并唤醒等待方。重复交回只算第一次（关闭端口时也可能来一次）。</summary>
        public void Complete(ConsentAnswer? answer)
        {
            if (Interlocked.Exchange(ref _answered, 1) != 0)
            {
                return;
            }

            Answer = answer;
            Completed.Set();
        }

        private int _answered;
    }
}
