using PetDock.Hosting;

namespace PetDock.DyberPet;

/// <summary>
/// 宿主导入按钮看到的 DyberPet 导入器。它只负责「认出目录 + 转成标准包」；
/// 落盘、登记、换源仍由 PetImportService 走同一条生产路径。
/// 转换时把帧复制进输出包并写相对路径，导入后的宠物不再依赖用户原始目录。
/// </summary>
public sealed class DyberPetPackImporter : IPackImporter
{
    public string Id => "com.petdock.dyberpet.importer";

    public bool CanImport(string sourceDir)
    {
        try
        {
            return DyberPetLayout.Probe(sourceDir).Layout != DyberPetLayoutKind.None;
        }
        catch
        {
            // 探测失败不是「认领」：让下一个导入器或标准包路径去报更有用的错。
            return false;
        }
    }

    public void Import(string sourceDir, string outDir) =>
        DyberPetImporter.Import(sourceDir, outDir, bundleFrames: true);
}
