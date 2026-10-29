// 宠物页的「多开一只」与「删除」（2026-10-28 第七轮）。
//
// 多开不复制素材：AddInstance 在清单里再加一条指向同一目录的条目（id 用 #2/#3 区分），
// 每只宠物是同一个包的独立实例；删除时只有最后一条引用才删目录。
// 这样「同一个桌宠开三只」不会占三份磁盘，「删掉其中一只」也不会把其余两只带走。
using System;
using System.Collections.Generic;
using PetDock.Core.Compositor;
using PetDock.Hosting;

namespace PetDock.Core.Ui;

internal sealed partial class UiHost
{
    /// <summary>给选中那只再开一只（主窗/额外槽位都可以作为来源）。返回 null = 成功。</summary>
    internal string? DuplicatePetFromPanel(string petId)
    {
        PetDataStore? store = _store;
        PetInstance? instance = null;
        PetDataStore.ActivePack? source = store?.Find(petId);
        if (store is null || source is null)
        {
            return I18n.Get("cp.pets.duplicate.missing");
        }

        if (_petSet is null || _frames?.Roster is null)
        {
            return I18n.Get("cp.pets.duplicate.unavailable");
        }

        int ordinal = 2;
        foreach (PetDataStore.ActivePack pack in store.ReadPets())
        {
            if (string.Equals(pack.Path, source.Path, StringComparison.OrdinalIgnoreCase))
            {
                ordinal++;
            }
        }

        string name = I18n.Get("cp.pets.copyName", source.Name, ordinal);
        PetDataStore.ActivePack added;
        try
        {
            added = store.AddInstance(petId, name);
        }
        catch (Exception error)
        {
            KernelLog.Write($"pet-duplicate-catalog-failed id={petId} {error.GetType().Name}: {error.Message}");
            return I18n.Get("cp.pets.duplicate.failed");
        }

        try
        {
            instance = _frames.Roster.BuildInstance(
                "multi", new[] { source.Path }, freshView: true, imported: true, displayName: name);
        }
        catch (Exception error)
        {
            KernelLog.Write($"pet-duplicate-build-failed id={petId} {error.GetType().Name}: {error.Message}");
        }

        if (instance is null || !_petSet.TryAdd(instance, source.Path, added.InstanceId))
        {
            instance?.Dispose();
            store.RemoveFromCatalog(added.InstanceId);
            return I18n.Get("cp.pets.duplicate.failed");
        }

        KernelLog.Write($"pet-duplicated source={petId} id={added.InstanceId} name={name}");
        RefreshTrayPets();
        RefreshUi();
        return null;
    }

    /// <summary>
    /// 彻底删除一只：从桌面拿掉、从清单删掉，最后一个引用消失时删素材目录。
    /// 主窗那只的实例归 Program（不能在事件栈里 Dispose），所以只隐藏窗口；重启后不再出现。
    /// </summary>
    internal string? DeletePetFromPanel(string petId)
    {
        PetDataStore? store = _store;
        PetDataStore.ActivePack? pack = store?.Find(petId);
        if (store is null || pack is null)
        {
            return I18n.Get("cp.pets.delete.missing");
        }

        if (IsPrimary(petId))
        {
            _primaryRemoved = true;
            _loop.Hide();
            store.RemoveFromCatalog(petId);
            KernelLog.Write($"pet-delete-primary id={petId} pack={pack.Path}");
        }
        else
        {
            if (_petSet?.Remove(petId, out _) != true)
            {
                return I18n.Get("cp.pets.delete.missing");
            }

            store.RemoveFromCatalog(petId);
            KernelLog.Write($"pet-delete-extra id={petId} pack={pack.Path}");
        }

        bool filesGone = store.DeletePackIfUnreferenced(pack.Path);
        KernelLog.Write($"pet-deleted id={petId} files-gone={filesGone}");
        RefreshTrayPets();
        RefreshUi();
        return null;
    }

    /// <summary>
    /// 把点击/拖动反馈接到一只宠物窗上（主窗与额外槽位共用）。
    /// packId 是**惰性**的：同一个主窗换过素材包之后还要说新包的话，
    /// 接线时就把 id 固定下来会让旧台词一直跟着用户。
    /// </summary>
    private void AttachPetFeedback(LayeredFrameWindow window, Func<string?> packId)
    {
        window.Tapped += () => window.ShowSpeech(PokeLine(packId()));
        window.Dragged += () => window.ShowSpeech(PackLine(packId(), lines => lines.Drag) ?? I18n.Get("ui.bubble.drag"));
        window.Dropped += () => window.ShowSpeech(PackLine(packId(), lines => lines.Drop) ?? I18n.Get("ui.bubble.drop"));
        window.PhysicsStarted += () => window.ShowSpeech(PackLine(packId(), lines => lines.Throw) ?? I18n.Get("ui.bubble.throw"));
        window.PhysicsLanded += () => window.ShowSpeech(PackLine(packId(), lines => lines.Drop) ?? I18n.Get("ui.bubble.drop"));
    }

    /// <summary>
    /// 素材包自带的台词（标准包清单里的 "lines"）。每次点按都读一次小 JSON：
    /// 这是用户点了才发生的事，而缓存会让换包之后还在说旧词。
    /// </summary>
    private PackLineSet PackLinesOf(string? petId)
    {
        if (petId is null || _store?.Find(petId) is not { } pack)
        {
            return PackLineSet.Empty;
        }

        // 语言在读取点决定：素材包按界面语言覆盖台词，换语言之后点一下就说新语言。
        return PetPackMetadataReader.Read(pack.Path)?.Lines.For(I18n.Language) ?? PackLineSet.Empty;
    }

    /// <summary>点按：优先这只宠物自己的台词，没声明才用宿主通用句。</summary>
    private string PokeLine(string? petId)
    {
        IReadOnlyList<string> lines = PackLinesOf(petId).Poke;
        if (lines.Count > 0)
        {
            return lines[Random.Shared.Next(lines.Count)];
        }

        return I18n.Get("ui.bubble.poke" + (Random.Shared.Next(3) + 1));
    }

    /// <summary>包声明的那一组台词里随机一句；没有就返回 null（调用方回落到宿主通用句）。</summary>
    private string? PackLine(string? petId, Func<PackLineSet, IReadOnlyList<string>> pick)
    {
        IReadOnlyList<string> lines = pick(PackLinesOf(petId));
        return lines.Count == 0 ? null : lines[Random.Shared.Next(lines.Count)];
    }
}
