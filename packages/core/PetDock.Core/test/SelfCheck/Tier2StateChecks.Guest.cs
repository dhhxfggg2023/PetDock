// tier-2 客体桩件：本进程自己扮演子进程，判据在 Tier2StateChecks.cs。
//
// 为什么用产品自己的 exe 当客体：自检代码就编在 PetDock.Core.exe 里（test/SelfCheck 不过 D-03a 计数），
// 所以「测试自体扮演子进程」在这里正好是同一个 exe 的另一个入口——不新增项目、不依赖外部运行时。
// src/Program.cs 在建窗口与 DPI 之前把它分流到这里，所以本模式不碰任何 UI。
using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;

namespace PetDock.Hosting;

internal static class Tier2Guest
{
    /// <summary>客体模式旗标（src/Program.cs 最先分流它）。</summary>
    public const string Flag = "--tier2-guest";

    /// <summary>每帧故意慢过默认渲染预算（8ms）、但远快于请求等待上限的客体。</summary>
    public const string SlowMode = "slow";

    /// <summary>应答一次 `probe` 就自杀的客体（RFC-33 的崩溃落地判据用它）。</summary>
    public const string SuicideMode = "suicide";

    /// <summary>慢客体的每帧延迟：比 8ms 的渲染预算慢，比 2000ms 的等待上限快。</summary>
    public const int SlowFrameMs = 20;

    private const int Side = 4;

    public static int Run(string mode)
    {
        // stdin 是重定向管道时同步 ReadLine 会永久挂起（原规格 50 的既有实录），一律走异步。
        using var stdin = new StreamReader(Console.OpenStandardInput());
        long frameId = 0;
        string? line;
        while ((line = stdin.ReadLineAsync().GetAwaiter().GetResult()) is not null)
        {
            JsonObject? request;
            try
            {
                request = JsonNode.Parse(line) as JsonObject;
            }
            catch (System.Text.Json.JsonException)
            {
                continue; // 半行/坏包：不出声，等下一行
            }

            if (request?["method"] is not JsonNode methodNode || request["id"] is not JsonNode idNode)
            {
                continue; // 通知与残包不应答
            }

            string method = methodNode.GetValue<string>();
            string id = idNode.GetValue<string>();
            if (method == "shutdown")
            {
                return 0;
            }

            if (mode == SuicideMode && method == "probe")
            {
                Write(id, "ok");
                Console.Out.Flush();
                Environment.Exit(7); // 应答后自杀：宿主的下一次 Call 必然看到进程没了
            }

            if (method == "abi.hello")
            {
                Write(id, new JsonObject
                {
                    ["id"] = "petdock-selfcheck-guest",
                    ["version"] = "1.0.0",
                    ["requiresHitmap"] = false,
                    ["preferredFps"] = 60,
                    ["capabilities"] = new JsonArray(),
                });
            }
            else if (method == "abi.frame")
            {
                if (mode == SlowMode)
                {
                    Thread.Sleep(SlowFrameMs);
                }

                frameId++;
                Write(id, new JsonObject
                {
                    ["frameId"] = frameId,
                    ["size"] = new JsonObject { ["w"] = Side, ["h"] = Side },
                    ["scale"] = 4.0,
                    ["bitmap"] = Convert.ToBase64String(Blank(Side * Side * 4)),
                    ["hitmap"] = Convert.ToBase64String(Hits(Side * Side)),
                });
            }
            else
            {
                Write(id, "ok");
            }

            Console.Out.Flush();
        }

        return 0;
    }

    /// <summary>全透明像素：判据只关心「帧读数在推进」，不关心长相。</summary>
    private static byte[] Blank(int length)
    {
        return new byte[length];
    }

    private static byte[] Hits(int length)
    {
        byte[] map = new byte[length];
        for (int i = 0; i < map.Length; i++)
        {
            map[i] = 1;
        }

        return map;
    }

    private static void Write(string id, JsonNode result)
    {
        var reply = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["result"] = result,
        };
        Console.Out.WriteLine(reply.ToJsonString());
    }
}
