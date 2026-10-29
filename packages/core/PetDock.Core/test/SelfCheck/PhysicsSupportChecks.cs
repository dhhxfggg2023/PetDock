// 抛掷落点的判据（2026-10-29）：命令形态 `--uitest`，判据行 `ui-physics-support-*`。
//
// 只能验几何：真实的「站上标题栏」要人看。但「这次该落在哪个面、什么时候不该落」是纯函数，
// 恰好是这一层最容易错的地方（把路过当站上去、从下方被吸上去、蹭到 8 像素就停住）。
using System;
using System.Collections.Generic;
using System.Drawing;
using PetDock.Hosting;

namespace PetDock.Core.Compositor;

internal static class PhysicsSupportChecks
{
    internal delegate void Report(string name, bool ok, string detail);

    private static readonly Rectangle Area = new(0, 0, 1920, 1080);

    public static void Run(Report check)
    {
        try
        {
            Find(check);
            SkipSmallOverlap(check);
            SkipInside(check);
            SkipTopEdge(check);
            HasSupport(check);
            Topology(check);
        }
        catch (Exception error)
        {
            check("ui-physics-support-crash", false, error.GetType().Name + ": " + error.Message);
        }
    }

    /// <summary>拓扑本身：枚举不抛、缓存生效、筛完的矩形都够大（小窗不当落脚点）。</summary>
    private static void Topology(Report check)
    {
        DesktopTopology topology = DesktopTopology.Shared;
        _ = topology.Surfaces();
        int first = topology.Samples;
        IReadOnlyList<Rectangle> again = topology.Surfaces();
        bool cached = topology.Samples == first;
        bool sane = true;
        foreach (Rectangle rect in again)
        {
            if (rect.Width < 80 || rect.Height < 40)
            {
                sane = false;
                break;
            }
        }

        check("ui-topology-cache", cached, $"samples={topology.Samples} (250ms 内不重复枚举)");
        check("ui-topology-sane", sane, $"surfaces={again.Count}");
    }
    private static IReadOnlyList<Rectangle> Surfaces() => new[]
    {
        new Rectangle(100, 300, 600, 400),
        new Rectangle(800, 200, 500, 300),
    };

    private static void Find(Report check)
    {
        var pet = new Rectangle(300, 200, 128, 100);
        bool found = PhysicsSupport.TryFind(pet, previousBottom: 290, newBottom: 310, Area, Surfaces(), out int top);
        check(
            "ui-physics-support-find",
            found && top == 300,
            $"found={found} top={top} (应落在第一块窗口的顶边 300)");
    }

    private static void SkipSmallOverlap(Report check)
    {
        // 水平只压住 20 像素（< 128/3）：蹭过去不算站上去。
        var pet = new Rectangle(680, 200, 128, 100);
        bool found = PhysicsSupport.TryFind(pet, previousBottom: 290, newBottom: 310, Area, Surfaces(), out int top);
        check("ui-physics-support-skip-narrow", !found, $"found={found} top={top} (重叠 20px 不该停住)");
    }

    private static void SkipInside(Report check)
    {
        // 上一帧已经在面里（从侧面撞上去）：不能被吸到顶边上。
        var pet = new Rectangle(300, 300, 128, 100);
        bool found = PhysicsSupport.TryFind(pet, previousBottom: 360, newBottom: 400, Area, Surfaces(), out int top);
        check("ui-physics-support-skip-inside", !found, $"found={found} top={top}");
    }

    private static void SkipTopEdge(Report check)
    {
        // 贴着屏幕上沿的「面」没有落差，不能当落点（否则宠物会「落」到屏幕外）。
        IReadOnlyList<Rectangle> surfaces = new[] { new Rectangle(200, 0, 800, 200) };
        var pet = new Rectangle(300, 10, 128, 100);
        bool found = PhysicsSupport.TryFind(pet, previousBottom: 0, newBottom: 20, Area, surfaces, out int top);
        check("ui-physics-support-skip-top", !found, $"found={found} top={top}");
    }

    private static void HasSupport(Report check)
    {
        IReadOnlyList<Rectangle> surfaces = Surfaces();
        var onEdge = new Rectangle(300, 200, 128, 100);   // bottom = 300，正好在第一块上
        var offEdge = new Rectangle(1600, 200, 128, 100); // 右边没有面
        var shifted = new Rectangle(300, 210, 128, 100);  // 底边 310 ≠ 300（已经被挪走）

        bool has = PhysicsSupport.HasSupport(onEdge, 300, surfaces);
        bool lost = PhysicsSupport.HasSupport(offEdge, 300, surfaces);
        bool moved = PhysicsSupport.HasSupport(shifted, 300, surfaces);
        check(
            "ui-physics-support-still-there",
            has && !lost && !moved,
            $"has={has} offEdge={lost} moved={moved}");

        check(
            "ui-physics-support-none",
            !PhysicsSupport.HasSupport(onEdge, 300, Array.Empty<Rectangle>()),
            "没有拓扑 = 没有支撑（窗口关掉之后必须掉下去）");
    }
}
