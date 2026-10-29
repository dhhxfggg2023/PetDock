// agent 状态陪伴层的判据（2026-10-29，P4 第三块）。命令形态 `--uitest`，判据行 `ui-agent-*`。
//
// 判的是三条诚实规则与它的边界：文件不在 / 读不懂 / 跑着但很久没更新，三种都不能说成「空闲」。
// 素材是当场写的小 JSON —— 这一层本来就不读图片，判据也不该依赖外部文件。
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PetDock.Core.PetRuntime;

namespace PetDock.Hosting;

internal static class AgentChecks
{
    private const string AgentId = "petdock.agent";

    internal delegate void Report(string name, bool ok, string detail);

    public static void Run(Report check)
    {
        PetSurfaceRoster roster = PetSurfaceRoster.Discover(PluginHost.ProbePluginsRoot());
        using PetInstance? pet = roster.BuildInstance("agent-check");
        IPetCompanion? agent = Find(pet);
        check("ui-agent-loaded", agent is not null, $"companions={pet?.Companions.Count ?? 0}");
        if (agent is null)
        {
            return;
        }

        string root = Path.Combine(Path.GetTempPath(), "petdock-agentcheck-" + Guid.NewGuid().ToString("N"));
        string? saved = Environment.GetEnvironmentVariable("PETDOCK_AGENT_STATE");
        try
        {
            Directory.CreateDirectory(root);
            Environment.SetEnvironmentVariable("PETDOCK_AGENT_STATE", Path.Combine(root, "state.json"));
            Running(check, agent.GetType(), root);
            Stale(check, agent.GetType(), root);
            Transition(check, agent.GetType(), root);
            Unreadable(check, agent.GetType(), root);
            UnknownState(check, agent.GetType(), root);
            Language(check, agent.GetType(), root);
            Missing(check, agent.GetType(), root);
        }
        catch (Exception error)
        {
            check("ui-agent-crash", false, error.GetType().Name + ": " + error.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PETDOCK_AGENT_STATE", saved);
            TryDelete(root);
        }
    }

    private static void Running(Report check, Type type, string root)
    {
        string path = Path.Combine(root, "running.json");
        Write(path, "{\"state\":\"running\",\"task\":\"跑测试\",\"progress\":0.25,"
            + "\"updatedAt\":\"" + Iso(DateTime.UtcNow) + "\"}");
        var host = new Host("zh-CN");
        IPetCompanion companion = Init(type, host, path);
        string status = companion.Report.Status;
        string detail = companion.Report.Detail;
        check(
            "ui-agent-running",
            status.Contains("运行中", StringComparison.Ordinal)
                && status.Contains("跑测试", StringComparison.Ordinal)
                && detail.Contains("25%", StringComparison.Ordinal),
            $"status={status} detail={detail}");

        string? said = companion.Invoke("refresh");
        string? unknown = companion.Invoke("no-such-command");
        check(
            "ui-agent-refresh",
            said is not null && said.Contains("运行中", StringComparison.Ordinal) && unknown is null,
            $"said={said ?? "(null)"} unknown={unknown ?? "(null)"}");
    }

    private static void Stale(Report check, Type type, string root)
    {
        string path = Path.Combine(root, "stale.json");
        Write(path, "{\"state\":\"running\",\"task\":\"长任务\","
            + "\"updatedAt\":\"" + Iso(DateTime.UtcNow.AddMinutes(-10)) + "\"}");
        IPetCompanion companion = Init(type, new Host("zh-CN"), path);
        string status = companion.Report.Status;
        string detail = companion.Report.Detail;
        check(
            "ui-agent-stale",
            status.Contains("可能已停", StringComparison.Ordinal) && detail.Contains("分钟前", StringComparison.Ordinal),
            $"status={status} detail={detail}");
    }

    /// <summary>从 running 变成 done：说一句话（用户不一定盯着面板），并且状态跟着变。</summary>
    private static void Transition(Report check, Type type, string root)
    {
        string path = Path.Combine(root, "transition.json");
        Write(path, "{\"state\":\"running\",\"task\":\"写测试\"}");
        IPetCompanion companion = Init(type, new Host("zh-CN"), path);
        Thread.Sleep(30);
        Write(path, "{\"state\":\"done\",\"task\":\"写测试\"}");

        CompanionTick tick = companion.Tick(1000, WorldSnapshot.Empty(default));
        check(
            "ui-agent-transition",
            !string.IsNullOrWhiteSpace(tick.Say)
                && companion.Report.Status.Contains("已完成", StringComparison.Ordinal),
            $"say={tick.Say ?? "(none)"} status={companion.Report.Status}");
    }

    private static void Unreadable(Report check, Type type, string root)
    {
        string path = Path.Combine(root, "broken.json");
        Write(path, "{ this is not json");
        IPetCompanion companion = Init(type, new Host("zh-CN"), path);
        check(
            "ui-agent-unreadable",
            companion.Report.Status.Contains("读不出来", StringComparison.Ordinal)
                && companion.Report.Detail == path,
            $"status={companion.Report.Status} detail={companion.Report.Detail}");
    }

    private static void UnknownState(Report check, Type type, string root)
    {
        string path = Path.Combine(root, "unknown.json");
        Write(path, "{\"state\":\"compacting\",\"task\":\"整理上下文\"}");
        IPetCompanion companion = Init(type, new Host("zh-CN"), path);
        string status = companion.Report.Status;
        check(
            "ui-agent-unknown-state",
            status.Contains("compacting", StringComparison.Ordinal)
                && !status.Contains("空闲", StringComparison.Ordinal),
            $"status={status}");
    }

    private static void Language(Report check, Type type, string root)
    {
        string path = Path.Combine(root, "english.json");
        Write(path, "{\"state\":\"running\",\"task\":\"run tests\"}");
        IPetCompanion companion = Init(type, new Host("en-US"), path);
        check(
            "ui-agent-language",
            companion.Report.Status.Contains("Running", StringComparison.Ordinal)
                && companion.Report.Commands.Count > 0,
            $"status={companion.Report.Status} commands={companion.Report.Commands.Count}");
    }

    private static void Missing(Report check, Type type, string root)
    {
        string path = Path.Combine(root, "never-written.json");
        IPetCompanion companion = Init(type, new Host("zh-CN"), path);
        check(
            "ui-agent-missing",
            companion.Report.Status.Contains("未发现", StringComparison.Ordinal)
                && companion.Report.Detail == path,
            $"status={companion.Report.Status} detail={companion.Report.Detail}");
    }

    private static IPetCompanion Init(Type type, Host host, string path)
    {
        Environment.SetEnvironmentVariable("PETDOCK_AGENT_STATE", path);
        if (Activator.CreateInstance(type) is not IPetCompanion companion)
        {
            throw new InvalidOperationException("cannot construct " + type.Name);
        }

        companion.InitAsync(new CompanionContext(
                "agent-check", "Check", null, new ResourceBudget(1000.0 / 30.0, 1000.0, 1 << 20), host))
            .GetAwaiter()
            .GetResult();
        return companion;
    }

    private static IPetCompanion? Find(PetInstance? pet)
    {
        if (pet is null)
        {
            return null;
        }

        foreach (IPetCompanion companion in pet.Companions)
        {
            if (string.Equals(companion.Meta.Id, AgentId, StringComparison.Ordinal))
            {
                return companion;
            }
        }

        return null;
    }

    private static string Iso(DateTime utc) => utc.ToString("o", System.Globalization.CultureInfo.InvariantCulture);

    private static void Write(string path, string json) => File.WriteAllText(path, json, new UTF8Encoding(false));

    private static void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (Exception)
        {
            // 临时目录删不掉不影响判据结论。
        }
    }

    /// <summary>宿主口子桩件：判据要看语言切换、说话与落盘请求。</summary>
    private sealed class Host : ICompanionHost
    {
        public Host(string language) => Language = language;

        public string Language { get; }

        public int Commits { get; private set; }

        public List<string> Said { get; } = new();

        public List<string> Sounds { get; } = new();

        public bool CommitState()
        {
            Commits++;
            return true;
        }

        public void Say(string text)
        {
            if (!string.IsNullOrWhiteSpace(text))
            {
                Said.Add(text);
            }
        }

        public void PlaySound(string soundPath) => Sounds.Add(soundPath);
    }
}
