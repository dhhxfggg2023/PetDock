// --golden-baseline <outdir>：把示例宠物（fixtures/sample-pet，D-13a）三个 clip 的首帧
// 渲染成基准 PNG（frame-000.png），供 tests/PetDock.Golden.Tests 的逐像素比对用。
//
// 为什么不进测试工程（vstest testhost）渲染：testhost.exe 是 GUI 子系统进程，要对它的父
// （vstest.console）注册退出回调；本机 vstest.console 跑在 DSH harness 的 Session-0 无桌面
// 通道里，testhost 对它的 OpenProcess 被 ACL 拒（Win32 5），测试宿主起不来。core 进程是真
// 桌面会话，这条渲染链与 --animtest 完全相同（同一 roster / BuildInstance / QueryFrame(0)），
// 由它产出确定性基准。逐像素比对与三态判定仍在金图工程里做（见该工程的注释）。
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using PetDock.Core.PetRuntime;
using PetDock.Hosting;

namespace PetDock.Hosting;

internal static class GoldenBaselineGen
{
    public static int Run(string outdir)
    {
        PetSurfaceRoster roster = PetSurfaceRoster.Discover(PluginHost.ProbePluginsRoot());
        if (!roster.HasView)
        {
            Console.Error.WriteLine("GOLDEN-FAIL no-view-implementation-in-roster");
            return 1;
        }

        using PetInstance? instance = roster.BuildInstance("animtest");
        if (instance is null)
        {
            Console.Error.WriteLine("GOLDEN-FAIL build-instance-returned-null");
            return 1;
        }

        instance.AttachAndStart(128, 128, 1.0);
        instance.Start();

        foreach (string clip in new[] { "idle", "walk", "hover" })
        {
            if (!instance.View.SetAnimation(clip, restart: true))
            {
                Console.Error.WriteLine($"GOLDEN-FAIL set-animation clip={clip} not-accepted");
                return 1;
            }

            Frame frame = instance.QueryFrame(0);
            string clipDir = Path.Combine(outdir, clip);
            Directory.CreateDirectory(clipDir);
            string png = Path.Combine(clipDir, "frame-000.png");
            frame.BitmapHandle.Bitmap.Save(png, ImageFormat.Png);
            Console.WriteLine($"GOLDEN-WROTE clip={clip} {png}");
        }

        instance.View.Detach();
        Console.WriteLine("GOLDEN-OK");
        return 0;
    }
}
