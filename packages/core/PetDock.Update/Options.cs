// 搬運工的命令行（2026-10-31）。
//
// 参数必须写死成这几个名字，并且**每一个都要有**：这个进程跑在「宿主已经退出」的那一刻，
// 用户没有第二次机会去改一个拼错的参数。缺一个就返回 null，由调用方退出 —— 宁可这一次不更新，
// 也不要在一个猜出来的目录上覆盖文件。
//
// 相对路径一律拒绝（`--install .` 这种写法在 WorkingDirectory 与用户预期不一致时会搬到别处去），
// 所以这里要求两个目录都得是绝对路径，并且真的存在。
using System;
using System.IO;

namespace PetDock.Update;

/// <summary>这一次搬运要做什么：把新版本换上去，或者把上一趟没换完的换回来。</summary>
internal enum ApplyMode
{
    /// <summary>装：校验 zip → 重新解包 → 先备份后覆盖。</summary>
    Apply,

    /// <summary>
    /// 回滚：2026-10-31 审计出来的那条路 —— 上一趟覆盖到一半被打断（断电、蓝屏、被结束），
    /// 盘上是新旧混杂的树。这时唯一安全的动作是用 backup/ 把旧版本换回去。
    /// </summary>
    RollBack,
}

/// <summary>一次搬运需要知道的全部事情。</summary>
internal sealed record Options(
    string DataRoot,
    string InstallRoot,
    int WaitPid,
    string RelaunchArgs,
    ApplyMode Mode)
{
    internal const string ApplyFlag = "--apply";
    internal const string InstallFlag = "--install";
    internal const string WaitFlag = "--wait-pid";
    internal const string RollBackFlag = "--rollback";

    /// <summary>解析命令行。任一项缺失或不合法都返回 null（调用方退出 2）。</summary>
    internal static Options? Parse(string[] args)
    {
        string? dataRoot = Value(args, ApplyFlag);
        string? installRoot = Value(args, InstallFlag);
        string? wait = Value(args, WaitFlag);
        if (dataRoot is null || installRoot is null || wait is null)
        {
            return null;
        }

        if (!int.TryParse(wait, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out int pid) || pid <= 0)
        {
            return null;
        }

        if (!Path.IsPathFullyQualified(dataRoot) || !Path.IsPathFullyQualified(installRoot))
        {
            return null;
        }

        // 安装目录必须真的存在：它是「覆盖到哪里」的唯一依据，不存在就说明调用方算错了。
        if (!Directory.Exists(installRoot))
        {
            return null;
        }

        try
        {
            Directory.CreateDirectory(dataRoot);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }

        ApplyMode mode = Array.IndexOf(args, RollBackFlag) >= 0 ? ApplyMode.RollBack : ApplyMode.Apply;

        // 重启后回到「打开主控面板」：用户点的是更新，不该被丢回一个看不见的托盘。
        return new Options(dataRoot, installRoot, pid, "--open-panel", mode);
    }

    private static string? Value(string[] args, string flag)
    {
        int at = Array.IndexOf(args, flag);
        if (at < 0 || at + 1 >= args.Length)
        {
            return null;
        }

        string value = args[at + 1].Trim('"');
        return value.Length == 0 ? null : value;
    }
}
