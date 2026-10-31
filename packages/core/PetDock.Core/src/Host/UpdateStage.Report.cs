// 「装好了，但没能自己把它开回来」这一步留下的痕迹（2026-10-31 遗留项）。
//
// result.txt 只回答「装没装上」，它与「有没有自己开起来」是两件事：把后者塞进那一行，
// 用户会看到「已更新到 0.4.2 relaunch-failed」这种半句话。所以单独一份文件：
// 搬运工写、宿主下一次启动读一次就删，并与 result.txt 一起收走。
//
// 为什么这件事必须落到用户眼前：早先它只写 helper.log —— 而用户不会去看日志。
// 他看到的只是「点了一次更新、桌宠消失了」，隔一会儿自己又把它点开，于是「刚才为什么没了」
// 永远没有答案。这一行就是那个答案。
using System.IO;

namespace PetDock.Hosting;

internal static partial class UpdateStage
{
    /// <summary>搬运工「装好了但没能开起来」的原因码（见 <see cref="UpdateResult.AppliedNotRestarted"/>）。</summary>
    internal const string RelaunchFile = "relaunch-failed.txt";

    internal static string RelaunchPath(string dataRoot) => Path.Combine(Dir(dataRoot), RelaunchFile);
}
