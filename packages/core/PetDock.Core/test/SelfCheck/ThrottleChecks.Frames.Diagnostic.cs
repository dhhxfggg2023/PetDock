// RFC-21 §4 的诊断读数判据（从 ThrottleChecks.Frames.cs 拆出，D-03c 的 400 行上限）。
//
// 为什么单独一份：RFC-42 §3.4 把「断言中文行首」改成「断言结构化字段 + ASCII 数据 token」，
// 这段取证因此长了一点；压注释凑行数会把判据的说明书弄丢，不如再拆一块 partial。
using System;
using System.Globalization;
using PetDock.Core.Ui;

namespace PetDock.Hosting;

internal static partial class ThrottleChecks
{
    /// <summary>⑫ 诊断包读到档位：一行文本里 tier/ticks/submitted 都是真读数，不是编的。</summary>
    private static void DiagnosticLine(Check done, ResidentFrameLoop loop)
    {
        string root = System.IO.Path.Combine(
            AppContext.BaseDirectory, "petdock-throttle-diag-" + Guid.NewGuid().ToString("N"));
        try
        {
            var service = new DiagnosticService(new PetDataStore(root), host: null, pet: null, log: null)
            {
                FrameLoop = loop,
            };
            // 读数必须在采集前后各取一次 tick 再夹住比：只在采集**之后**比 `==` 是竞态——
            // 采集与比较之间泵还能合法地走一 tick（2026-10-26 `--all` 实测 ticks=16 loopTicks=17）。
            long ticksBefore = loop.Ticks;
            DiagnosticReadings readings = service.CollectReadings();
            long ticksAfter = loop.Ticks;
            string summary = DiagnosticSections.BuildSummary(readings, RedactRules.ForCurrentMachine(root, null));
            // RFC-42 §3.4：行的标题会翻译，判据只认结构化字段与 ASCII 数据 token ——
            // 「这一行存在」由 tier=/ticks=/submitted= 三个 token 同时出现来证明。
            string tier = readings.ThrottleTier;
            bool hasTierToken = tier.Length > 0
                && summary.Contains("tier=" + tier, StringComparison.Ordinal);
            bool ok = readings.HasThrottle
                && hasTierToken
                && summary.Contains("ticks=" + readings.ThrottleTicks.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                && summary.Contains("submitted=" + readings.ThrottleSubmitted.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                // 帧预算那一行也要拼上同一个档位读数（不新增记录字段，只补一行文本）。
                && readings.FrameBudgetLine.Contains("throttle(", StringComparison.Ordinal)
                // 读数必须是**这个**循环的真读数，不是 0 占位：落在采集窗口内即可（不要求等于事后读数）。
                && readings.ThrottleTicks > 0
                && readings.ThrottleTicks >= ticksBefore
                && readings.ThrottleTicks <= ticksAfter;
            done("ui-throttle-diagnostic-line", ok,
                "tier=" + tier
                + " ticks=" + readings.ThrottleTicks.ToString(CultureInfo.InvariantCulture)
                + " window=" + ticksBefore.ToString(CultureInfo.InvariantCulture)
                + ".." + ticksAfter.ToString(CultureInfo.InvariantCulture)
                + " submitted=" + readings.ThrottleSubmitted.ToString(CultureInfo.InvariantCulture)
                + " hasTierToken=" + Flag(hasTierToken)
                + " line=" + ExtractThrottleLine(summary));
        }
        finally
        {
            try
            {
                if (System.IO.Directory.Exists(root))
                {
                    System.IO.Directory.Delete(root, recursive: true);
                }
            }
            catch (Exception)
            {
                // 临时目录删不掉不影响判据结论（先例：DiagnosticChecks）。
            }
        }
    }

    /// <summary>取证用：按数据 token（tier= 与 ticks= 同行）找出那条读数，不认行首标题。</summary>
    private static string ExtractThrottleLine(string summary)
    {
        foreach (string line in summary.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.Contains("tier=", StringComparison.Ordinal)
                && trimmed.Contains("ticks=", StringComparison.Ordinal))
            {
                return trimmed;
            }
        }

        return "(none)";
    }
}
