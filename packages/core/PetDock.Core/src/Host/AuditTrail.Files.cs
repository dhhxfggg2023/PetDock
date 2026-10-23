// AuditTrail 的文件那一半：开文件、落盘、收尾（去掉 .part）、目录总量上限。
//
// 为什么要拆出来：D-03c 的单文件 400 行上限。主文件 AuditTrail.cs 讲的是
// 「谁在记、记什么、记成什么行」，这里讲的是「文件生命周期」——
// 两件事的失败模式与不变量不同，混在一个文件里时谁也说不清
// 哪一段该在哪把锁底下跑。
//
// 不变量（与主文件同一套）：
//   只有后台写入线程碰文件；其它线程只往队列里放行。
//   本文件的所有写盘方法都在 _commitGate 之内被调用（持锁者就是那一瞬间的唯一写者）。
//   队列锁（_gate）不覆盖「写文件」那段——这正是 ResidentLog 踩过的坑：
//   两处并发 commit 会让行写坏、字节数算错，于是上限永不触发。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using PetDock.Core.Compositor;

namespace PetDock.Hosting;

internal sealed partial class AuditTrail
{
    /// <summary>commit 的串行锁（后台线程与调用线程都要落盘，见 Flush）。</summary>
    private readonly object _commitGate = new();

    // —— 只被后台写入线程访问 ——
    private StreamWriter? _writer;
    private long _fileBytes;

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
        Finish();
    }

    /// <summary>把缓冲里的行写进文件。写失败只计数，不抛。</summary>
    private void Flush()
    {
        // 取批次与写文件必须在**同一把** commit 锁里。
        //
        // 为什么（2026-10-21 实测）：分开放的时候，「缓冲空」不再等价于「行已落盘」——
        // 后台线程（Loop 每 200ms 调一次 Flush）取走批次、正卡在写文件之前的那一小段窗口里，
        // 主线程的 FlushNow() 看到 _buffer.Count == 0 就直接返回，
        // 而文件是在 EnsureWriter() 里才建的 ⇒ 目录里一个文件都没有
        // （判据 ui-audit-trail-file-written 偶发 files=0 就是这么来的）。
        // 合成一把锁之后，「缓冲空」重新等价于「该写的都写完了」，
        // FlushNow() 的语义（返回时行已在文件里、文件确实存在）才立得住。
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
                EnsureWriter();

                // 取一次局部引用：字段是可空的，编译器不会因为 EnsureWriter 保证了它非空就消掉警告。
                StreamWriter writer = _writer!;
                foreach (string line in batch)
                {
                    writer.Write(line);
                    _fileBytes += line.Length;
                }

                writer.Flush();

                // 目录总量上限：每次落盘后看一眼，超了就从最旧的文件开始删。
                // 流水**不承诺**永久保存（RFC-20 §3.5）—— 保留期策略是后续切片。
                EnforceDirCap();
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

    /// <summary>打开（或复用）当前活动文件。</summary>
    private void EnsureWriter()
    {
        if (_writer is not null)
        {
            return;
        }

        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, _fileName + ".part");
        _writer = new StreamWriter(
            new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        _fileBytes = 0;
    }

    /// <summary>关掉活动文件并去掉 .part 后缀（它从这一刻起才是硬证据）。</summary>
    private void Finish()
    {
        lock (_commitGate)
        {
            if (_writer is null)
            {
                return;
            }

            try
            {
                _writer.Flush();
                _writer.Dispose();
            }
            catch (Exception)
            {
                // 关不掉就留着 .part —— 残留的 .part 会在导出时被标注为未完成。
            }

            _writer = null;

            try
            {
                string part = Path.Combine(_directory, _fileName + ".part");
                string done = Path.Combine(_directory, _fileName);
                if (File.Exists(part))
                {
                    if (File.Exists(done))
                    {
                        File.Delete(done);
                    }

                    File.Move(part, done);
                }
            }
            catch (Exception)
            {
                // 同上。
            }
        }
    }

    /// <summary>目录总量超上限时，从最旧的文件开始删。</summary>
    private void EnforceDirCap()
    {
        try
        {
            if (!Directory.Exists(_directory))
            {
                return;
            }

            var done = new List<(string Path, long Size, DateTime Written)>();
            long total = 0;
            foreach (string file in Directory.EnumerateFiles(_directory, "audit-*.jsonl*"))
            {
                var info = new FileInfo(file);
                total += info.Length;
                done.Add((file, info.Length, info.LastWriteTimeUtc));
            }

            if (total <= MaxDirBytes)
            {
                return;
            }

            done.Sort((a, b) => a.Written.CompareTo(b.Written));

            foreach ((string path, long size, _) in done)
            {
                if (total <= MaxDirBytes)
                {
                    break;
                }

                try
                {
                    File.Delete(path);
                    total -= size;
                }
                catch (Exception)
                {
                    // 删不掉就留着（可能正被别的进程打开）。
                }
            }
        }
        catch (Exception)
        {
            // 目录扫描失败不该影响流水本身。
        }
    }
}
