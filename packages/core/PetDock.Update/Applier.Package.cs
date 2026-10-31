// 「待安装的那一份」怎么被读出来、怎么被验、怎么被解开（2026-10-31 从 Applier.cs 拆出：D-03c）。
//
// 为什么搬文件之前要重新验一次 sha256，而不是直接用下载时解好的那一份：
//   下载时校验的是 zip，宿主随后把它解到 stage/。从 stage/ 装，等于相信「从现在到装机那一刻
//   没有人动过那个目录」—— 而这两个目录都在用户可写的数据区里，中间还隔着「用户下次开机」
//   这不知多久的一段。所以这里重新拿 zip 对一次 sha256，再重新解一份到 unpacked/ 去装：
//   装到盘上的东西因此严格等于那个被校验过的 zip 的内容。
//
// 诚实边界：这挡不住能写数据区的人 —— 他连搬运工本身都能换掉。要做到那一步需要代码签名，
// 本仓库今天没有证书。所以这里只把「校验值真的被用上了」这条落实，不假装它是完整防护。
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace PetDock.Update;

internal static partial class Applier
{
    /// <summary>待安装标记里的 sha256（没有就 null）。</summary>
    private static string? ReadExpectedSha(Options options)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(options.DataRoot, "_update", "pending.json")));
            return doc.RootElement.TryGetProperty("sha256", out JsonElement value)
                && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (Exception error) when (error is IOException or JsonException)
        {
            return null;
        }
    }

    /// <summary>待安装标记里的 zip 落点（没有就 null）。</summary>
    private static string? ReadZipPath(Options options)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(options.DataRoot, "_update", "pending.json")));
            return doc.RootElement.TryGetProperty("zip", out JsonElement value)
                && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (Exception error) when (error is IOException or JsonException)
        {
            return null;
        }
    }

    /// <summary>一个文件的 sha256（小写十六进制）。读不到返回空串（调用方据此判失败）。</summary>
    private static string Sha256Of(string path)
    {
        try
        {
            using FileStream stream = File.OpenRead(path);
            byte[] hash = SHA256.HashData(stream);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// 把 zip 解到 <paramref name="stage"/>（剪掉顶层目录一格）。
    /// 与宿主下载时那一份是同一份形状 —— 这里重写一遍是因为搬运工**不引用内核**
    /// （搬一个坏掉的内核正是它要能处理的情形），所以不能复用 UpdateStage.Extract。
    /// </summary>
    private static bool Unpack(string zip, string stage, Note note, out string? why)
    {
        why = null;
        try
        {
            DeleteTree(stage);
            Directory.CreateDirectory(stage);
            string rootFull = Path.GetFullPath(stage) + Path.DirectorySeparatorChar;
            using ZipArchive archive = ZipFile.OpenRead(zip);
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                if (entry.Name.Length == 0)
                {
                    continue;
                }

                string[] parts = entry.FullName.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2 || entry.FullName.Contains("..", StringComparison.Ordinal))
                {
                    continue;
                }

                string dest = Path.GetFullPath(Path.Combine(stage, string.Join('/', parts[1..])));
                if (!dest.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
                {
                    why = "zip-slip";
                    return false;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                entry.ExtractToFile(dest, overwrite: true);
            }

            return true;
        }
        catch (Exception error) when (error is IOException or InvalidDataException
            or UnauthorizedAccessException or NotSupportedException)
        {
            why = error.GetType().Name;
            note($"unpack-crashed {error.GetType().Name}: {error.Message}");
            return false;
        }
    }

}
