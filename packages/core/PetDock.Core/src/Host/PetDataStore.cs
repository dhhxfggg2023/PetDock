// 用户数据区（RFC-15 §3）：导入的素材与清单落在这里。
//
// 落点取舍（RFC-25 §3.3 修订）：默认 %LOCALAPPDATA%\PetDock —— 用户可写（Program Files 是只读的，
// 写导入素材会失败）、随用户漫游、不需要管理员权限、无人值守也能稳定找到。发布物里带便携标记
// （petdock.portable）时改落程序目录的 data\，让「一个文件夹」自包含、删目录即卸载。
// 仓库内不放：那里是源码，用户自己的东西不该混进源码树。测试与开发用 PETDOCK_DATA_ROOT 显式覆盖。
//
// 为什么不按内容寻址存（docs/15 §2 的那一套）：内容寻址要配套 CAS 引用与引用反查表，
// 而引用方（视图实现）今天只认路径。v1 用「复制进用户数据区 + 按路径引用」近似，
// 代价与欠账记在 RFC-15 §7，不假装已经合规。
using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PetDock.Core.Compositor;

namespace PetDock.Hosting;

/// <summary>
/// 用户数据区（docs/17 §1 的数据形态落点）。
/// 只管三件事：素材包目录、清单文件、当前选中项。
/// </summary>
internal sealed class PetDataStore
{
    /// <summary>显式覆盖数据区根目录的环境变量名（测试与开发用；未设置时用系统默认位置）。</summary>
    public const string RootEnv = "PETDOCK_DATA_ROOT";

    /// <summary>数据区在用户目录下的文件夹名。</summary>
    public const string FolderName = "PetDock";

    /// <summary>
    /// 便携标记文件名（RFC-25 §3.3）：出现在程序目录里即表示「这是个自包含的便携文件夹」，
    /// 数据区随之落在程序目录的 <see cref="PortableDataFolder"/> 下，整个目录可整体搬走/删掉。
    /// 发布物由 tools/release.mjs 写入此文件；开发时仓库里没有它，所以仍走 %LOCALAPPDATA%。
    /// </summary>
    public const string PortableMarker = "petdock.portable";

    /// <summary>便携部署时数据区在程序目录下的子目录名。</summary>
    public const string PortableDataFolder = "data";

    /// <summary>素材包子目录名（刻意不含任何格式词，见 RFC-15 §3.6）。</summary>
    public const string PacksFolder = "packs";

    /// <summary>状态文件子目录名（RFC-24 §6.1）。目录归本类所有，<see cref="Host.StateStore"/> 只管文件。</summary>
    public const string StateFolder = "state";

    /// <summary>清单文件名（记录导入过什么、当前选中哪个）。</summary>
    public const string CatalogFile = "pets.json";

    /// <summary>清单的 schema 版本（docs/17 §1 的规矩：只许加可空字段，版本只向前）。</summary>
    public const int SchemaVersion = 1;

    /// <summary>单文件上限（字节）：超过就是「这不是一份素材包，是别的东西」。</summary>
    public const long MaxFileBytes = 64L * 1024 * 1024;

    /// <summary>单个导入物总大小上限（字节）。</summary>
    public const long MaxPackBytes = 512L * 1024 * 1024;

    public PetDataStore(string root) => Root = root ?? throw new ArgumentNullException(nameof(root));

    /// <summary>数据区根目录。</summary>
    public string Root { get; }

    /// <summary>素材包目录（<root>/packs）。</summary>
    public string PacksDir => Path.Combine(Root, PacksFolder);

    /// <summary>状态文件目录（<root>/state）。</summary>
    public string StateDir => Path.Combine(Root, StateFolder);

    /// <summary>清单文件路径。</summary>
    public string CatalogPath => Path.Combine(Root, CatalogFile);

    /// <summary>当前选中的导入物（实例 id + 素材根目录）；没有则 null。</summary>
    public sealed record ActivePack(string InstanceId, string Path);

    /// <summary>
    /// 定位数据区。优先级：显式覆盖 → 程序目录旁的便携标记 → %LOCALAPPDATA% → 程序目录（退化）。
    /// 便携标记排在前面，是为了让「解压出来的一大坨文件夹」自包含：数据写在程序目录里，
    /// 用户把整个文件夹删掉就是卸载干净（RFC-25 §3.4）。
    /// </summary>
    public static PetDataStore Resolve()
    {
        string? custom = Environment.GetEnvironmentVariable(RootEnv);
        if (!string.IsNullOrWhiteSpace(custom))
        {
            return new PetDataStore(Path.GetFullPath(custom));
        }

        string beside = AppContext.BaseDirectory;
        if (File.Exists(Path.Combine(beside, PortableMarker)))
        {
            return new PetDataStore(Path.Combine(beside, PortableDataFolder));
        }

        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(local))
        {
            local = beside;
        }

        return new PetDataStore(Path.Combine(local, FolderName));
    }

    /// <summary>
    /// 把用户选中的目录**复制**进数据区，返回（实例 id、目录名、素材根路径）。
    /// 复制而不是引用：docs/15 §1 说「宿主不提供绕过通道」，直接引用用户原目录等于给了通道；
    /// 复制也让「用户把原目录删了，宠物还在」成立。
    /// 同名目录不覆盖：加了序号另起一个，导入历史因此不会互相踩。
    /// 失败一律抛（调用方把异常转成界面上的内联错误条）。
    /// </summary>
    public (string InstanceId, string Folder, string Path) StagePack(string sourceDir)
    {
        if (string.IsNullOrWhiteSpace(sourceDir))
        {
            throw new DirectoryNotFoundException("没有选择目录");
        }

        string full = Path.GetFullPath(sourceDir);
        if (!Directory.Exists(full))
        {
            throw new DirectoryNotFoundException($"目录不存在：{full}");
        }

        Directory.CreateDirectory(PacksDir);

        string trimmed = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string baseName = Sanitize(Path.GetFileName(trimmed));
        if (baseName.Length == 0)
        {
            baseName = "pack";
        }

        string folder = baseName;
        int suffix = 2;
        while (Directory.Exists(Path.Combine(PacksDir, folder)))
        {
            folder = baseName + "-" + suffix.ToString(CultureInfo.InvariantCulture);
            suffix++;
        }

        string target = Path.Combine(PacksDir, folder);
        CopyTree(full, target);
        return ("user." + folder, folder, target);
    }

    /// <summary>丢弃一次失败的暂存（导入校验没过时调用；只删本类刚建的目录）。</summary>
    public void Discard(string stagedPath)
    {
        try
        {
            if (Directory.Exists(stagedPath))
            {
                Directory.Delete(stagedPath, recursive: true);
            }
        }
        catch (Exception error)
        {
            KernelLog.Write($"data-store-discard-failed {error.GetType().Name}: {error.Message}");
        }
    }

    /// <summary>
    /// 写清单：登记一个导入物并把它设为当前选中。
    /// 写含 docs/17 §4 要求的两个字段（agent_id 与 timestamp）。
    /// </summary>
    public void RegisterActive(string instanceId, string folder)
    {
        JsonObject catalog = ReadCatalog();
        catalog["schemaVersion"] = SchemaVersion;
        catalog["agent_id"] = "host";
        catalog["timestamp"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);

        JsonArray pets = catalog["pets"] as JsonArray ?? new JsonArray();
        bool known = false;
        foreach (JsonNode? item in pets)
        {
            if (item is JsonObject entry && string.Equals((string?)entry["id"], instanceId, StringComparison.Ordinal))
            {
                entry["folder"] = folder;
                known = true;
            }
        }

        if (!known)
        {
            pets.Add(new JsonObject { ["id"] = instanceId, ["folder"] = folder });
        }

        catalog["pets"] = pets;
        catalog["active"] = instanceId;

        Directory.CreateDirectory(Root);

        // 两阶段提交（docs/17 §3 的形状）：先写临时文件，再整文件替换 ——
        // 清单写到一半断电的话，旧清单还在，不会留下半个文件。
        string temp = CatalogPath + ".tmp";
        File.WriteAllText(
            temp,
            catalog.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));
        File.Move(temp, CatalogPath, overwrite: true);
    }

    /// <summary>读当前选中的导入物；清单不存在或已失效时返回 null。</summary>
    public ActivePack? ReadActive()
    {
        JsonObject catalog = ReadCatalog();
        string? active = (string?)catalog["active"];
        if (string.IsNullOrWhiteSpace(active))
        {
            return null;
        }

        JsonArray pets = catalog["pets"] as JsonArray ?? new JsonArray();
        foreach (JsonNode? item in pets)
        {
            if (item is not JsonObject entry || !string.Equals((string?)entry["id"], active, StringComparison.Ordinal))
            {
                continue;
            }

            string? folder = (string?)entry["folder"];
            if (string.IsNullOrWhiteSpace(folder))
            {
                return null;
            }

            string path = Path.Combine(PacksDir, folder);
            return Directory.Exists(path) ? new ActivePack(active, path) : null;
        }

        return null;
    }

    /// <summary>读清单；文件缺失或损坏时返回空表（损坏的清单不许让程序起不来）。</summary>
    private JsonObject ReadCatalog()
    {
        try
        {
            if (!File.Exists(CatalogPath))
            {
                return new JsonObject();
            }

            JsonNode? parsed = JsonNode.Parse(File.ReadAllText(CatalogPath, Encoding.UTF8));
            return parsed as JsonObject ?? new JsonObject();
        }
        catch (Exception error)
        {
            KernelLog.Write($"data-store-catalog-unreadable {error.GetType().Name}: {error.Message}");
            return new JsonObject();
        }
    }

    /// <summary>目录名净化：只留字母数字与连字符下划线，其余换成连字符。</summary>
    private static string Sanitize(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var chars = new char[raw.Length];
        for (int i = 0; i < raw.Length; i++)
        {
            char c = raw[i];
            bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '-' || c == '_';
            chars[i] = ok ? c : '-';
        }

        return new string(chars).Trim('-');
    }

    /// <summary>
    /// 递归复制目录（.NET 8 没有现成的整目录复制 API）。
    /// 三条护栏：重解析点不跟（避免越界到目录之外）、单文件不超上限、总量不超上限。
    /// 越界与超限都抛 —— 静默跳过会让用户以为「全部导入成功了」。
    /// </summary>
    private static void CopyTree(string from, string to)
    {
        long total = 0;
        CopyInto(from, to, ref total);
    }

    private static void CopyInto(string from, string to, ref long total)
    {
        Directory.CreateDirectory(to);

        foreach (string file in Directory.GetFiles(from))
        {
            FileInfo info = new(file);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException($"不支持的文件（重解析点）：{file}");
            }

            if (info.Length > MaxFileBytes)
            {
                throw new IOException($"文件超出大小上限（{MaxFileBytes} 字节）：{file}");
            }

            total += info.Length;
            if (total > MaxPackBytes)
            {
                throw new IOException($"所选目录总量超出上限（{MaxPackBytes} 字节）");
            }

            File.Copy(file, Path.Combine(to, Path.GetFileName(file)), overwrite: true);
        }

        foreach (string dir in Directory.GetDirectories(from))
        {
            DirectoryInfo info = new(dir);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException($"不支持的目录（重解析点）：{dir}");
            }

            CopyInto(dir, Path.Combine(to, Path.GetFileName(dir)), ref total);
        }
    }
}
