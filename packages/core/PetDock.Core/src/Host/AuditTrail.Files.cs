// AuditTrail 的文件生命周期：落盘批次的串行化（文件开/写/收尾在 LogFileWriter）。
//
// 为什么拆出来：D-03c 的单文件 400 行上限。主文件 AuditTrail.cs 讲的是
// 「谁在记、记什么、记成什么行」，这里讲的是「什么时候把队列交给磁盘」——
// 两件事的失败模式与不变量不同。
//
// 不变量：
//   只有后台写入线程碰文件；其它线程只往队列里放行。
//   批次读取与写文件在同一把 commit 锁里（「缓冲空」=「该写的都写完了」）。
//   队列锁（_gate）不覆盖写文件那段 —— 两处并发 commit 会写坏行。
using System;
using PetDock.Core.Compositor;

namespace PetDock.Hosting;

internal sealed partial class AuditTrail
{
    /// <summary>commit 的串行锁（后台线程与调用线程都要落盘，见 Flush）。</summary>
    private readonly object _commitGate = new();

    /// <summary>后台线程：等信号 → 落盘 → 再等。</summary>
    private void Loop()
    {
        while (true)
        {
            _signal.Wait(200);
            Flush();
            lock (_gate)
            {
                if (_completed)
                {
                    break;
                }
            }

            // 必须在本轮 Flush 之后再清信号，否则 Flush 期间来的 Set 会被吞掉。
            // 这是 ManualResetEventSlim：只 Set 不 Reset 时，第一次 Set 之后
            // Wait(200) 永远立即返回，本循环退化成满速自旋。
            _signal.Reset();
        }

        Flush();
        lock (_commitGate)
        {
            _fileWriter.Complete();
        }
    }

    /// <summary>把缓冲里的行写进文件。写失败只计数，不抛。</summary>
    private void Flush()
    {
        // 取批次与写文件必须在**同一把** commit 锁里。
        //
        // 为什么（2026-10-21 实测）：分开放的时候，「缓冲空」不再等价于「行已落盘」——
        // 后台线程取走批次、正卡在写文件之前的那一小段窗口里，
        // 主线程的 FlushNow() 看到 _buffer.Count == 0 就直接返回，
        // 而文件是在第一次写入时才建的 ⇒ 目录里一个文件都没有
        // （判据 ui-audit-trail-file-written 偶发 files=0 就是这么来的）。
        //
        // 锁序只有 _commitGate → _gate 一条（下面 catch 块里也是这个顺序），
        // 没有任何地方反向嵌套 ⇒ 不会死锁。
        lock (_commitGate)
        {
            string[] batch;
            lock (_gate)
            {
                if (_buffer.Count == 0)
                {
                    return;
                }

                batch = _buffer.ToArray();
                _buffer.Clear();
            }

            try
            {
                _fileWriter.Write(batch);
            }
            catch (Exception error)
            {
                lock (_gate)
                {
                    _available = false;
                    _dropped += batch.Length;
                }

                KernelLog.Write("audit-trail-unavailable " + DescribeFailure(error));
            }
        }
    }

    /// <summary>
    /// 流水写不下去时，诊断日志里写什么。
    ///
    /// 类型 + 脱敏后的消息，不带堆栈：
    ///   类型让我们知道是哪一类故障（IOException / UnauthorizedAccessException …）；
    ///   消息让我们知道**为什么**——只记类型的话，流水写不下去永远是「静默故障」，
    ///   2026-10-21 那次排查就只看到一句 IOException，什么也没说。
    ///   消息要脱敏：它常带绝对路径（含用户名），落盘日志与 stderr 都不该有它。
    /// </summary>
    private string DescribeFailure(Exception error)
    {
        string message = (error.Message ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ');
        if (message.Length > 200)
        {
            message = message[..200] + "...";
        }

        return error.GetType().Name + " msg=" + _rules.Text(message);
    }
}
