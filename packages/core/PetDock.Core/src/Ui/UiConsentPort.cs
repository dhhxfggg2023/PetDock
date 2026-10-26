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

    /// <summary>已经打开的那个授权窗（UI 线程写；模态期间后来的请求并进它）。</summary>
    private AuthorizationDialog? _dialog;

    /// <summary>当前窗里承载的请求（顺序 = 弹窗里的请求顺序；模态期间会增长）。</summary>
    private List<Pending> _open = new();

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

    /// <summary>弹出待答询问。<b>必须在 UI 线程调用。</b>
    /// 2026-10-26 真机故障（三只插件同时要授权 → 连弹三个窗）：这里一次取走**全部**待答请求，
    /// 只开一个窗、按插件分段；答案按每份请求各自的勾选集分别落账。
    /// 真机第二幕（同日 16:56）：三只插件是隔 0.4–0.6 秒**先后**申请——开窗那一刻队里只有一份，
    /// 另两份在模态循环里才到，于是仍连弹三个。既然模态期间计时器仍在触发（约束③），
    /// 就让后来的请求并进**已经打开**的窗：一份答案，各插件一段。</summary>
    public void Pump()
    {
        if (_showing)
        {
            AbsorbPending(); // 窗还开着：新来的请求并进去，不另开窗
            return;
        }

        List<Pending> batch = TakeAllPending();
        if (batch.Count == 0)
        {
            return;
        }

        _open = batch;
        _showing = true;
        var requests = new ConsentRequest[batch.Count];
        for (int i = 0; i < batch.Count; i++)
        {
            requests[i] = batch[i].Request;
        }

        var dialog = new AuthorizationDialog(requests);
        try
        {
            _dialog = dialog; // 让模态期间的 AbsorbPending 找得到它
            dialog.ShowDialog();

            // 关窗（「取消」与右上角 ×）= 没有回答 = 按拒绝处理：默认拒绝比默认允许安全
            // （授权弹窗自己的规则也是「取消按拒绝处理」）。
            if (dialog.DialogResult != DialogResult.OK)
            {
                foreach (Pending pending in _open)
                {
                    pending.Complete(ConsentAnswer.Reject);
                }

                return;
            }

            // _open 可能比 batch 长（模态期间并进来的那些），下标与弹窗里的请求一一对应。
            for (int i = 0; i < _open.Count; i++)
            {
                _open[i].Complete(MapAnswer(dialog.Answer, dialog.SelectedIdsFor(i)));
            }
        }
        catch (Exception ex)
        {
            // 弹窗自己炸了也不能让等待方永远挂着：没有回答 = 按拒绝处理。
            KernelLog.Write($"consent-dialog-failed {ex.GetType().Name}: {ex.Message}");
            foreach (Pending pending in _open)
            {
                pending.Complete(null);
            }
        }
        finally
        {
            _dialog = null;
            _open = new List<Pending>();
            _showing = false;
            dialog.Dispose();
        }
    }

    /// <summary>把模态循环期间新到的请求并进已打开的窗（UI 线程；没有窗时什么都不做）。</summary>
    private void AbsorbPending()
    {
        AuthorizationDialog? dialog = _dialog;
        if (dialog is null)
        {
            return;
        }

        List<Pending> late = TakeAllPending();
        for (int i = 0; i < late.Count; i++)
        {
            _open.Add(late[i]);
            dialog.AddRequest(late[i].Request);
        }
    }

    /// <summary>把弹窗的回答映射成端口契约的形状（单份请求）。范围统一、勾选集各自一份。</summary>
    internal static ConsentAnswer MapAnswer(GrantAnswer answer, IReadOnlyList<string> granted) => answer switch
    {
        GrantAnswer.Deny => ConsentAnswer.Reject,
        GrantAnswer.Once => new ConsentAnswer(ConsentScope.Once, granted),
        _ => new ConsentAnswer(ConsentScope.Session, granted),
    };

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

    /// <summary>取走全部待答询问（没有就返回空表）。一次取走是合批的前提：
    /// 同一个窗里能问几个，取决于调用这一刻队列里有几个。</summary>
    private List<Pending> TakeAllPending()
    {
        lock (_lock)
        {
            if (_closed || _queue.Count == 0)
            {
                return new List<Pending>();
            }

            var batch = new List<Pending>(_queue);
            _queue.Clear();
            return batch;
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
