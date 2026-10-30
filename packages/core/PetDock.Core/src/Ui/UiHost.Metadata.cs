// 宠物行的素材包元数据（2026-10-29）：作者 / 许可 / 目录 / 图标。
// 快照装配统一在这里补，页面只读；不把文件 IO 塞进 UiRoster（那个类是纯数据装配）。
using System.Collections.Generic;
using PetDock.Hosting;

namespace PetDock.Core.Ui;

internal sealed partial class UiHost
{
    private UiSnapshot DecoratePetMetadata(UiSnapshot snapshot)
    {
        if (_store is null || snapshot.Pets.Count == 0)
        {
            return snapshot;
        }

        var pets = new List<PetLine>(snapshot.Pets.Count);
        foreach (PetLine line in snapshot.Pets)
        {
            PetDataStore.ActivePack? pack = _store.Find(line.Id);
            if (pack is null)
            {
                pets.Add(line);
                continue;
            }

            PetPackMetadata? meta = PetPackMetadataReader.Read(pack.Path);
            pets.Add(line with
            {
                Author = meta?.Author,
                License = meta?.License,
                Path = pack.Path,
                IconPath = meta?.IconPath,
            });
        }

        return snapshot with { Pets = pets };
    }
}
