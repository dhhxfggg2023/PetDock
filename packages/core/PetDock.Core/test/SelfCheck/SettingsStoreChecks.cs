// 设置落盘的「唯一写者」判据（2026-10-28 审计 P0）。
//
// 原来每条改动都是「读出 → 改一个字段 → 整份写回」，中间没有锁，临时文件名还是固定的
// settings.json.tmp：UI 线程（切语言、应用设置、记窗口位置）与后台线程（插件启停）同时写时，
// 后写的那个拿着旧快照把前一个刚写的字段改回去，两个写还会抢同一个临时文件。
//
// 三条判据各挡一种写法：
//   1. 并发改两个不同字段 → 两个都得在（挡「后写覆盖前写」）；
//   2. 并发写完不留临时文件（挡「固定 .tmp 被抢，留下半份残骸」）；
//   3. 没有变化时 Update 不写盘（挡「点一下没变也重写整份文件」）。
//
// 真落盘：判据用真 UserSettingsStore 写真实文件，读回用的是另一个 store 对象。
using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PetDock.Core.Compositor;

namespace PetDock.Hosting;

internal static class SettingsStoreChecks
{
    /// <summary>与其它判据文件同一形态：每个文件自带一份（这些文件之间不共享类型）。</summary>
    internal delegate void Check(string name, bool ok, string detail);

    /// <summary>判据体的形状（自声明理由同 HostingTypes.cs 的先例）。</summary>
    private delegate void RootBody(Check check, string root);

    /// <summary>每条线程改多少轮：少到跑得快，多到旧写法几乎必然丢一次更新。</summary>
    private const int Rounds = 120;

    private const double WantedIdle = 50.0;
    private static readonly Point WantedPosition = new(321, 123);

    public static void Run(Check check)
    {
        WithRoot(check, "ui-settings-update-atomic", Atomic);
        WithRoot(check, "ui-settings-update-no-temp-leftover", NoTempLeftover);
        WithRoot(check, "ui-settings-update-skips-unchanged-write", SkipsUnchangedWrite);
        WithRoot(check, "ui-settings-pet-size-persisted", PetSizePersisted);
        WithRoot(check, "ui-settings-pet-size-per-pet", PerPetSize);
    }

    /// <summary>① 两条线程各改一个字段，最后两个字段都得是各自写过的值。</summary>
    private static void Atomic(Check check, string root)
    {
        var store = new UserSettingsStore(root);
        store.Save(UserSettings.Default);

        RunConcurrently(store);

        UserSettings final = new UserSettingsStore(root).Load();
        bool idleKept = Math.Abs(final.IdleFrameBudgetMs - WantedIdle) < 0.001;
        bool positionKept = final.PetPosition is Point kept && kept == WantedPosition;
        check(
            "ui-settings-update-atomic",
            idleKept && positionKept,
            $"idle={final.IdleFrameBudgetMs:0.###} pos={(final.PetPosition is Point p ? p.X + "," + p.Y : "(none)")} rounds={Rounds}");
    }

    /// <summary>② 并发写完：目录里不许剩下 *.tmp，正式文件还得读得回来。</summary>
    private static void NoTempLeftover(Check check, string root)
    {
        var store = new UserSettingsStore(root);
        store.Save(UserSettings.Default with { Language = "en-US" });

        RunConcurrently(store);

        string[] leftovers = Directory.GetFiles(root, "*.tmp");
        UserSettings reloaded = new UserSettingsStore(root).Load();
        bool parsed = string.Equals(reloaded.Language, "en-US", StringComparison.Ordinal);
        check(
            "ui-settings-update-no-temp-leftover",
            leftovers.Length == 0 && parsed,
            $"tmp={leftovers.Length} language={reloaded.Language}");
    }

    /// <summary>③ 返回同一个实例 = 没有要改的：文件的时间戳不许动（证明真的没写盘）。</summary>
    private static void SkipsUnchangedWrite(Check check, string root)
    {
        var store = new UserSettingsStore(root);
        store.Save(UserSettings.Default with { Language = "zh-CN" });
        DateTime before = File.GetLastWriteTimeUtc(store.Path);

        // 文件系统时间粒度按毫秒算，睡一下才分得开「没写」和「写了但内容一样」。
        Thread.Sleep(60);
        store.Update(current => current);
        DateTime after = File.GetLastWriteTimeUtc(store.Path);

        check(
            "ui-settings-update-skips-unchanged-write",
            before == after,
            $"before={before:HH:mm:ss.fff} after={after:HH:mm:ss.fff}");
    }

    /// <summary>④ 宠物尺寸落盘并读回；越界值被 Clamped() 拉回上限；没设过时保持 null（由面板回落 128）。</summary>
    private static void PetSizePersisted(Check check, string root)
    {
        var store = new UserSettingsStore(root);
        store.Save(UserSettings.Default with { PetSize = 160 });
        int? reloaded = new UserSettingsStore(root).Load().PetSize;

        store.Update(current => current with { PetSize = 999 });
        int? clamped = new UserSettingsStore(root).Load().PetSize;

        store.Save(UserSettings.Default);
        int? unset = new UserSettingsStore(root).Load().PetSize;

        check(
            "ui-settings-pet-size-persisted",
            reloaded == 160 && clamped == UserSettings.MaxPetSize && unset is null,
            $"reloaded={reloaded?.ToString() ?? "(none)"} clamped={clamped?.ToString() ?? "(none)"} unset={unset?.ToString() ?? "(none)"}");
    }

    /// <summary>
    /// 尺寸按宠物单独记（2026-10-31；用户：「调整大小应该是每个宠物单独调整，你这样我怎么调整」）。
    ///
    /// 要挡的写法：一条全局 PetSize —— 拖一次滑块，桌上每只一起变成同一个数，
    /// 「大猫小老鼠」根本做不到。判据：三只各设一个值 → 重读三只都保住了；
    /// 没设过的那只回落到全局默认；越界值被夹进 64..256（一个手滑改坏的文件不该把窗口拽到 9999）。
    /// </summary>
    private static void PerPetSize(Check check, string root)
    {
        var store = new UserSettingsStore(root);
        // with 表达式后面不能直接接 .Method()（C# 语法），先赋给局部变量再逐只设。
        UserSettings start = UserSettings.Default with { PetSize = 128 };
        store.Save(start.WithPetSize("user.big", 224).WithPetSize("user.small", 72));

        UserSettings reloaded = new UserSettingsStore(root).Load();
        bool keptBig = reloaded.SizeOf("user.big") == 224;
        bool keptSmall = reloaded.SizeOf("user.small") == 72;
        bool fallback = reloaded.SizeOf("user.never-set") == 128;
        bool entries = reloaded.PetSizes is not null && reloaded.PetSizes.Count == 2;

        // 越界：写 9999 之后再读回来必须是上限，不是 9999。
        store.Update(current => current.WithPetSize("user.big", 9999));
        int clamped = new UserSettingsStore(root).Load().SizeOf("user.big");

        int big = reloaded.SizeOf("user.big");
        int small = reloaded.SizeOf("user.small");
        int never = reloaded.SizeOf("user.never-set");
        int count = reloaded.PetSizes?.Count ?? 0;
        check(
            "ui-settings-pet-size-per-pet",
            keptBig && keptSmall && fallback && entries && clamped == UserSettings.MaxPetSize,
            $"big={big} small={small} fallback={never} entries={count} clamped={clamped}");

        // 反向：全局默认改了，逐只设过的那两只不受影响（单独设的值不能被全局值盖掉）。
        store.Update(current => current with { PetSize = 96 });
        UserSettings afterGlobal = new UserSettingsStore(root).Load();
        int smallAfter = afterGlobal.SizeOf("user.small");
        int neverAfter = afterGlobal.SizeOf("user.never-set");
        check(
            "ui-settings-pet-size-not-overridden",
            smallAfter == 72 && neverAfter == 96,
            $"small={smallAfter} unset={neverAfter} global={afterGlobal.PetSize}");
    }

    /// <summary>
    /// 两条线程背靠背地各改一个字段。Barrier 让它们真的同时进循环 ——
    /// 否则第一条跑完第二条才开始，旧写法也能绿（判据就成了摆设）。
    /// </summary>
    private static void RunConcurrently(UserSettingsStore store)
    {
        using var gate = new Barrier(2);
        Task idle = Task.Run(() =>
        {
            gate.SignalAndWait();
            for (int i = 0; i < Rounds; i++)
            {
                store.Update(current => current with { IdleFrameBudgetMs = WantedIdle });
            }
        });

        Task position = Task.Run(() =>
        {
            gate.SignalAndWait();
            for (int i = 0; i < Rounds; i++)
            {
                store.Update(current => current with { PetPosition = WantedPosition });
            }
        });

        Task.WaitAll(idle, position);
    }

    /// <summary>起一个一次性数据区、跑一条判据、无论成败都收走它（理由同 PetPositionChecks.WithRoot）。</summary>
    private static void WithRoot(Check check, string name, RootBody body)
    {
        string root = Path.Combine(SelfTestScratch.Base, "petdock-settings-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        try
        {
            body(check, root);
        }
        catch (Exception error)
        {
            check(name, false, $"setup-failed {error.GetType().Name}: {error.Message}");
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static void TryDelete(string root)
    {
        try
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
        catch (IOException)
        {
            // 临时目录删不掉不算判据失败：它不在被测对象里。
        }
    }
}
