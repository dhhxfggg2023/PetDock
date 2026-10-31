// 搬运工的入口（2026-10-31）。真正的逻辑在 Applier.cs —— 这个进程没有控制台，
// 出什么事都只能写进 _update/helper.log 与 result.txt，所以入口本身必须短到不会出错。
namespace PetDock.Update;

internal static class Program
{
    /// <summary>0 = 装好（含「装好了但没能自己开起来」）；1 = 没装成；2 = 参数不合法。</summary>
    private static int Main(string[] args)
    {
        Options? options = Options.Parse(args);
        return options is null ? 2 : Applier.Run(options);
    }
}
