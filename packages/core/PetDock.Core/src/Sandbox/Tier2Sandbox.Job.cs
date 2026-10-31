// tier-2 的进程边界接线（Job Object）。
//
// 为什么单独一个 partial 文件：Tier2Sandbox.cs 已经在 D-03c 的 400 行边上，
// 而这段代码属于「客体跑起来之后的生命周期外挂」，与 Start/Call/StopProcess 不是同一件事。
// 语义与边界写在 Tier2Job.cs 文件头：宿主死 → 客体死、客体只许一个进程、内存有上限。
using System.Diagnostics;
using PetDock.Core.Sandbox;

namespace PetDock.Core.Sandbox;

public sealed partial class Tier2Sandbox
{
    private Tier2Job? _job;

    /// <summary>客体是否真的落在作业里（false = 边界没生效，见 Tier2Job 文件头）。</summary>
    public bool JobBound => _job is not null;

    /// <summary>
    /// 把刚启动的客体放进作业。建不起来就照常启动：少一层边界不该让插件整个不可用，
    /// 但必须留下痕迹（审计行），否则「以为有边界」比「没有边界」更危险。
    /// </summary>
    private void BindJob(Process process)
    {
        _job = Tier2Job.Create(Tier2Job.DefaultMemoryLimitBytes, out string? why);
        if (_job is not null && _job.Assign(process))
        {
            return;
        }

        _job?.Dispose();
        _job = null;
        Emit(SandboxEventKind.Audit, ("tier2", "job-unbound"), ("why", why ?? "assign-failed"));
    }

    /// <summary>还回句柄。进程已经停了，这一下只是把内核资源收干净。</summary>
    private void ReleaseJob()
    {
        _job?.Dispose();
        _job = null;
    }

    /// <summary>
    /// 判据钩子：关掉作业句柄 = 模拟「宿主进程被强杀、内核回收句柄」。
    /// 返回 false = 本来就没有作业可关。
    /// </summary>
    internal bool CloseJobForTest()
    {
        if (_job is null)
        {
            return false;
        }

        ReleaseJob();
        return true;
    }
}
