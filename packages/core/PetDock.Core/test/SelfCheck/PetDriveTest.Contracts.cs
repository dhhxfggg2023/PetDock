// --drivetest 的第三块：命中仲裁（原规格 11 §2.2）与状态层语义（原规格 11 §6）。
// 拆自 PetDriveTest.cs（D-03c：单文件 ≤400 行）；命令形态与总体判据见 PetDriveTest.Run。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using PetDock.Core.PetRuntime;

namespace PetDock.Hosting;

internal static partial class PetDriveTest
{
    /// <summary>组 7（原规格 11 §2.2）：命中仲裁 hitmap &gt; bitmap alpha，且 alpha===0 永远放行穿透。</summary>
    private static int Hitmap()
    {
        int failures = 0;
        PetInstance instance = MakeInstance(out _, out _);
        instance.AttachAndStart(8, 8, 1.0);
        instance.Start();
        Frame frame = instance.QueryFrame(0);

        Check(ref failures, "hitmap-length", frame.Hitmap is not null
                && frame.Hitmap.Length == frame.BitmapHandle.PixelCount,
            $"hitmap={frame.Hitmap?.Length ?? 0} pixels={frame.BitmapHandle.PixelCount}");
        Check(ref failures, "hitmap-center-hits", instance.HitsAt(3, 3), "center-hitmap=1");

        // alpha===0 永远放行穿透：即使 hitmap 在该点写 1 也不许命中。
        // 这里用**自造**的 8x8 位图，而不是 StubView 的帧：StubView 有真素材时会读
        // pets/sample-pet，四角也可能被抗锯齿糊上 alpha，测试就会随素材变红。
        using var probeBitmap = new Bitmap(8, 8, PixelFormat.Format32bppPArgb);
        using (Graphics graphics = Graphics.FromImage(probeBitmap))
        {
            graphics.Clear(Color.Transparent);
            using var brush = new SolidBrush(Color.FromArgb(255, 230, 80, 40));
            graphics.FillEllipse(brush, 2, 2, 4, 4);
        }

        var probeHandle = new BitmapHandle(9001, probeBitmap);
        byte[] forced = new byte[probeHandle.PixelCount];
        Array.Fill(forced, (byte)1);
        Frame overrideFrame = new(probeHandle, frame.Size, frame.Scale, null, forced);
        overrideFrame.CacheAlpha();
        Check(ref failures, "hitmap-alpha-zero-wins", !overrideFrame.HitsAt(0, 0),
            "alpha=0 时 hitmap=1 也必须穿透");
        // 圆心 (4,4)：alpha!=0 且 hitmap=1 → 可命中；(7,7) 在圆外（alpha=0）→ 穿透。
        Check(ref failures, "hitmap-alpha-nonzero-uses-map", overrideFrame.HitsAt(4, 4),
            "alpha!=0 且 hitmap=1 → 可命中");

        // 长度不对的命中图是插件写坏了：必须抛，不许静默当命中（那会把整窗变成可点）。
        bool lengthRefused = false;
        try
        {
            Frame shortMap = new(frame.BitmapHandle, frame.Size, frame.Scale, null, new byte[4]);
            shortMap.HitsAt(4, 4);
        }
        catch (ArgumentException)
        {
            lengthRefused = true;
        }

        Check(ref failures, "hitmap-length-refused", lengthRefused, "长度不足的 hitmap 被拒");
        // 隐藏档一律不可命中（原规格 11:190 的穿透优先，也是「隐藏」这条降级档的语义）。
        instance.Display.Advance(DisplayTier.Hidden, "drive-hide", 1);
        Check(ref failures, "hitmap-hidden-not-hittable", !instance.HitsAt(4, 4), "Hidden 档不可命中");
        instance.Dispose();
        return failures;
    }

    /// <summary>组 8（原规格 11 §6）：write 只在缓冲区、commit 才落盘、rollback 丢未提交、migrate 纯函数、越限拒绝。</summary>
    private static int StateSemantics()
    {
        int failures = 0;
        var state = new StubState();
        var patch = new Dictionary<string, System.Text.Json.Nodes.JsonNode?>
        {
            ["mood"] = System.Text.Json.Nodes.JsonValue.Create("calm"),
        };
        state.Write(patch);
        Check(ref failures, "state-buffered-only", state.Disk.Count == 0 && state.Read("mood") is not null,
            $"disk={state.Disk.Count} buffer={state.Read("mood")?.ToJsonString()}");
        state.CommitAsync().GetAwaiter().GetResult();
        Check(ref failures, "state-commit-persists", state.Disk.Count == 1 && state.Commits == 1,
            $"disk={state.Disk.Count} commits={state.Commits}");

        // rollback 的语义（原规格 11:213「崩溃：缓冲区全丢，磁盘无影响」）：丢弃未落盘的修改，
        // 已提交的值仍在 —— 不是把缓冲清空（清空会把已落盘的值也一起抹掉）。
        state.Write(new Dictionary<string, System.Text.Json.Nodes.JsonNode?>
        {
            ["uncommitted"] = System.Text.Json.Nodes.JsonValue.Create("gone"),
        });
        Check(ref failures, "state-write-is-buffered", state.Read("uncommitted") is not null,
            "write 之后缓冲区里读得到");
        state.Rollback();
        Check(ref failures, "state-rollback-drops-buffer",
            state.Rollbacks == 1 && state.Read("uncommitted") is null
                && state.Read("mood")?.GetValue<string>() == "calm" && state.Disk.Count == 1,
            $"uncommitted={state.Read("uncommitted")?.ToJsonString() ?? "(null)"}"
                + $" mood={state.Read("mood")?.ToJsonString() ?? "(null)"} disk={state.Disk.Count}");

        var snapshot = new Dictionary<string, System.Text.Json.Nodes.JsonNode?>
        {
            ["schemaVersion"] = System.Text.Json.Nodes.JsonValue.Create(1),
        };
        IReadOnlyDictionary<string, System.Text.Json.Nodes.JsonNode?> migrated = state.Migrate(1, 2, snapshot);
        Check(ref failures, "state-migrate-pure",
            snapshot["schemaVersion"]?.GetValue<int>() == 1
                && migrated["schemaVersion"]?.GetValue<int>() == 2,
            "入参未被 mutate 且返回了新载荷");

        bool overLimitRefused = false;
        try
        {
            state.KeyLimitBytes = 16;
            state.Write(new Dictionary<string, System.Text.Json.Nodes.JsonNode?>
            {
                ["big"] = System.Text.Json.Nodes.JsonValue.Create(new string('x', 64)),
            });
        }
        catch (StateLimitException)
        {
            overLimitRefused = true;
        }

        Check(ref failures, "state-key-limit", overLimitRefused, "超过单键上限即拒绝");
        return failures;
    }
}
