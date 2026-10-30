// 宠物导入的宿主编排（RFC-15 §3.2/§3.3/§3.5）。
//
// 为什么编排层单独成类：导入要跨三个属主 —— 数据区（PetDataStore，落盘与清单）、
// 名册（PetSurfaceRoster，出视图实现）、帧源（SwappableFrameSource，换内容源）。
// 这三件事的**顺序**是规格的一部分（RFC-15 §3.3：先建新实例 → 再换源 → 最后释放旧实例），
// 顺序错了就是「桌面上空了」或「旧宠物挂在新素材上」。顺序散在 UI 里就没有一处能改对它。
//
// core 不解析素材格式：这里只做「把这个目录复制进来、登记、换源」。
// 「这个目录里是什么格式」的知识全在视图实现侧（D-03b / 原规格 01 P-02）。
using System;
using System.IO;
using System.Collections.Generic;
using System.Threading;
using PetDock.Core.Compositor;
using PetDock.Core.PetRuntime;
using PetDock.Core.Ui;

namespace PetDock.Hosting;

/// <summary>
/// 「当前宠物被换掉了」的回调（自声明，理由同 HostingTypes.cs 的先例：
/// 系统自带的同名泛型委托含内核黑名单子串）。
/// </summary>
internal delegate void PetReplacedNotify(PetInstance? instance);

/// <summary>
/// 「这只新宠物摆到桌面上」的尝试（RFC-37 §3.5 片 2）。返回 false 表示桌面上放不下
/// （没有集合层、或那一只装不起来）—— 调用方据此退回 RFC-15 的替换语义。
/// 自声明理由同上。
/// </summary>
internal delegate bool PetAdoptNotify(PetInstance instance, string packPath);

/// <summary>导入的一个阶段（值是一个 i18n 键，由宿主翻成给用户看的一句话）。</summary>
internal delegate void ImportStageNotify(string key);

/// <summary>第三方转换的帧进度（Done/Total；Total 为 0 表示导入器没给总量）。</summary>
internal delegate void ImportConvertProgressNotify(int done, int total);

/// <summary>把一段必须落在 UI 线程上的工作交回去（后台线程阻塞等待它做完）。</summary>
internal delegate void ImportUiDispatch(UiCommand step);

/// <summary>一次导入的结果。失败时 <see cref="Error"/> 有值，桌面上的旧宠物原样不动。</summary>
internal sealed record ImportResult(bool Ok, string Message, string? InstanceId = null, string? MessageKey = null)
{
    internal const string AddedKey = "host.import.added";
    internal const string ReplacedKey = "host.import.replaced";

    /// <summary>用户按了取消（或关掉了界面）：不是失败，不显示成错误。</summary>
    public bool WasCancelled { get; init; }

    public static ImportResult Failure(string message) => new(false, message);

    public static ImportResult Cancel(string message) => new(false, message) { WasCancelled = true };
}

/// <summary>
/// 宠物导入的宿主编排。
/// 线程：重活（格式转换、复制、登记）可以在后台线程；建实例、换源、挂到窗口集合
/// 会碰窗口的帧循环，必须经 <c>onUi</c> 回到 UI 线程（UI 线程不等待后台线程，因此不会自锁）。
/// </summary>
internal sealed class PetImportService
{
    private readonly PetDataStore _store;
    private readonly SwappableFrameSource _frames;
    private readonly PetSurfaceRoster _roster;
    private readonly Func<PetInstance?> _currentPet;
    private readonly PetReplacedNotify _onPetReplaced;
    private readonly IReadOnlyList<IPackImporter> _importers;

    public PetImportService(
        PetDataStore store,
        SwappableFrameSource frames,
        PetSurfaceRoster roster,
        Func<PetInstance?> currentPet,
        PetReplacedNotify onPetReplaced,
        IReadOnlyList<IPackImporter>? importers = null)
    {
        _store = store;
        _frames = frames;
        _roster = roster;
        _currentPet = currentPet;
        _onPetReplaced = onPetReplaced;
        _importers = importers ?? Array.Empty<IPackImporter>();
    }

    /// <summary>
    /// 「把这只摆上桌」的尝试（RFC-37 §3.5 片 2）。空 = 没有集合层（自检、单窗装配），
    /// 那时导入就是 RFC-15 的替换语义。由 UiHost 在构造之后接上（集合是后来才挂上去的）。
    /// </summary>
    public PetAdoptNotify? AdoptExtra { get; set; }

    /// <summary>
    /// 导入一个用户选中的目录。
    /// 顺序（RFC-15 §3.3，不可换）：
    ///   ① 复制到数据区 → ② 登记清单 → ③ 用新素材根重建实例并初始化 → ④ 换内容源 → ⑤ 释放旧实例。
    /// 任何一步失败都**不换源**：旧宠物继续在桌面上，错误信息交给界面（内联错误条）。
    ///
    /// **片 2 的例外（RFC-37 §3.5）**：装了窗口集合时，第 ④ 步换成「把新的那只摆上桌」——
    /// 用户导入第二只宠物是想要两只，不是想要换掉第一只。摆不进去（没有集合层）才退回替换。
    /// </summary>
    public ImportResult Import(
        string sourceDir,
        CancellationToken token = default,
        ImportStageNotify? stage = null,
        ImportUiDispatch? onUi = null,
        CopyProgressNotify? progress = null,
        ImportConvertProgressNotify? convertProgress = null)
    {
        // 没给调度器 = 调用方本来就在 UI 线程上（自检、旧装配）：直接执行。
        onUi ??= step => step();
        string stagedPath = string.Empty;
        string conversionPath = string.Empty;
        PetDataStore.ActivePack? previousActive = null;
        try
        {
            token.ThrowIfCancellationRequested();
            // 显示名取用户选的那个目录名（原始中文保留）：磁盘上的目录名会被 Sanitize 成 ASCII，
            // 不能拿它当名字，否则「守岸人」会变成「-----」，用户认不出自己刚导入的是哪只。
            string displayName = DisplayNameOf(sourceDir);
            stage?.Invoke("host.import.stage.convert");

            // 第三方格式先由插件转成标准包。转换失败就按导入失败回滚。
            string stagedSource = sourceDir;
            foreach (IPackImporter importer in _importers)
            {
                if (!importer.CanImport(sourceDir))
                {
                    continue;
                }

                conversionPath = _store.CreateStagingDirectory("convert");
                if (importer is IPackImporterProgress withProgress)
                {
                    withProgress.Import(
                        sourceDir,
                        conversionPath,
                        p => convertProgress?.Invoke(p.Done, p.Total),
                        token);
                }
                else
                {
                    importer.Import(sourceDir, conversionPath);
                }

                stagedSource = conversionPath;
                KernelLog.Write($"import-converted importer={importer.Id} source={sourceDir}");
                break;
            }

            token.ThrowIfCancellationRequested();
            stage?.Invoke("host.import.stage.copy");

            // 追加时必须把「当前选中」指回来（登记会把它指向新来的那只），所以先记下原值。
            previousActive = _store.ReadActive();
            (string instanceId, string folder, string path) = _store.StagePack(stagedSource, token, progress);
            stagedPath = path;

            token.ThrowIfCancellationRequested();
            stage?.Invoke("host.import.stage.register");

            // ② 登记：清单写失败 ⇒ 素材拷贝成了孤儿，回滚（不留半个状态）。
            _store.RegisterActive(instanceId, folder, displayName);

            token.ThrowIfCancellationRequested();
            stage?.Invoke("host.import.stage.start");

            // ③ 重建实例：素材根按 RFC-15 §3.2 第 3 跳显式传给视图实现。
            PetInstance? next = null;
            onUi(() =>
            {
                next = _roster.BuildInstance(
                    "import", new[] { path }, freshView: true, imported: true, displayName: displayName);
                if (next is null)
                {
                    throw new InvalidOperationException(I18n.Get("host.import.err.no-view"));
                }

                // 初始化在这里做（init/describe/attach）：视图实现不接受这个素材包时，
                // 异常会在换源**之前**抛出 —— 旧宠物因此毫发无损（RFC-15 §3.5 倒数第二行）。
                next.AttachAndStart(128, 128, 1.0);
                next.Start();
            });

            token.ThrowIfCancellationRequested();

            // ④ 片 2：桌面上已经有位置就摆上去，主窗那只不动（active 也指回原来那只）。
            PetAdoptNotify? adopt = AdoptExtra;
            bool adopted = false;
            if (adopt is not null)
            {
                onUi(() => adopted = adopt(next!, path));
            }

            if (adopted)
            {
                _store.SetActive(previousActive?.InstanceId);
                KernelLog.Write($"import-added instance={next!.Id} folder={folder}"
                    + $" main={previousActive?.InstanceId ?? "(none)"}");
                return new ImportResult(true, I18n.Get(ImportResult.AddedKey), next.Id, ImportResult.AddedKey);
            }

            // ④ 换源：代理切到新宠物。下一帧生效（代理不缓存帧）。
            onUi(() =>
            {
                PetInstance? previous = _currentPet();
                _frames.Replace(new PetFrameSource(next!));
                _onPetReplaced(next);

                // ⑤ 释放旧实例：换源之后才做，先释放会让切换窗口里露出空白帧。
                ReleaseOld(previous, next!);
            });

            KernelLog.Write($"import-ok instance={next!.Id} folder={folder}");
            return new ImportResult(true, I18n.Get(ImportResult.ReplacedKey), next.Id, ImportResult.ReplacedKey);
        }
        catch (OperationCanceledException)
        {
            // 用户取消：复制一半的素材要扔掉；清单登记留在原样（下一次导入会再写）。
            Rollback(stagedPath);
            KernelLog.Write($"import-cancelled source={sourceDir}");
            return ImportResult.Cancel(I18n.Get("cp.pets.import.cancelled"));
        }
        catch (Exception error)
        {
            // 失败要回滚：复制进去的素材拷贝 + 清单登记都得撤掉。
            // 撤不掉的部分如实记日志，不假装干净。
            Rollback(stagedPath);
            string message = $"{error.GetType().Name}: {error.Message}";
            KernelLog.Write($"import-failed {message}");
            return ImportResult.Failure(Describe(error));
        }
        finally
        {
            if (conversionPath.Length > 0)
            {
                _store.Discard(conversionPath);
            }
        }
    }

    /// <summary>
    /// 启动时按清单恢复上次选中的宠物（清单里有、素材目录还在时才恢复）。
    /// 恢复不了就静默退回默认宠物 —— 启动期的失败不该让程序起不来。
    /// </summary>
    public PetInstance? RestoreActive() => RestoreFromCatalog(_store, _roster);

    /// <summary>
    /// 按清单恢复上次选中的宠物（静态形态：装配期用得上，那时还没有服务实例）。
    /// 恢复不了就返回 null —— 调用方据此保留默认宠物。
    /// </summary>
    public static PetInstance? RestoreFromCatalog(PetDataStore store, PetSurfaceRoster roster)
    {
        PetDataStore.ActivePack? active = store.ReadActive();
        if (active is null)
        {
            return null;
        }

        try
        {
            PetInstance? instance = roster.BuildInstance(
                "import", new[] { active.Path }, freshView: true, imported: true, displayName: active.Name);
            if (instance is null)
            {
                KernelLog.Write($"import-restore-miss instance={active.InstanceId} reason=no-view");
                return null;
            }

            instance.AttachAndStart(128, 128, 1.0);
            instance.Start();
            KernelLog.Write($"import-restore-ok instance={instance.Id} path={active.Path}");
            return instance;
        }
        catch (Exception error)
        {
            KernelLog.Write($"import-restore-failed {error.GetType().Name}: {error.Message}");
            return null;
        }
    }

    /// <summary>
    /// 释放一个不再被使用的实例（旧宠物已经不在桌面上了，但它的资源要还回去）。
    /// 公开：装配期恢复上次导入的宠物时，默认那只也要这样释放。
    /// </summary>
    public static void ReleaseInstance(PetInstance? previous, PetInstance? keep)
    {
        if (previous is null || ReferenceEquals(previous, keep))
        {
            return;
        }

        try
        {
            previous.Dispose();
        }
        catch (Exception error)
        {
            KernelLog.Write($"import-release-failed {error.GetType().Name}: {error.Message}");
        }
    }

    /// <summary>释放旧实例（旧宠物已经不在桌面上了，但它的资源要还回去）。</summary>
    private static void ReleaseOld(PetInstance? previous, PetInstance next)
    {
        if (previous is null || ReferenceEquals(previous, next))
        {
            return;
        }

        try
        {
            previous.Dispose();
        }
        catch (Exception error)
        {
            KernelLog.Write($"import-release-failed {error.GetType().Name}: {error.Message}");
        }
    }

    /// <summary>用户选中的目录叫什么（去尾分隔符；空名回落到一句可读的默认名）。</summary>
    private static string DisplayNameOf(string sourceDir)
    {
        string trimmed = (sourceDir ?? string.Empty)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string name = Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(name) ? I18n.Get("cp.pets.name.pack") : name;
    }

    /// <summary>回滚一次失败的导入（删掉刚复制进去的目录）。</summary>
    private void Rollback(string stagedPath)
    {
        if (string.IsNullOrEmpty(stagedPath))
        {
            return;
        }

        _store.Discard(stagedPath);
    }

    /// <summary>
    /// 失败原因的人类可读说法（RFC-15 §3.5：文案要让用户知道下一步怎么办）。
    /// 不弹模态框 —— 失败信息只出现在宠物页的内联错误条里。
    /// </summary>
    private static string Describe(Exception error) => error switch
    {
        DirectoryNotFoundException => I18n.Get("host.import.err.dir-missing"),
        FileNotFoundException => I18n.Get("host.import.err.dir-missing"),
        UnauthorizedAccessException => I18n.Get("host.import.err.copy-denied"),
        IOException => I18n.Get("host.import.err.copy-failed"),
        _ => I18n.Get("host.import.err.failed"),
    };
}
