// RFC-24（docs/17）的存储层：每实例一个 state/<key>.json + 提交锁 + 原子替换。
// 分层：本文件只管「字节怎么安全落盘」；缓冲区、迁移、上限判定在 PersistentPetState。
// 调用纪律（docs/11:168）：不在 tick 内做持久化写 —— 调用点是装载 / 挂起 / 释放这类生命周期边界。
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json.Nodes;
using System.Threading;
using PetDock.Core.Compositor;
using PetDock.Core.PetRuntime;

namespace PetDock.Hosting;

/// <summary>
/// 一次提交被拒绝的码（日志、诊断与异常共用一套词；不要另起近义词）。
/// </summary>
internal static class StateRefusal
{
    public const string Locked = "locked";
    public const string SchemaNewer = "schema-newer";
    public const string ForeignInstance = "foreign-instance";
    public const string MigrationFailed = "migration-failed";
    public const string OverLimit = "over-limit";
    public const string Invalid = "invalid-state";
    public const string Io = "io";
}

/// <summary>提交失败（带机器可判的原因码，见 <see cref="StateRefusal"/>）。</summary>
internal sealed class StateCommitException : System.Exception
{
    public StateCommitException(string reason, string message, System.Exception? inner = null)
        : base(message, inner)
    {
        Reason = reason;
    }

    public string Reason { get; }
}

/// <summary>磁盘上一份可解析的状态文件。</summary>
internal sealed record StateFile(
    int SchemaVersion,
    string AgentId,
    string InstanceId,
    IReadOnlyDictionary<string, JsonNode?> Values);

/// <summary>
/// 单个实例状态文件的读写门（RFC-24 §6.1/§6.2）。
/// 两道锁都在同一处：进程内 <see cref="SemaphoreSlim"/>（同进程两个实例写同一文件时先撞它），
/// 进程外 <c>.lock</c> 的独占句柄（另一个进程撞它）。取不到就拒绝，不排队 ——
/// 生命周期边界上的等待会退化成「关窗卡住」，而丢一次提交只丢一个 buffer 周期的状态。
/// </summary>
internal sealed class StateStore
{
    public const string FileSuffix = ".json";

    /// <summary>docs/11:212 的单键上限；主机层与 ABI 声明的是同一个数。</summary>
    public const int PerKeyLimitBytes = 8 * 1024;

    /// <summary>读取护栏：超过它的文件按「坏文件」处理（状态文件正常是几百字节）。</summary>
    private const int MaxReadBytes = 1 << 20;

    private const string AgentId = "host";

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly SemaphoreSlim _gate;

    public StateStore(string directory, string key)
    {
        string fullDir = Path.GetFullPath(directory);
        FilePath = BuildPath(fullDir, key);
        LockPath = FilePath + ".lock";
        _gate = Gates.GetOrAdd(FilePath, _ => new SemaphoreSlim(1, 1));
    }

    /// <summary>状态文件的完整路径。</summary>
    public string FilePath { get; }

    /// <summary>进程间互斥用的锁文件路径（与状态文件同目录，后缀 .lock）。</summary>
    public string LockPath { get; }

    public bool Exists => File.Exists(FilePath);

    /// <summary>
    /// 文件键（RFC-24 §6.2）：实例 id 做文件名安全化；
    /// 素材根参与哈希 —— 「导入的宠物」每换一只实例 id 都不变（都是 import.&lt;pluginId&gt;），
    /// 只用 id 会让两只不同的宠物共用一个文件（互相踩状态）。
    /// 素材根为空（默认宠物）时不加后缀，文件名保持可读。
    /// </summary>
    public static string KeyFor(string instanceId, IReadOnlyList<string>? assetRoots)
    {
        var safe = new StringBuilder(instanceId.Length);
        foreach (char c in instanceId)
        {
            safe.Append(char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? char.ToLowerInvariant(c) : '_');
        }

        string key = safe.Length == 0 ? "instance" : safe.ToString();
        string root = assetRoots is { Count: > 0 } ? Path.GetFullPath(assetRoots[0]) : string.Empty;
        return root.Length == 0 ? key : key + "-" + ShortHash(root);
    }

    /// <summary>
    /// 取同一道锁的独占权（自检与「两个栈写同一文件」的判据用；正常路径由 <see cref="Save"/> 内部取）。
    /// 取不到抛 <see cref="StateRefusal.Locked"/>。
    /// </summary>
    public System.IDisposable Acquire()
    {
        if (!_gate.Wait(0))
        {
            throw new StateCommitException(StateRefusal.Locked, $"state lock busy (in-process): {FilePath}");
        }

        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath)!);
            FileStream stream = new(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return new HeldLock(_gate, stream);
        }
        catch (IOException error)
        {
            _gate.Release();
            throw new StateCommitException(StateRefusal.Locked, $"state lock busy (file): {LockPath}", error);
        }
        catch (System.UnauthorizedAccessException error)
        {
            _gate.Release();
            throw new StateCommitException(StateRefusal.Io, $"state lock denied: {LockPath}", error);
        }
    }

    /// <summary>
    /// 读一份状态文件。文件不存在 → null（首次启动是正常路径，不是错误）。
    /// 坏文件（解析不了 / 形状不对 / 超读护栏）→ null 并记一行日志：不阻止启动，也不假装读到。
    /// </summary>
    public StateFile? Load()
    {
        if (!File.Exists(FilePath))
        {
            return null;
        }

        try
        {
            long length = new FileInfo(FilePath).Length;
            if (length > MaxReadBytes)
            {
                KernelLog.Write($"state-load-rejected file={FilePath} reason=too-large bytes={length}");
                return null;
            }

            JsonNode? node = JsonNode.Parse(File.ReadAllText(FilePath, Encoding.UTF8));
            if (node is not JsonObject root
                || root["schemaVersion"] is not JsonValue versionNode
                || !versionNode.TryGetValue(out int version)
                || root["values"] is not JsonObject values)
            {
                KernelLog.Write($"state-load-rejected file={FilePath} reason=shape");
                return null;
            }

            string instanceId = root["instanceId"] is JsonValue idNode && idNode.TryGetValue(out string? id)
                ? id ?? string.Empty
                : string.Empty;
            string agentId = root["agent_id"] is JsonValue agentNode && agentNode.TryGetValue(out string? agent)
                ? agent ?? string.Empty
                : string.Empty;

            var parsed = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, JsonNode?> pair in values)
            {
                parsed[pair.Key] = pair.Value?.DeepClone();
            }

            return new StateFile(version, agentId, instanceId, parsed);
        }
        catch (System.Exception error) when (error is IOException or System.Text.Json.JsonException or System.UnauthorizedAccessException)
        {
            KernelLog.Write($"state-load-failed file={FilePath} {error.GetType().Name}: {error.Message}");
            return null;
        }
    }

    /// <summary>
    /// 写一份状态文件：取锁 → 写临时文件 → 原子替换。
    /// 半截文件不可见：读方要么看到旧文件，要么看到新文件（RFC-24 §3 第 5 条）。
    /// 单键超限在这里拒绝（<see cref="StateLimitException"/>），不打折、不裁剪。
    /// </summary>
    public void Save(int schemaVersion, string instanceId, IReadOnlyDictionary<string, JsonNode?> values)
    {
        byte[] payload = Serialize(schemaVersion, instanceId, values);
        using System.IDisposable held = Acquire();
        string temp = FilePath + ".tmp";
        try
        {
            File.WriteAllBytes(temp, payload);
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (System.Exception error) when (error is IOException or System.UnauthorizedAccessException)
        {
            TryDelete(temp);
            throw new StateCommitException(StateRefusal.Io, $"state write failed: {FilePath} {error.GetType().Name}: {error.Message}", error);
        }
    }

    /// <summary>序列化 + 单键上限校验（校验在写之前：超限不产生任何字节）。</summary>
    private byte[] Serialize(int schemaVersion, string instanceId, IReadOnlyDictionary<string, JsonNode?> values)
    {
        var keys = new List<string>(values.Keys);
        keys.Sort(StringComparer.Ordinal);

        JsonObject payloadValues = new();
        foreach (string key in keys)
        {
            JsonNode? value = values[key];
            int bytes = Encoding.UTF8.GetByteCount(value?.ToJsonString() ?? "null");
            if (bytes > PerKeyLimitBytes)
            {
                throw new StateLimitException(key, bytes, PerKeyLimitBytes);
            }

            // 深拷贝：调用方之后 mutate 缓冲区，不能改到已经写下的字节。
            payloadValues[key] = value?.DeepClone();
        }

        JsonObject root = new()
        {
            ["schemaVersion"] = schemaVersion,
            ["agent_id"] = AgentId,
            ["timestamp"] = System.DateTime.UtcNow.ToString("o"),
            ["instanceId"] = instanceId,
            ["values"] = payloadValues,
        };

        var options = new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = false,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        return Encoding.UTF8.GetBytes(root.ToJsonString(options));
    }

    private static string BuildPath(string directory, string key) => System.IO.Path.Combine(directory, key + FileSuffix);

    private static string ShortHash(string text)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(text.ToLowerInvariant()));
        return System.Convert.ToHexString(hash[..4]).ToLowerInvariant();
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (System.Exception error) when (error is IOException or System.UnauthorizedAccessException)
        {
            KernelLog.Write($"state-temp-left file={path} {error.GetType().Name}: {error.Message}");
        }
    }

    /// <summary>锁的持有期（释放顺序：先关文件句柄，再放进程内信号量）。</summary>
    private sealed class HeldLock : System.IDisposable
    {
        private readonly SemaphoreSlim _gate;
        private FileStream? _stream;

        public HeldLock(SemaphoreSlim gate, FileStream stream)
        {
            _gate = gate;
            _stream = stream;
        }

        public void Dispose()
        {
            FileStream? stream = _stream;
            _stream = null;
            try
            {
                stream?.Dispose();
            }
            finally
            {
                _gate.Release();
            }
        }
    }
}
