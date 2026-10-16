// t9（docs/41 D-10b 的代码侧）：PetRuntime 纯逻辑行为断言。
// 只走公共面（BitmapRegistry / Frame / InstanceStateMachine / DegradationChain / AnimationClip /
// BrainDirective / PetEvent / PetViewContext / StateLimitException），不触达真实窗口系统、真实 GPU、
// 不做文件系统写入（D-10b）—— 位图一律是进程内内存位图。
// 每条断言下写「什么情况下会红」，失败信息要能自己回答问题。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using PetDock.Core.PetRuntime;
using Xunit;

namespace PetDock.Runtime.Tests;

/// <summary>PetRuntime 公共面的行为断言（不是形状断言 —— 形状在 ContractShapeTests）。</summary>
public sealed class PetRuntimeLogicTests
{
    private const double Eps = 1e-9;

    /// <summary>一张全透明的预乘 32bpp 位图（进程内内存位图，不落盘、不上屏）。</summary>
    private static Bitmap TransparentPremultiplied(int width, int height)
    {
        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        using Graphics g = Graphics.FromImage(bitmap);
        g.Clear(Color.FromArgb(0, 0, 0, 0));
        return bitmap;
    }

    private static Frame FrameOf(BitmapHandle handle, byte[]? hitmap) =>
        new(handle, new PetSize(handle.PixelWidth, handle.PixelHeight), 1.0, null, hitmap);

    [Fact]
    public void BitmapRegistryAcceptsOnlyPremultiplied32bpp()
    {
        using Bitmap good = TransparentPremultiplied(4, 4);
        var registry = new BitmapRegistry();
        BitmapHandle handle = registry.Acquire(good);

        // 红：registry 开始收非预乘位图（docs/11:49）—— 整只宠物会偏色且不报错。
        Assert.Equal(1L, handle.Id);
        Assert.Equal(4, handle.PixelWidth);
        Assert.Equal(16, handle.PixelCount);
        Assert.True(registry.LiveCount == 1, "acquire 之后必须留下一条存活句柄");

        using Bitmap straight = new(4, 4, PixelFormat.Format32bppArgb);
        Assert.Throws<ArgumentException>(() => registry.Acquire(straight));
    }

    [Fact]
    public void BitmapRegistryReleaseIsIdempotentAndCounted()
    {
        using Bitmap a = TransparentPremultiplied(2, 2);
        using Bitmap b = TransparentPremultiplied(2, 2);
        var registry = new BitmapRegistry();
        BitmapHandle ha = registry.Acquire(a);
        BitmapHandle hb = registry.Acquire(b);

        // 红：句柄编号复用或重复 release 被重复计数（会掩盖泄漏）。
        Assert.Equal(2L, hb.Id);
        Assert.True(registry.LiveCount == 2, "两个未释放句柄必须都在");

        registry.Release(ha);
        registry.Release(ha);
        Assert.True(registry.LiveCount == 1, "二次 release 必须是无害 no-op");
        Assert.True(registry.Released == 1, "重复 release 不许重复计数");

        registry.Release(null);
        Assert.True(registry.LiveCount == 1, "release(null) 不许动任何句柄");

        registry.ReleaseAll();
        Assert.True(registry.LiveCount == 0, "ReleaseAll 之后不许有悬空句柄");
        Assert.True(registry.Released == 2, "ReleaseAll 只释放仍存活的 hb（ha 已释放过一次）");
        Assert.True(registry.Acquired == 2, "Acquired 只随 Acquire 增长");
    }

    [Fact]
    public void BitmapRegistryRejectsNullBitmap()
    {
        var registry = new BitmapRegistry();

        // 红：null 位图被静默接受（之后每一帧都会 NRE）。
        Assert.Throws<ArgumentNullException>(() => registry.Acquire(null!));
    }

    [Fact]
    public void FrameAlphaZeroAlwaysPassesThrough()
    {
        using Bitmap bitmap = TransparentPremultiplied(4, 4);
        var registry = new BitmapRegistry();
        var hitmap = new byte[16];
        for (int i = 0; i < hitmap.Length; i++)
        {
            hitmap[i] = 1;
        }

        Frame frame = FrameOf(registry.Acquire(bitmap), hitmap);

        // 红：alpha=0 的像素变成可命中 —— 整窗透明区会被 surface 变成可点（docs/11:190）。
        Assert.False(frame.HitsAt(0, 0), "hitmap 全 1 也不得让 alpha=0 的像素可命中");
        Assert.False(frame.HitsAt(3, 3), "hitmap 全 1 也不得让 alpha=0 的像素可命中");
        Assert.Equal(0, frame.AlphaAt(0, 0));
    }

    [Fact]
    public void FrameHitmapDecidesOnlyWhenAlphaIsNonZero()
    {
        using Bitmap bitmap = TransparentPremultiplied(4, 4);
        bitmap.SetPixel(1, 1, Color.FromArgb(255, 255, 0, 0));
        var registry = new BitmapRegistry();
        BitmapHandle handle = registry.Acquire(bitmap);

        var noHit = new byte[16];
        var hit = new byte[16];
        hit[(1 * 4) + 1] = 1;

        Frame blocked = FrameOf(handle, noHit);
        Frame allowed = FrameOf(handle, hit);
        Frame byAlpha = FrameOf(handle, null);

        // 红：hitmap 被忽略（漏判命中）或 hitmap 缺失时不再退回 alpha 通道。
        Assert.True(blocked.AlphaAt(1, 1) == 255, "预乘位图的 alpha 必须读得到");
        Assert.False(blocked.HitsAt(1, 1), "hitmap=0 的像素必须放行");
        Assert.True(allowed.HitsAt(1, 1), "hitmap=1 且 alpha 非零必须可命中");
        Assert.True(byAlpha.HitsAt(1, 1), "hitmap 缺失时退回读 alpha 通道");
        Assert.False(byAlpha.HitsAt(0, 0), "alpha=0 永远放行穿透（docs/11:190）");
    }

    [Fact]
    public void FrameHitmapShorterThanPixelCountThrows()
    {
        using Bitmap bitmap = TransparentPremultiplied(4, 4);
        bitmap.SetPixel(3, 3, Color.FromArgb(255, 255, 0, 0));
        var registry = new BitmapRegistry();
        Frame frame = FrameOf(registry.Acquire(bitmap), new byte[2]);

        // 红：长度不对的命中图被静默当命中 —— 那会把整窗变成可点。
        Assert.Throws<ArgumentException>(() => frame.HitsAt(3, 3));
    }

    [Fact]
    public void FrameOutOfBoundsIsPassThrough()
    {
        using Bitmap bitmap = TransparentPremultiplied(4, 4);
        var registry = new BitmapRegistry();
        Frame frame = FrameOf(registry.Acquire(bitmap), null);

        // 红：帧外坐标被算成命中（窗口外点击被宠物吃掉）。
        Assert.False(frame.HitsAt(-1, 0), "负坐标必须穿透");
        Assert.False(frame.HitsAt(4, 0), "右边界外必须穿透");
        Assert.False(frame.HitsAt(0, 4), "下边界外必须穿透");
        Assert.Equal((byte)0, frame.AlphaAt(9, 9));
    }

    [Fact]
    public void InstanceStateMachineEdgesMatchDocs11()
    {
        // 红：转移表被改动 —— docs/11 §10 的箭头集合是冻结的。
        Assert.Equal(
            "Discovered->Imported Discovered->Disposed Imported->Instantiated Imported->Disposed "
            + "Instantiated->Running Instantiated->Disposed Running->Suspended Running->Disposed "
            + "Suspended->Running Suspended->Disposed",
            InstanceStateMachine.Describe());
        Assert.True(InstanceStateMachine.Order.Count == 6, "docs/11 §10 的六阶段顺序");
    }

    [Fact]
    public void InstanceStateMachineRejectsBackwardAndPostDisposeTransitions()
    {
        Assert.True(
            InstanceStateMachine.CanTransition(InstanceState.Running, InstanceState.Suspended),
            "running ⇄ suspended 必须双向合法");
        Assert.False(
            InstanceStateMachine.CanTransition(InstanceState.Suspended, InstanceState.Instantiated),
            "回退必须被拒");

        // 红：乱序转移被放行 —— 会变成一只安静不动的宠物。
        Assert.False(InstanceStateMachine.CanTransition(InstanceState.Disposed, InstanceState.Running));
        Assert.False(InstanceStateMachine.CanTransition(InstanceState.Discovered, InstanceState.Running));

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => InstanceStateMachine.Require(InstanceState.Disposed, InstanceState.Running, "t9"));
        Assert.Contains("docs/11 §10", error.Message);
    }

    [Fact]
    public void DegradationChainStartsAtRealWithOneStep()
    {
        var chain = new DegradationChain(7);

        // 红：起点不再是 Real，或起点没留痕迹（用户可观察面就断了，docs/11:264）。
        Assert.Equal(DisplayTier.Real, chain.Tier);
        Assert.True(chain.Steps.Count == 1, "起点必须留下一条 start 痕迹");
        Assert.Equal("start", chain.Steps[0].Reason);
        Assert.Equal(7L, chain.Steps[0].AtMs);
        Assert.True(chain.Refused.Count == 0, "起点不许有被拒绝的记录");
    }

    [Fact]
    public void DegradationChainMovesForwardOnly()
    {
        var chain = new DegradationChain();

        // 红：出现逆序降级（docs/11:264 逐字「不允许逆序进行」）。
        Assert.True(chain.Advance(DisplayTier.ReducedRate, "slow", 10), "向前降级必须成功");
        Assert.True(chain.Steps.Count == 2, "向前降级必须留一条痕迹");
        Assert.True(chain.Advance(DisplayTier.ReducedRate, "same", 11), "同档必须是无害 no-op");
        Assert.True(chain.Steps.Count == 2, "同档不得新增痕迹");
        Assert.True(chain.Advance(DisplayTier.Placeholder, "budget", 12), "再次向前降级必须成功");
        Assert.True(chain.Steps.Count == 3, "只有真正向前的降级才留痕迹");
        Assert.False(chain.Advance(DisplayTier.ReducedRate, "recover", 13), "回退必须被拒绝");
        Assert.Equal(DisplayTier.Placeholder, chain.Tier);
        Assert.True(chain.Refused.Count == 1, "被拒绝的回退必须留证据");
        Assert.True(chain.IsForwardOnly(), "链条必须只向前");
        Assert.Contains("forward-only=true", chain.Describe());
        Assert.Contains("refused-backward=1", chain.Describe());
    }

    [Fact]
    public void DegradationChainNotifiesOnlyRealForwardSteps()
    {
        var chain = new DegradationChain();
        var seen = new List<DegradationStep>();
        chain.Changed += seen.Add;

        chain.Advance(DisplayTier.ReducedRate, "a", 1);
        chain.Advance(DisplayTier.ReducedRate, "b", 2);
        chain.Advance(DisplayTier.Real, "c", 3);

        // 红：同档/回退也发通知 —— 用户可观察面会被噪声淹没。
        Assert.True(seen.Count == 1, "只有真正向前的降级才发 Changed");
        Assert.Equal(DisplayTier.ReducedRate, seen[0].Tier);
        Assert.Equal("a", seen[0].Reason);
    }

    private static bool Equal(double a, double b) => Math.Abs(a - b) < Eps;

    [Fact]
    public void AnimationClipPeriodMsFollowsFramesAndFps()
    {
        // 红：周期公式被改 —— 动画循环判据（docs/11 §7 / t3 的 --animtest）会整体失真。
        Assert.True(
            Equal(new AnimationClip("idle", 4, 6, ClipLoop.Loop).PeriodMs, 4000.0 / 6),
            "4 帧 6fps 的周期必须是 666.667ms");
        Assert.True(
            Equal(new AnimationClip("walk", 4, 8, ClipLoop.Loop).PeriodMs, 500.0),
            "4 帧 8fps 的周期必须是 500ms");
        Assert.True(
            Equal(new AnimationClip("hover", 2, 6, ClipLoop.Loop).PeriodMs, 2000.0 / 6),
            "2 帧 6fps 的周期必须是 333.333ms");
        Assert.True(
            Equal(new AnimationClip("broken", 4, 0, ClipLoop.Loop).PeriodMs, 0.0),
            "fps <= 0 时周期必须是 0");
        Assert.Contains("frames=4", new AnimationClip("idle", 4, 6, ClipLoop.Loop).ToString());
    }

    [Fact]
    public void BrainDirectiveFactoriesFillOnlyTheirOwnSlots()
    {
        BrainDirective anim = BrainDirective.SetAnimation("walk", true);

        // 红：某个工厂开始借用别人的槽位 —— 仲裁与分发都会读错指令。
        Assert.Equal(DirectiveKind.SetAnimation, anim.Kind);
        Assert.Equal("walk", anim.Clip);
        Assert.True(anim.Restart, "restart 必须透传");
        Assert.True(anim.Options is null, "未给 options 时不得凭空造一个");
        Assert.True(anim.At is null, "setAnimation 不得填 moveTo 的槽");
        Assert.True(anim.Reason is null, "setAnimation 不得填 suspend 的槽");
        Assert.True(anim.Event is null && anim.Capability is null && anim.Justification is null, "不许借用别的槽位");

        BrainDirective move = BrainDirective.MoveTo(new PetPoint(3, 4), 2.5);
        Assert.Equal(DirectiveKind.MoveTo, move.Kind);
        Assert.True(move.At is not null, "moveTo 必须带目标点");
        Assert.True(
            Equal(move.At!.Value.X, 3.0) && Equal(move.At.Value.Y, 4.0),
            "moveTo 的目标点必须原样透传");
        Assert.True(Equal(move.Speed!.Value, 2.5), "speed 必须透传");

        // 红：null 原因变成 NRE（宿主会在挂起路径上崩），或 requestCapability 不带理由。
        Assert.Equal(string.Empty, BrainDirective.Suspend(null!).Reason);
        Assert.Equal(DirectiveKind.Resume, BrainDirective.Resume("wake").Kind);
        Assert.True(BrainDirective.Emit(PetEvent.Named("tick")).Event is not null, "emit 必须带事件");
        Assert.Equal(
            "input.mousePosition",
            BrainDirective.RequestCapability("input.mousePosition", "need cursor").Capability);
        Assert.Equal(
            "need cursor",
            BrainDirective.RequestCapability("input.mousePosition", "need cursor").Justification);
        Assert.Throws<ArgumentNullException>(() => BrainDirective.RequestCapability(null!, "why"));
    }

    [Fact]
    public void DirectiveKindOrderIsTheDocs11ArbitrationOrder()
    {
        // 红：枚举被重新编号 —— docs/11:162 的仲裁顺序（suspend > setAnimation > moveTo > emit >
        // requestCapability）就悄悄换了，而仲裁器与 Brain 两边都靠这个数值。
        Assert.True((int)DirectiveKind.Suspend > (int)DirectiveKind.Resume, "Suspend 必须最优先");
        Assert.True((int)DirectiveKind.Resume > (int)DirectiveKind.SetAnimation);
        Assert.True((int)DirectiveKind.SetAnimation > (int)DirectiveKind.MoveTo);
        Assert.True((int)DirectiveKind.MoveTo > (int)DirectiveKind.Emit);
        Assert.True((int)DirectiveKind.Emit > (int)DirectiveKind.RequestCapability, "RequestCapability 必须最低");
    }

    [Fact]
    public void PetRuntimeValueTypesKeepTheirDocumentedShape()
    {
        // 红：格式化开始依赖本机区域设置（自检输出不可复现），或上下文构造不再归一化 null。
        Assert.Equal("1.5", PetPoint.Fmt(1.5));
        Assert.Equal("2x3", new PetSize(2, 3).ToString());
        Assert.Equal("(1,2)", new PetPoint(1, 2).ToString());

        PetColor opaque = PetColor.Opaque(1, 2, 3);
        Assert.True(opaque.A == 255, "Opaque 必须补 a=255");
        Assert.True(opaque.R == 1 && opaque.G == 2 && opaque.B == 3);        WorldSnapshot empty = WorldSnapshot.Empty(new PetRect(0, 0, 10, 10));
        Assert.True(empty.Cursor is null, "空世界不得注入可信输入");
        Assert.True(empty.TimeOfDayMinutes == 0 && !empty.IsOccluded);

        PetEvent named = PetEvent.Named("host.notice");
        Assert.Equal("host.notice", named.Type);
        Assert.True(named.At > 0, "Named 必须带毫秒时间戳");
        Assert.Equal("host.notice@0", new PetEvent("host.notice", 0).ToString());

        PetViewContext view = new("pet.instance", null!, new ResourceBudget(8.3, 33.3, 128 * 128));
        Assert.True(view.Assets.Count == 0, "assets 为 null 时归一成空列表");
        Assert.Equal("pet.instance", view.PetInstanceId);
        Assert.Contains("frame<8.3ms", view.Budget.ToString());
        Assert.Throws<ArgumentNullException>(
            () => new PetViewContext(null!, Array.Empty<string>(), default));
        Assert.Throws<ArgumentNullException>(() => new BrainContext(null!, null, default));
    }

    [Fact]
    public void StateLimitExceptionCarriesTheDocs11LimitFacts()
    {
        var error = new StateLimitException("pet.mood", 9000, 8192);

        // 红：越限异常不再带 key/字节数/上限 —— 调用方无法回答「到底超了多少」。
        Assert.Equal("pet.mood", error.Key);
        Assert.Equal(9000, error.Bytes);
        Assert.Equal(8192, error.Limit);
        Assert.Contains("pet.mood", error.Message);
        Assert.Contains("8192", error.Message);
    }
}
