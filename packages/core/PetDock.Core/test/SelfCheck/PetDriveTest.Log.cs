// RFC-23 §4 的判据（--drivetest 的第 10 组）：插件日志面六条。
//
// 为什么这一组值得单独存在：原规格 20 §1 把 `@petdock/sdk/log` 标成 stable，而全仓在此之前
// **没有任何一条实现路径**（JS 侧那段 `__bind` 无人调用、C# 侧连类型都没有）。一条 stable
// 规格没有判据，就只能靠「文档说有」来相信它 —— 这正是本组要消灭的东西。
//
// 判据用的是真收口（HostPluginLog）+ 临时审计目录，不是桩件替代品：
// 位置归属错了、级别漏记了，都必须在**真的会写进流水的那条路径**上暴露。
using System;
using System.Collections.Generic;
using System.IO;
using PetDock.Core.Compositor;
using PetDock.Core.PetRuntime;

namespace PetDock.Hosting;

internal static partial class PetDriveTest
{
    /// <summary>组 10：插件日志面（RFC-23 §4）。</summary>
    private static int PluginLogChecks()
    {
        int failures = 0;
        string dir = Path.Combine(SelfTestScratch.Base, "petdock-log-check-" + Guid.NewGuid().ToString("N"));
        IPluginLogSink? previousSink = PluginLog.Sink;
        try
        {
            var trail = new AuditTrail(dir, RedactRules.ForCurrentMachine(dir, null));
            trail.Start();
            var sink = new HostPluginLog(trail);
            PluginLog.Sink = sink;
            PluginLog.ResetCounters();

            // ① 归属：作用域里打的日志记到作用域那个 id 上。
            using (PluginLog.Scope("com.example.probe"))
            {
                PluginLog.Info("attributed-line");
            }

            // ② 作用域外打的**不**归到上一个插件头上（宁可 (unattributed)，不许猜）。
            PluginLog.Warn("outside-scope");

            // ③ 级别：warn/error 进流水，info 不进。
            using (PluginLog.Scope("com.example.probe"))
            {
                PluginLog.Warn("warn-line");
                PluginLog.Error("error-line");
                PluginLog.Info("info-line");
            }

            PluginLog.Info("after");
            trail.FlushNow();

            AuditTrailRead read = AuditTrail.ReadAll(trail.TrailDirectory);
            var rows = new List<string>();
            foreach (AuditTrailRow row in read.Rows)
            {
                rows.Add(row.PluginId + "|" + row.Kind + "|" + row.Detail);
            }

            string joined = string.Join(" ;; ", rows);
            int probeLogRows = 0;
            foreach (AuditTrailRow row in read.Rows)
            {
                if (row.Kind == HostPluginLog.KindLog && row.PluginId == "com.example.probe")
                {
                    probeLogRows++;
                }
            }

            Check(ref failures, "plugin-log-attributed",
                sink.Written >= 3 && sink.ErrorCountOf("com.example.probe") == 1,
                $"written={sink.Written} errors={sink.ErrorCountOf("com.example.probe")}");

            Check(ref failures, "plugin-log-unattributed-not-guessed",
                PluginLog.UnattributedCount >= 1 && sink.ErrorCountOf(PluginLog.Unattributed) == 0,
                $"unattributed={PluginLog.UnattributedCount} guessedErrors={sink.ErrorCountOf(PluginLog.Unattributed)}");

            // warn + error 各一行；info/debug 不进流水（info 不是「一次决定」，debug 不落盘）。
            Check(ref failures, "plugin-log-warn-and-error-to-trail",
                probeLogRows == 2 && sink.TrailRows == 2,
                $"rows={probeLogRows} trailRows={sink.TrailRows} all=[{joined}]");

            Check(ref failures, "plugin-log-error-counted",
                sink.ErrorCountOf("com.example.probe") == 1 && sink.ErrorCountOf("com.example.other") == 0,
                $"probe={sink.ErrorCountOf("com.example.probe")} other={sink.ErrorCountOf("com.example.other")}");

            // ④ debug：接受调用、不落盘、只计数。
            long debugBefore = PluginLog.DebugDropped;
            using (PluginLog.Scope("com.example.probe"))
            {
                PluginLog.Debug("debug-line");
            }

            AuditTrailRead afterDebug = AuditTrail.ReadAll(trail.TrailDirectory);
            bool debugInTrail = false;
            int debugLines = 0;
            foreach (AuditTrailRow row in afterDebug.Rows)
            {
                if (row.Detail != null && row.Detail.Contains("level=debug", StringComparison.Ordinal))
                {
                    debugInTrail = true;
                }

                if (string.Equals(row.Kind, HostPluginLog.KindLog, StringComparison.Ordinal))
                {
                    debugLines++;
                }
            }

            Check(ref failures, "plugin-log-debug-dropped",
                !debugInTrail && debugLines == 2 && PluginLog.DebugDropped == debugBefore + 1,
                $"inTrail={debugInTrail} logRows={debugLines} dropped={PluginLog.DebugDropped - debugBefore}");

            // ⑤ 收口抛异常不传染：换上一个必抛的收口，调用方不得看到异常。
            PluginLog.Sink = new ThrowingSink();
            long sinkFailsBefore = PluginLog.SinkFailures;
            bool threw = false;
            try
            {
                using (PluginLog.Scope("com.example.probe"))
                {
                    PluginLog.Error("sink-boom");
                }
            }
            catch (Exception)
            {
                threw = true;
            }

            Check(ref failures, "plugin-log-sink-throw-isolated",
                !threw && PluginLog.SinkFailures == sinkFailsBefore + 1,
                $"threw={threw} sinkFails={PluginLog.SinkFailures - sinkFailsBefore}");

            trail.Complete();
            trail.Dispose();
        }
        catch (Exception error)
        {
            Check(ref failures, "plugin-log-harness",
                false,
                error.GetType().Name + ": " + error.Message);
        }
        finally
        {
            PluginLog.Sink = previousSink;
            TryDelete(dir);
        }

        return failures;
    }

    /// <summary>必抛的收口：用来证明「日志故障不传染业务」（RFC-23 §3.4）。</summary>
    private sealed class ThrowingSink : IPluginLogSink
    {
        public void Write(string pluginId, PluginLogLevel level, string message)
            => throw new InvalidOperationException("disk-full (simulated)");
    }

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
}
