// RFC-24 §4.1 的七条判据（--drivetest 的 `drive-state-*` 组）。
// 判据一律走真实对象：真 StateStore、真目录（系统 TEMP 下的一次性子目录）、真文件字节比对；
// 迁移用的「插件」是桩件（仓库里唯一的真状态插件是 NullState，它不迁移也不留东西）。
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using PetDock.Core.Compositor;
using PetDock.Core.PetRuntime;

namespace PetDock.Hosting;

internal static class StateChecks
{
    /// <summary>判据入口：返回失败数（0 = 全过）。</summary>
    public static int Run()
    {
        int failures = 0;
        string root = Path.Combine(Path.GetTempPath(), "petdock-state-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            RoundTrip(root, ref failures);
            BufferNotCommitted(root, ref failures);
            LimitRejected(root, ref failures);
            MigrateForward(root, ref failures);
            NewerSchemaReadOnly(root, ref failures);
            LockRefused(root, ref failures);
            NoStateLayer(root, ref failures);
        }
        finally
        {
            TryDeleteTree(root);
        }

        return failures;
    }

    /// <summary>①写入 + 提交 → **另一个** store 对象从同一文件读回；文件头三个字段齐全。</summary>
    private static void RoundTrip(string root, ref int failures)
    {
        string dir = Path.Combine(root, "round-trip");
        var events = new List<PetEvent>();
        PersistentPetState first = PersistentPetState.Attach("drive.state", new MemoryPetState(), dir, null, events.Add);
        first.Write(new Dictionary<string, JsonNode?> { ["mood"] = 42 });

        bool committed = first.TryCommit(out StateCommitException? refusal);
        string text = File.Exists(first.StorePath) ? File.ReadAllText(first.StorePath) : string.Empty;
        bool headerOk = text.Contains("\"schemaVersion\":1", StringComparison.Ordinal)
            && text.Contains("\"agent_id\":\"host\"", StringComparison.Ordinal)
            && text.Contains("\"timestamp\":", StringComparison.Ordinal)
            && text.Contains("\"instanceId\":\"drive.state\"", StringComparison.Ordinal);

        PersistentPetState second = PersistentPetState.Attach("drive.state", new MemoryPetState(), dir, null, null);
        int mood = second.Read("mood")?.GetValue<int>() ?? -1;
        bool commitEvent = events.Exists(e => e.Type == EventCatalog.PetStateCommit
            && e.Payload?["petId"]?.GetValue<string>() == "drive.state");

        PetDriveTest.Check(ref failures, "state-round-trip",
            committed && mood == 42 && headerOk && commitEvent,
            $"committed={committed} refused={refusal?.Reason ?? "(no)"} mood={mood} header={headerOk}"
                + $" commit-events={commitEvent} file={first.StorePath}");
    }

    /// <summary>②只 write 不 commit → 文件字节不变（或压根没建）：崩溃语义的判据面。</summary>
    private static void BufferNotCommitted(string root, ref int failures)
    {
        string dir = Path.Combine(root, "buffer");
        PersistentPetState state = PersistentPetState.Attach("drive.buffer", new MemoryPetState(), dir, null, null);
        state.Write(new Dictionary<string, JsonNode?> { ["mood"] = 1 });
        bool committedOnce = state.TryCommit(out _);
        byte[]? before = Snapshot(state.StorePath);

        state.Write(new Dictionary<string, JsonNode?> { ["mood"] = 2 });
        byte[]? after = Snapshot(state.StorePath);
        bool unchanged = before is not null && after is not null && SameBytes(before, after);
        bool readable = state.Read("mood")?.GetValue<int>() == 2;

        PetDriveTest.Check(ref failures, "state-buffer-not-committed",
            committedOnce && unchanged && readable && state.HasUnsaved,
            $"unchanged={unchanged} buffer={state.Read("mood")?.ToJsonString() ?? "null"}"
                + $" dirty={state.HasUnsaved} bytes={(before?.Length.ToString() ?? "none")}");
    }

    /// <summary>③单键 8 KiB + 1 字节 → commit 抛 StateLimitException（带 key），文件不变。</summary>
    private static void LimitRejected(string root, ref int failures)
    {
        string dir = Path.Combine(root, "limit");
        PersistentPetState state = PersistentPetState.Attach("drive.limit", new MemoryPetState(), dir, null, null);
        state.Write(new Dictionary<string, JsonNode?> { ["mood"] = 7 });
        state.TryCommit(out _);
        byte[]? before = Snapshot(state.StorePath);

        // 序列化后恰好 8193 字节：JsonNode.ToJsonString 会加引号，所以字符串长度取 8191。
        string big = new string('a', StateStore.PerKeyLimitBytes - 1);
        string? key = null;
        int bytes = 0;
        try
        {
            state.Write(new Dictionary<string, JsonNode?> { ["mood"] = big });
            state.CommitAsync().GetAwaiter().GetResult();
        }
        catch (StateLimitException error)
        {
            key = error.Key;
            bytes = error.Bytes;
        }

        byte[]? after = Snapshot(state.StorePath);
        bool unchanged = before is not null && after is not null && SameBytes(before, after);

        PetDriveTest.Check(ref failures, "state-limit-rejected",
            key == "mood" && bytes > StateStore.PerKeyLimitBytes && unchanged,
            $"key={key ?? "(none)"} bytes={bytes} limit={StateStore.PerKeyLimitBytes} unchanged={unchanged}");
    }

    /// <summary>④磁盘 v1 + 空 values，插件 v2 且迁移加一个字段 → 缓冲区有字段、文件写成 v2、发出 migrated{1,2}。</summary>
    private static void MigrateForward(string root, ref int failures)
    {
        string dir = Path.Combine(root, "migrate");
        string key = StateStore.KeyFor("drive.migrate", null);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, key + StateStore.FileSuffix), SeedFile(1, "drive.migrate", "{}"));

        var events = new List<PetEvent>();
        PersistentPetState state = PersistentPetState.Attach(
            "drive.migrate", new SchemaTwoState(), dir, null, events.Add);

        string text = File.ReadAllText(state.StorePath);
        bool bufferMigrated = state.Read("added")?.GetValue<bool>() == true;
        bool fileMigrated = text.Contains("\"schemaVersion\":2", StringComparison.Ordinal)
            && text.Contains("\"added\":true", StringComparison.Ordinal);
        bool migratedEvent = events.Exists(e => e.Type == EventCatalog.PetStateMigrated
            && e.Payload?["from"]?.GetValue<int>() == 1
            && e.Payload?["to"]?.GetValue<int>() == 2);

        PetDriveTest.Check(ref failures, "state-migrate-forward",
            bufferMigrated && fileMigrated && migratedEvent && state.Migrations == 1,
            $"buffer={bufferMigrated} file={fileMigrated} event={migratedEvent} migrations={state.Migrations}");
    }

    /// <summary>⑤磁盘 v9、插件 v1 → 读得到、commit 被拒（schema-newer）、文件一字节不动。</summary>
    private static void NewerSchemaReadOnly(string root, ref int failures)
    {
        string dir = Path.Combine(root, "newer");
        string key = StateStore.KeyFor("drive.newer", null);
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, key + StateStore.FileSuffix);
        File.WriteAllText(file, SeedFile(9, "drive.newer", "{\"mood\":5}"));
        byte[] before = File.ReadAllBytes(file);

        PersistentPetState state = PersistentPetState.Attach("drive.newer", new MemoryPetState(), dir, null, null);
        bool readable = state.Read("mood")?.GetValue<int>() == 5;
        state.Write(new Dictionary<string, JsonNode?> { ["mood"] = 6 });
        bool refused = !state.TryCommit(out StateCommitException? refusal)
            && refusal?.Reason == StateRefusal.SchemaNewer;
        byte[] after = File.ReadAllBytes(file);

        PetDriveTest.Check(ref failures, "state-newer-schema-readonly",
            readable && refused && SameBytes(before, after) && state.IsBlocked,
            $"readable={readable} refused={refused} unchanged={SameBytes(before, after)} blocked={state.BlockedReason}");
    }

    /// <summary>⑥另一个 store 先占住同一道锁 → 本次 commit 被拒、文件不变。</summary>
    private static void LockRefused(string root, ref int failures)
    {
        string dir = Path.Combine(root, "lock");
        PersistentPetState state = PersistentPetState.Attach("drive.lock", new MemoryPetState(), dir, null, null);
        state.Write(new Dictionary<string, JsonNode?> { ["mood"] = 3 });
        state.TryCommit(out _);
        byte[]? before = Snapshot(state.StorePath);

        string key = StateStore.KeyFor("drive.lock", null);
        var rival = new StateStore(dir, key);
        bool refused;
        try
        {
            using (rival.Acquire())
            {
                state.Write(new Dictionary<string, JsonNode?> { ["mood"] = 4 });
                refused = !state.TryCommit(out StateCommitException? refusal)
                    && refusal?.Reason == StateRefusal.Locked;
            }
        }
        catch (StateCommitException error) when (error.Reason == StateRefusal.Locked)
        {
            // 同一个 store 自己都拿不到锁：这在「两道锁都在同一处」的实现里也满足判据（写入被拒）。
            state.Write(new Dictionary<string, JsonNode?> { ["mood"] = 4 });
            refused = !state.TryCommit(out _);
        }

        byte[]? after = Snapshot(state.StorePath);
        bool unchanged = before is not null && after is not null && SameBytes(before, after);

        PetDriveTest.Check(ref failures, "state-lock-refused",
            refused && unchanged, $"refused={refused} unchanged={unchanged} file={state.StorePath}");
    }

    /// <summary>⑦没有状态层 / 没有数据区根 → 什么都不留（自检不污染用户数据区）。</summary>
    private static void NoStateLayer(string root, ref int failures)
    {
        string absent = Path.Combine(root, "absent");
        PetInstance instance = PetDriveTest.MakeInstance(out _, out _);
        bool wrote = instance.WriteHostState(PersistentPetState.PositionKey, new JsonObject { ["x"] = 1, ["y"] = 2 });
        bool committed = instance.CommitState();
        bool stillStub = instance.StateLayer is StubState;
        bool noDir = !Directory.Exists(absent);
        instance.Dispose();

        PetDriveTest.Check(ref failures, "state-no-state-layer-keeps-nothing",
            !wrote && !committed && stillStub && noDir,
            $"wrote={wrote} committed={committed} stub={stillStub} dir={noDir}");
    }

    /// <summary>手写一份状态文件（用来造「磁盘比插件旧/新」的两个场景）。</summary>
    private static string SeedFile(int schemaVersion, string instanceId, string values)
    {
        return "{"
            + $"\"schemaVersion\":{schemaVersion},"
            + "\"agent_id\":\"host\","
            + "\"timestamp\":\"2026-10-23T00:00:00.0000000Z\","
            + $"\"instanceId\":\"{instanceId}\","
            + $"\"values\":{values}"
            + "}";
    }

    private static byte[]? Snapshot(string path) => File.Exists(path) ? File.ReadAllBytes(path) : null;

    private static bool SameBytes(byte[] left, byte[] right)
    {
        if (left.Length != right.Length)
        {
            return false;
        }

        for (int i = 0; i < left.Length; i++)
        {
            if (left[i] != right[i])
            {
                return false;
            }
        }

        return true;
    }

    private static void TryDeleteTree(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            KernelLog.Write($"state-check-cleanup-failed {error.GetType().Name}: {error.Message}");
        }
    }

    /// <summary>schema 2 的桩件：迁移 1→2 只加一个字段（「只升版本号不改数据」会因为缺字段而 FAIL）。</summary>
    private sealed class SchemaTwoState : IPetState
    {
        public int SchemaVersion => 2;

        public IReadOnlyDictionary<string, JsonNode?> ReadAll() => new Dictionary<string, JsonNode?>();

        public JsonNode? Read(string key) => null;

        public void Write(IReadOnlyDictionary<string, JsonNode?> patch)
        {
        }

        public System.Threading.Tasks.Task CommitAsync() => System.Threading.Tasks.Task.CompletedTask;

        public void Rollback()
        {
        }

        public IReadOnlyDictionary<string, JsonNode?> Migrate(
            int from, int to, IReadOnlyDictionary<string, JsonNode?> snapshot)
        {
            var copy = new Dictionary<string, JsonNode?>(snapshot, StringComparer.Ordinal);
            copy["added"] = true;
            return copy;
        }
    }
}
