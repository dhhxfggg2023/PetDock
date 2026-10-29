// 陪伴层的判据（2026-10-29，P4 插件线第一块）。命令形态 `--uitest`，判据行 `ui-companion-*`。
//
// 判的是「宿主把插件摆上来、转发得对、数值真的过状态层」，不是玩法好不好玩
// （那是产品判断，不是判据能回答的问题）。真实插件从 packages/plugins 装载，
// 不用桩件顶替：这一条要同时证明「名册真的认出了 contributes.companions」。
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using PetDock.Core.PetRuntime;
using PetDock.Core.Ui;

namespace PetDock.Hosting;

internal static class CompanionChecks
{
    private const string CareId = "petdock.care";

    private static readonly ResourceBudget Budget = new(1000.0 / 30.0, 1000.0, 1 << 20);

    internal delegate void Report(string name, bool ok, string detail);

    public static void Run(Report check)
    {
        PetSurfaceRoster roster = PetSurfaceRoster.Discover(PluginHost.ProbePluginsRoot());
        check("ui-companion-roster", roster.CompanionTypes >= 1, $"types={roster.CompanionTypes}");

        using PetInstance? pet = roster.BuildInstance("companion-check");
        IPetCompanion? care = FindCare(pet);
        check(
            "ui-companion-bound",
            care is not null,
            $"loaded={pet?.Companions.Count ?? 0}");
        if (pet is null || care is null)
        {
            return;
        }

        // 每只宠物各造一份：两只共用一个对象会互相改数值（InstallCompanions 的注释）。
        using PetInstance? twin = roster.BuildInstance("companion-check-2");
        IPetCompanion? other = FindCare(twin);
        check(
            "ui-companion-per-instance",
            other is not null && !ReferenceEquals(care, other),
            $"first={care.GetHashCode()} second={other?.GetHashCode() ?? 0}");

        Commands(check, pet, care);
        Gestures(check, pet, care);
        Snapshot(check, pet, care);
        Persistence(check, care);
        check("ui-companion-faults", pet.CompanionFaults == 0, $"faults={pet.CompanionFaults}");
    }

    private static void Commands(Report check, PetInstance pet, IPetCompanion care)
    {
        // InvokeCompanion 的返回值是「失败原因」：null = 成功（话由宿主自己说出去）。
        string before = care.Report.Status;
        string? problem = pet.InvokeCompanion(CareId, "feed");
        string after = care.Report.Status;
        check(
            "ui-companion-command",
            problem is null && !string.Equals(before, after, StringComparison.Ordinal),
            $"problem={problem ?? "(null)"} before={before} after={after}");

        string? missing = pet.InvokeCompanion("petdock.none", "feed");
        check(
            "ui-companion-missing-role",
            string.Equals(missing, I18n.Get("ui.companion.missing"), StringComparison.Ordinal),
            $"returned={missing ?? "(null)"}");

        string frozen = care.Report.Status;
        string? unknown = pet.InvokeCompanion(CareId, "no-such-command");
        check(
            "ui-companion-unknown-command",
            unknown is null && string.Equals(frozen, care.Report.Status, StringComparison.Ordinal),
            $"returned={unknown ?? "(null)"} status={care.Report.Status}");
    }

    /// <summary>点按是「桌面动作」那条路（与面板命令分开）：这里验它真的转到了插件。</summary>
    private static void Gestures(Report check, PetInstance pet, IPetCompanion care)
    {
        string before = care.Report.Status;
        for (int i = 0; i < 6; i++)
        {
            pet.NotifyCompanions(CompanionGesture.Poke);
        }

        string after = care.Report.Status;
        check(
            "ui-companion-gesture",
            !string.Equals(before, after, StringComparison.Ordinal),
            $"before={before} after={after}");
    }

    /// <summary>Report 是快照：拿在手里的旧对象不随后台 tick 变化。</summary>
    private static void Snapshot(Report check, PetInstance pet, IPetCompanion care)
    {
        CompanionReport held = care.Report;
        string heldText = held.Status;
        pet.TickCompanions(30 * 60 * 1000, WorldSnapshot.Empty(default));
        string fresh = care.Report.Status;
        check(
            "ui-companion-snapshot",
            string.Equals(heldText, held.Status, StringComparison.Ordinal)
                && !string.Equals(heldText, fresh, StringComparison.Ordinal),
            $"held={held.Status} fresh={fresh}");
    }

    /// <summary>
    /// 状态层往返与离线折旧：把上次落盘时间往前拨 3 小时，初始化后饱腹应掉 72 点
    /// （插件按分钟记账，见 CareValues.Decay）。命令之后新实例要读到同一个值，而不是回到默认。
    /// </summary>
    private static void Persistence(Report check, IPetCompanion care)
    {
        var state = new StubState();
        var host = new CompanionHostStub();
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long threeHoursAgo = now - (180L * 60 * 1000);
        state.Write(new Dictionary<string, JsonNode?>(StringComparer.Ordinal)
        {
            ["care.fed"] = JsonValue.Create(100.0),
            ["care.mood"] = JsonValue.Create(100.0),
            ["care.vim"] = JsonValue.Create(100.0),
            ["care.bond"] = JsonValue.Create(0.0),
            ["care.since"] = JsonValue.Create(threeHoursAgo),
            ["care.at"] = JsonValue.Create(threeHoursAgo),
        });

        Type type = care.GetType();
        IPetCompanion? first = New(type);
        IPetCompanion? second = New(type);
        if (first is null || second is null)
        {
            check("ui-companion-state", false, "cannot construct a second instance");
            return;
        }

        try
        {
            first.InitAsync(new CompanionContext("companion-check", "Check", state, Budget, host))
                .GetAwaiter()
                .GetResult();
            double fed = NumberOr(state.Read("care.fed"), -1);
            check(
                "ui-companion-offline-decay",
                Math.Abs(fed - 28) < 0.6,
                $"fed={fed:0.##} expected≈28 (100 - 0.4/min × 180min)");

            first.Invoke("feed");
            double fedAfter = NumberOr(state.Read("care.fed"), -1);
            check(
                "ui-companion-commit",
                host.Commits >= 1 && fedAfter > fed,
                $"commits={host.Commits} fed={fedAfter:0.##}");

            // 命令要顺带请宿主播音效（P4 音频）：路径由插件自己拼，文件必须真的在发布产物里。
            string? sound = host.Sounds.Find(path => path.EndsWith("feed.wav", StringComparison.OrdinalIgnoreCase));
            check(
                "ui-audio-companion",
                sound is not null && File.Exists(sound),
                $"requested={sound ?? "(none)"} exists={sound is not null && File.Exists(sound)}");

            second.InitAsync(new CompanionContext("companion-check", "Check", state, Budget, host))
                .GetAwaiter()
                .GetResult();
            double restored = NumberOr(state.Read("care.fed"), -1);
            check(
                "ui-companion-state-roundtrip",
                restored > 50,
                $"restored={restored:0.##} (command result carried across instances)");
        }
        catch (Exception error)
        {
            check("ui-companion-state", false, error.GetType().Name + ": " + error.Message);
        }
    }

    private static IPetCompanion? FindCare(PetInstance? pet)
    {
        if (pet is null)
        {
            return null;
        }

        foreach (IPetCompanion companion in pet.Companions)
        {
            if (string.Equals(companion.Meta.Id, CareId, StringComparison.Ordinal))
            {
                return companion;
            }
        }

        return null;
    }

    private static IPetCompanion? New(Type type) => Activator.CreateInstance(type) as IPetCompanion;

    /// <summary>与插件同一条读法：JSON 文本解析，JsonValue&lt;long&gt; 与 JsonValue&lt;double&gt; 都认。</summary>
    private static double NumberOr(JsonNode? node, double fallback)
    {
        if (node is null)
        {
            return fallback;
        }

        string text = node.ToJsonString();
        return double.TryParse(
            text,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out double number)
            ? number
            : fallback;
    }

    /// <summary>宿主口子的桩件：记下话与落盘请求（判据要证明命令之后真的请求了提交）。</summary>
    private sealed class CompanionHostStub : ICompanionHost
    {
        public string Language => "zh-CN";

        public int Commits { get; private set; }

        public List<string> Said { get; } = new();

        /// <summary>插件请宿主播放的音效（判据：命令之后真的请求了，且文件在）。</summary>
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
