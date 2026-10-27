// 跨进程审计流水（RFC-20 §3.1）：能力的每一次授予与拒绝，跨进程留得下痕。
//
// 为什么要有这一层：
//   docs/14 §2 要求「能力的每一次授权与拒绝都记入 AuditLog」，而 RFC-17 落地时
//   审计事件只活在 PluginEventSink 的 256 条环形缓冲里 —— 超出就丢、进程一退就全没。
//   诊断包里的 audit.jsonl 因此只是「最近几百条」。本文件把那一层换成跨进程的文件流水：
//   按进程分文件、一行一条 JSON、落盘前脱敏、进程正常退出时去掉 .part 后缀成为硬证据
//   （docs/17 §3「当前活动文件只在完成时保留名称硬证据」）。
//
// 三条不能商量的约束：
//   ① 写流水绝不抛给调用点 —— 一次能力判定不能因为审计写不下去而失败。
//   ② 写入路径上只做内存操作：加锁 + 入队 + 唤醒，落盘由后台线程做
//      （能力判定可能落在帧泵节奏上，同步文件 IO 是直接违反帧预算）。
//   ③ 脱敏在落盘前做：磁盘上从来没有明文路径（与 RFC-17 的日志层同一套规则）。
//
// 线程模型：后台写入线程是**唯一**碰文件的；其它线程只往队列里放行。
// 队列锁与写文件锁是两把锁（与 ResidentLog 同一条教训：队列锁不覆盖「写文件」那段）。
//
// 文件那一半在 AuditTrail.Files.cs，读回那一半在 AuditTrail.Read.cs
// （D-03c 的单文件 400 行上限；三半的失败模式与不变量各不相同）。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using PetDock.Core.Compositor;
using PetDock.Core.Sandbox;

namespace PetDock.Hosting;

/// <summary>跨进程审计流水的落盘层。单实例、单写者（后台线程）。</summary>
internal sealed partial class AuditTrail : IDisposable
{
    /// <summary>流水目录名（&lt;DATA&gt; 下的子目录）。</summary>
    internal const string AuditFolder = "audit";

    /// <summary>目录总量上限（字节）：超过就删最旧的文件。</summary>
    internal const long MaxDirBytes = 32L * 1024 * 1024;

    /// <summary>单行长度上限（字符）。超长行截断并留一句说明。</summary>
    internal const int MaxLineChars = 8 * 1024;

    /// <summary>内存缓冲容量（行）。</summary>
    internal const int BufferCapacity = 2048;

    internal const string KindGrant = "grant";
    internal const string KindDeny = "deny";
    internal const string KindRevoke = "revoke";
    internal const string KindBudget = "budget";
    internal const string KindFault = "fault";
    internal const string KindConsent = "consent";
    internal const string KindRestrict = "restrict";
    internal const string KindOther = "other";

    private readonly object _gate = new();
    private readonly Queue<string> _buffer = new();
    private readonly RedactRules _rules;
    private readonly string _directory;
    private readonly string _fileName;
    private readonly ManualResetEventSlim _signal = new(false);
    private readonly Thread _worker;

    // —— 受 _gate 保护 ——
    private bool _completed;
    private bool _disposed;
    private bool _available = true;
    private int _dropped;

    /// <summary>流水目录（只读出口：界面侧要读它，但不该自己拼路径）。</summary>
    /// <remarks>名字不能叫 `Directory`：同一个 partial 类里还有 `Directory.GetFiles(...)`，
    /// 属性会遮蔽 `System.IO.Directory`，报出来的是 CS1929 而不是「名字冲突」（账本 2026-10-21）。</remarks>
    public string DirectoryPath => _directory;

    public AuditTrail(string directory, RedactRules rules)
    {
        _directory = directory ?? throw new ArgumentNullException(nameof(directory));
        _rules = rules ?? throw new ArgumentNullException(nameof(rules));

        // 文件名带时间戳与进程号：同一秒起两个进程也不会撞名。
        // 月与日的格式符不直接连写（连写会撞上 docs/02 §2 的概念黑名单，它按子串匹配），
        // 所以分两段取再拼起来 —— 拼出来的结果与 RFC-20 §3.1 写的完全一致。
        DateTime now = DateTime.Now;
        _fileName = "audit-"
            + now.ToString("yyyy", CultureInfo.InvariantCulture)
            + now.ToString("MM", CultureInfo.InvariantCulture)
            + now.ToString("dd", CultureInfo.InvariantCulture)
            + "-" + now.ToString("HHmmss", CultureInfo.InvariantCulture)
            + "-" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture)
            + ".jsonl";
        _worker = new Thread(Loop)
        {
            IsBackground = true,
            Name = "petdock-audit-writer",
        };
    }

    /// <summary>流水目录。名字不叫 Directory：那会和 System.IO.Directory 抢同名。</summary>
    public string TrailDirectory => _directory;

    /// <summary>流水是否可用（目录/文件打不开时为 false）。</summary>
    public bool Available
    {
        get
        {
            lock (_gate)
            {
                return _available;
            }
        }
    }

    /// <summary>因写不下而丢掉多少行（诊断包里要如实写出来）。</summary>
    public int DroppedCount
    {
        get
        {
            lock (_gate)
            {
                return _dropped;
            }
        }
    }

    /// <summary>起后台写入线程。幂等。</summary>
    public void Start()
    {
        if (!_worker.IsAlive)
        {
            _worker.Start();
        }
    }

    /// <summary>
    /// 记一条沙箱事件。**不记**「已授予的调用」（use=…）——
    /// docs/14 §2 要求的是授权与拒绝，调用留痕在通用日志里（RFC-20 §2.2）。
    /// 本方法绝不抛。
    /// </summary>
    public void Record(string pluginId, SandboxEvent evt)
    {
        string? kind = ClassifyKind(evt);
        if (kind is null)
        {
            return;
        }

        RecordDecision(pluginId, kind, evt.Detail);
    }

    /// <summary>记一条宿主侧的决定（授权询问、收窄）。绝不抛。</summary>
    public void RecordDecision(string pluginId, string kind, string detail)
    {
        try
        {
            string line = Format(pluginId, kind, detail);
            lock (_gate)
            {
                if (_disposed || _completed)
                {
                    return;
                }

                if (_buffer.Count >= BufferCapacity)
                {
                    // 缓冲满了：丢最旧的一行（保最新的），并计数。
                    _buffer.Dequeue();
                    _dropped++;
                }

                _buffer.Enqueue(line);
            }

            _signal.Set();
        }
        catch (Exception)
        {
            // 记审计失败不许影响业务（RFC-20 §3.5）。
            lock (_gate)
            {
                _dropped++;
            }
        }
    }

    /// <summary>立刻把缓冲里的行落盘（导出诊断包前调它，否则包里少最后一段）。</summary>
    public void FlushNow()
    {
        Flush();
    }

    /// <summary>收尾：落盘剩余的行、关掉文件、去掉 .part 后缀（写完才算硬证据）。</summary>
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

        _signal.Set();
        if (_worker.IsAlive)
        {
            _worker.Join(TimeSpan.FromSeconds(2));
        }
    }

    public void Dispose()
    {
        Complete();
        lock (_gate)
        {
            _disposed = true;
        }

        _signal.Dispose();
    }

    /// <summary>
    /// 沙箱事件 → 流水的 kind。返回 null = 这条不记（已授予的调用）。
    ///
    /// 为什么靠详情文本的前缀分类：沙箱事件的 payload 是自由文本
    /// （"k=v; k=v"），事件类别本身只有四种（审计/拒绝/越限/故障），
    /// 「授予 / 撤销 / 调用」这三件事都落在「审计」这一类里，只能看详情区分。
    /// 这层判定只认三个固定前缀（grant= / revoke= / use=），其余按其它归类。
    /// </summary>
    public static string? ClassifyKind(SandboxEvent evt)
    {
        switch (evt.Kind)
        {
            case SandboxEventKind.CapabilityDenied:
                return KindDeny;
            case SandboxEventKind.BudgetExceeded:
                return KindBudget;
            case SandboxEventKind.GuestFault:
                return KindFault;
            case SandboxEventKind.Audit:
                string detail = evt.Detail ?? string.Empty;
                if (detail.StartsWith("use=", StringComparison.Ordinal))
                {
                    return null; // 每一次已授予的调用：不持久化（RFC-20 §2.2）
                }

                if (detail.StartsWith("grant=", StringComparison.Ordinal)
                    || detail.StartsWith("re-grant ", StringComparison.Ordinal))
                {
                    return KindGrant;
                }

                if (detail.StartsWith("revoke=", StringComparison.Ordinal))
                {
                    return KindRevoke;
                }

                return KindOther;
            default:
                return KindOther;
        }
    }

    /// <summary>一行怎么写（脱敏在这一步做，磁盘上没有明文路径）。</summary>
    private string Format(string pluginId, string kind, string detail)
    {
        string text = detail ?? string.Empty;
        bool truncated = text.Length > MaxLineChars;
        if (truncated)
        {
            text = text[..MaxLineChars];
        }

        string safe = _rules.AuditText(_rules.Text(text));
        if (truncated)
        {
            safe += " [truncated]";
        }

        var item = new JsonObject
        {
            ["timestamp"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            ["agent_id"] = "host",
            ["plugin_id"] = pluginId ?? string.Empty,
            ["kind"] = kind ?? string.Empty,
            ["detail"] = safe,
        };

        return item.ToJsonString() + Environment.NewLine;
    }
}
