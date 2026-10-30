// 宠物的桌面归属（2026-10-30）：收起 / 放回 / 记住收起状态。
//
// 为什么单独一个文件：UiHost.Tray.cs 已经贴着 D-03c 的 400 行上限，而这一块与「托盘命令」
// 也不是同一件事 —— 它是「这一只在不在桌面上」这个状态的唯一属主（托盘菜单与宠物页都走它）。
using System;
using System.Collections.Generic;
using System.IO;
using PetDock.Core.Compositor;
using PetDock.Hosting;

namespace PetDock.Core.Ui;

internal sealed partial class UiHost
{
    /// <summary>
    /// 把这一只从桌面上**收起来**：不删素材、不删清单条目（2026-10-30 语义分离）。
    ///
    /// 旧实现把「从桌面移除」与「删除」做成了同一个动作（都调 RemoveFromCatalog），
    /// 于是用户收起一只之后它就同时从宠物页消失了 —— 没有地方看到它，也没有入口把它放回来，
    /// 只能重新导入一遍（而素材一直都在）。用户报的「已导入的宠物没地方看，不知道怎么打开」
    /// 说的就是这个。现在：收起 = 停泵 + 藏窗 + 记住「它被收起了」；删除才是真删（宠物页那一个）。
    ///
    /// 主窗那只的**实例**归 Program / CAS（RFC-15 §3.2），本片不在这里释放它 ——
    /// 只藏起窗口并把收起状态落盘，真正的卸载在重启时发生。
    /// </summary>
    private void RemovePetById(string id)
    {
        if (IsPrimary(id))
        {
            string primaryId = PrimaryPetIdOf();
            _primaryRemoved = true;
            _loop.Hide();
            SetPetHidden(primaryId, hidden: true);
            KernelLog.Write($"pet-hide-primary id={primaryId} deferred=restart");
            RefreshTrayPets();
            RefreshUi();
            return;
        }

        if (_petSet?.Remove(id, out string packPath) == true)
        {
            SetPetHidden(id, hidden: true);
            KernelLog.Write($"pet-hide-extra id={id} pack={packPath}");
        }

        RefreshTrayPets();
        RefreshUi();
    }

    /// <summary>
    /// 把清单里的一只重新摆回桌面（宠物页的「在桌面上显示」）。返回 null = 成功。
    /// 与 RemovePetById 成对：一个收起、一个放回，都不碰素材与清单。
    /// </summary>
    internal string? ShowPetById(string id)
    {
        if (!_primaryRemoved && IsPrimary(id))
        {
            SetPetHidden(id, hidden: false);
            RefreshTrayPets();
            RefreshUi();
            return null;
        }

        // 收起过的主窗那只：IsPrimary 在 _primaryRemoved 之后返回 false，所以按 id 比对。
        if (string.Equals(id, PrimaryPetIdOf(), StringComparison.Ordinal))
        {
            SetPetHidden(id, hidden: false);
            ShowPrimaryPet();
            RefreshUi();
            return null;
        }

        PetDataStore.ActivePack? pack = _store?.Find(id);
        if (pack is null)
        {
            return I18n.Get("cp.pets.desktop.missing");
        }

        if (_petSet is null || _frames?.Roster is null)
        {
            return I18n.Get("cp.pets.desktop.unavailable");
        }

        if (!_petSet.ShowPack(_frames.Roster, pack))
        {
            return I18n.Get("cp.pets.desktop.failed");
        }

        SetPetHidden(id, hidden: false);
        RefreshTrayPets();
        RefreshUi();
        return null;
    }

    /// <summary>落盘的收起表里有主窗这只吗（首滴答用；没有设置存储就当没收起）。</summary>
    private bool IsPrimaryHiddenInSettings()
    {
        IReadOnlyList<string>? hidden = _settings?.Load().HiddenPets;
        if (hidden is null || hidden.Count == 0)
        {
            return false;
        }

        string id = PrimaryPetIdOf();
        foreach (string entry in hidden)
        {
            if (string.Equals(entry, id, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 记下「这一只被收起了」（settings.hiddenPets）。读-改-写在 store.Update 里做一次，
    /// 与切语言/记位置并发写盘时不会互相覆盖（RFC-16 §3.2 的同一纪律）。
    /// </summary>
    private void SetPetHidden(string id, bool hidden)
    {
        UserSettingsStore? store = _settings;
        if (store is null || string.IsNullOrEmpty(id))
        {
            KernelLog.Write($"pet-hide-not-persisted id={id} reason=no-settings");
            return;
        }

        try
        {
            store.Update(actual =>
            {
                var next = actual.HiddenPets is null
                    ? new List<string>()
                    : new List<string>(actual.HiddenPets);
                next.RemoveAll(entry => string.Equals(entry, id, StringComparison.Ordinal));
                if (hidden)
                {
                    next.Add(id);
                }

                return actual with { HiddenPets = next };
            });
            KernelLog.Write($"pet-hidden-persisted id={id} hidden={(hidden ? 1 : 0)}");
        }
        catch (Exception error)
        {
            KernelLog.Write($"pet-hide-persist-failed id={id} {error.GetType().Name}: {error.Message}");
        }
    }
}
