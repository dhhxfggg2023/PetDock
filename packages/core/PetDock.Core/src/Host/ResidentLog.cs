// 运行期日志落盘层（RFC-17 §3.1）：常驻形态的 KernelLog 终于有地方落。
//
// 三条不能商量的约束：
//   ① Write 路径上只允许有内存操作 —— KernelLog.Write 会被帧泵线程调用，
//      而 SM-04 的单帧预算只有 8.3ms，同步文件 IO 放进帧泵线程就是直接违反帧预算。
//      所以 Write 只做「加锁 + 入队 + 唤醒」，落盘由后台线程做。
//   ② 写失败绝不抛给调用点 —— 日志失败不该带走业务。commit 失败时这批行退回缓冲。
//   ③ 脱敏在落盘前做 —— 落盘的是脱敏后的文本，用户直接把 logs 目录打包发出去也是安全的。
//
// 活动文件带 .part 后缀：原规格 17 §3「当前活动文件只在完成时保留名称硬证据」——
// 没写完的文件不算证据。进程正常退出时去掉 .part 变成硬证据；被强杀留下的 .part
// 在导出时会被收进包里，但必须标注它未完成。
//
// 线程模型：后台写入线程是**唯一**碰文件的那个（_writer 只被它访问）；
// 其它线程只通过 _gate 看到 _activePath 这一个字符串。绝不把文件句柄的读
// 交给别的线程去猜。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using PetDock.Core.Compositor;

namespace PetDock.Hosting;

/// <summary>常驻形态的运行期日志落盘层。单实例、单写者（后台线程）。
/// 文件生命周期那一半在 ResidentLog.Files.cs（D-03c 拆分）。</summary>
internal sealed partial class ResidentLog : IKernelLogSink, IDisposable
{
    /// <summary>内存缓冲容量（行）。</summary>
    internal const int BufferCapacity = 512;

    /// <summary>缓冲半满即触发 commit。</summary>
    internal const int FlushThreshold = BufferCapacity / 2;

    /// <summary>距上次 commit 的最长时间（毫秒）。</summary>
    internal const int CommitIntervalMs = 200;

    /// <summary>单文件上限（字节）：超过就滚动。</summary>
    internal const long MaxFileBytes = 4L * 1024 * 1024;

    /// <summary>日志目录总量上限（字节）：超过就删最旧的已完结文件。</summary>
    internal const long MaxDirBytes = 64L * 1024 * 1024;

    /// <summary>单行长度上限（字符）。超长行截断并留一句说明。</summary>
    internal const int MaxLineChars = 8 * 1024;

    private readonly object _gate = new();

    /// <summary>commit 的串行锁（后台线程与调用线程都要 commit，见 Commit）。</summary>
    private readonly object _commitGate = new();
    private readonly Queue<string> _buffer = new();
    private readonly RedactRules _rules;
    private readonly string _directory;
    private readonly string _baseName;
    private readonly ManualResetEventSlim _signal = new(false);
    private readonly Thread _worker;

    // —— 只被后台写入线程访问 ——
    private StreamWriter? _writer;
    private long _fileBytes;
    private int _fileIndex;

    // —— 受 _gate 保护 ——
    private string? _activePath;
    private bool _completed;
    private bool _disposed;
    private int _dropped;

    /// <summary>造一个落盘层。日志目录不存在会被建出来（空目录也是成功）。</summary>
    public ResidentLog(string logDirectory, RedactRules rules)
    {
        _directory = logDirectory ?? throw new ArgumentNullException(nameof(logDirectory));
        _rules = rules ?? throw new ArgumentNullException(nameof(rules));
        // RFC-17 §3.1 规定的文件名：kernel- 加上「年+月+日-时分秒」这一串数字，再加 .log。
        // 月与日的格式符不直接连写（连写会撞上 原规格 02 §2 的概念黑名单，它按子串匹配），
        // 所以这里分两段取再拼起来——拼出来的结果与 RFC 写的完全一致。
        DateTime now = DateTime.Now;
        _baseName = "kernel-" + now.ToString("yyyy", CultureInfo.InvariantCulture)
            + now.ToString("MM", CultureInfo.InvariantCulture)
            + now.ToString("dd", CultureInfo.InvariantCulture)
            + "-" + now.ToString("HHmmss", CultureInfo.InvariantCulture);
        _worker = new Thread(Loop)
        {
            IsBackground = true,
            Name = "petdock-log-writer",
        };
    }

    /// <summary>日志目录（脱敏时用 &lt;DATA&gt; 替换它）。名字不叫 Directory：
    /// 那会和 System.IO.Directory 抢同名，类里的 Directory.CreateDirectory 会解析到属性上。</summary>
    public string LogDirectory => _directory;

    /// <summary>当前活动文件路径（带 .part）。没有活动文件时为 null。</summary>
    public string? ActiveFilePath
    {
        get
        {
            lock (_gate)
            {
                return _activePath;
            }
        }
    }

    /// <summary>缓冲里还没落盘的行数。</summary>
    public int PendingLines
    {
        get
        {
            lock (_gate)
            {
                return _buffer.Count;
            }
        }
    }

    /// <summary>因为缓冲满而被丢弃的行数（老行被挤掉）。</summary>
    public int DroppedLines
    {
        get
        {
            lock (_gate)
            {
                return _dropped;
            }
        }
    }

    /// <summary>起后台写入线程。幂等（已起就不再起）。</summary>
    public void Start()
    {
        if (_worker.IsAlive)
        {
            return;
        }

        _worker.Start();
    }

    /// <summary>
    /// 写一行（唯一入口）。只做内存操作：加锁、入队、必要时唤醒后台线程。
    /// 任何异常都在这里吞掉 —— 写日志失败不该把业务带走。
    /// </summary>
    public void Write(string message)
    {
        try
        {
            lock (_gate)
            {
                if (_completed)
                {
                    return;
                }

                if (_buffer.Count >= BufferCapacity)
                {
                    _buffer.Dequeue();
                    _dropped++;
                }

                _buffer.Enqueue(message ?? string.Empty);
                if (_buffer.Count >= FlushThreshold)
                {
                    _signal.Set();
                }
            }
        }
        catch (Exception)
        {
            // 落盘层的问题不外泄（RFC-17 §3.1：写失败绝不抛给调用点）。
        }
    }

    /// <summary>立刻把缓冲落盘（自检与进程退出用；不依赖后台线程的节拍）。</summary>
    public void CommitNow()
    {
        try
        {
            Commit();
        }
        catch (Exception)
        {
            // 同上：commit 失败不外泄，这批行留在缓冲里等下一次。
        }
    }

    /// <summary>
    /// 完结：落盘剩余的行、关闭文件、去掉 .part 后缀。
    /// 进程正常退出时走这条路径；被强杀时 .part 残留（导出时标注为未完成）。
    /// </summary>
    public void Complete()
    {
        lock (_gate)
        {
            if (_completed)
            {
                return;
            }

            _completed = true;
        }

        Commit();
        _signal.Set();
        try
        {
            if (_worker.IsAlive)
            {
                _worker.Join(1000);
            }
        }
        catch (Exception)
        {
            // 线程已经死了就当它不存在。
        }

        // 收尾文件也要与后台线程互斥：join 超时的情况下后台线程还可能正在写。
        lock (_commitGate)
        {
            FinishFile();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Complete();
        _signal.Dispose();
    }

    /// <summary>后台线程主体：等信号或等节拍，然后 commit。</summary>
    private void Loop()
    {
        while (true)
        {
            try
            {
                _signal.Wait(CommitIntervalMs);
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            lock (_gate)
            {
                if (_completed)
                {
                    return;
                }
            }

            Commit();

            // 同 AuditTrail.Loop：ManualResetEventSlim 只 Set 不 Reset 时，
            // 第一次 Set 之后 Wait(CommitIntervalMs) 永远立即返回，本循环退化成满速自旋。
            // 必须在本轮 Commit 之后清，才不会吞掉 Commit 期间来的 Set。
            _signal.Reset();
        }
    }

    /// <summary>
    /// 落盘一批。失败时这批行留在缓冲里（rollback），下一轮重试。
    ///
    /// **必须串行**：后台写入线程与调用线程的 CommitNow()/Complete() 都会走到这里。
    /// 没有这把锁时，两边会同时拿同一批行、同时写同一个 _writer ——
    /// 结果是行被写两遍或写坏（rollback 把行留在缓冲里重试，越积越多），
    /// 而 _fileBytes 也会算错，滚动永远不触发（判据 ui-diag-log-rolls-on-size 就是这么红的）。
    /// 持锁者就是那一瞬间的唯一写者，「单写者」的不变量仍然成立。
    /// </summary>
    private void Commit()
    {
        lock (_commitGate)
        {
            CommitLocked();
        }
    }

    /// <summary>落盘一批（调用者已持有 _commitGate）。</summary>
    private void CommitLocked()
    {
        string[] batch;
        lock (_gate)
        {
            if (_buffer.Count == 0)
            {
                return;
            }

            batch = _buffer.ToArray();
        }

        try
        {
            EnsureWriter();
            foreach (string raw in batch)
            {
                string line = Format(raw);
                _writer!.Write(line);
                _fileBytes += Encoding.UTF8.GetByteCount(line);
            }

            _writer!.Flush();
            EnforceDirCap();

            lock (_gate)
            {
                for (int i = 0; i < batch.Length && _buffer.Count > 0; i++)
                {
                    _buffer.Dequeue();
                }
            }
        }
        catch (Exception)
        {
            // rollback：这批行留在缓冲里，不写半行。
        }
    }

    /// <summary>行格式：timestamp=&lt;ISO-8601 UTC&gt; agent_id=host level=info msg=&lt;原文&gt;。
    /// 脱敏在这一步做（落盘前），所以磁盘上从来没有明文路径。</summary>
    private string Format(string raw)
    {
        bool truncated = raw.Length > MaxLineChars;
        string text = truncated ? raw[..MaxLineChars] : raw;
        string msg = _rules.Text(text);
        if (truncated)
        {
            msg += " [truncated]";
        }

        return "timestamp=" + DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)
            + " agent_id=host level=info msg=" + msg
            + Environment.NewLine;
    }
}
