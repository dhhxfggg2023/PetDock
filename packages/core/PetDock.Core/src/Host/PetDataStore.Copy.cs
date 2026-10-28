// 素材包复制（2026-10-28 第三轮，从 PetDataStore.cs 拆出）：目录递归、总量预扫描、
// 1 MiB 缓冲流复制、字节进度与文件内取消。拆出的直接原因是 D-03c 的单文件 400 行上限；
// 职责上也独立 —— 主文件管「数据区与清单」，这里只管「怎么把一棵目录搬过去」。
using System;
using System.IO;
using System.Threading;
using PetDock.Core.Ui;

namespace PetDock.Hosting;

internal sealed partial class PetDataStore
{
    /// <summary>
    /// 递归复制目录（.NET 8 没有现成的整目录复制 API）。
    /// 三条护栏：重解析点不跟（避免越界到目录之外）、单文件不超上限、总量不超上限。
    /// 越界与超限都抛 —— 静默跳过会让用户以为「全部导入成功了」。
    ///
    /// 复制实现从 File.Copy 换成 1 MiB 缓冲流：只有这样才能在文件**内部**响应取消
    /// 并上报字节进度（File.Copy 是内核整文件复制，中间没有回调）。
    /// 代价是丢掉了 File.Copy 对 ACL/ADS 的保留；素材包只有普通文件，时间戳显式补回。
    /// </summary>
    private static void CopyTree(string from, string to, CancellationToken token, CopyProgressNotify? progress)
    {
        long expected = MeasureTree(from, token);
        long copied = 0;
        progress?.Invoke(0, expected);
        CopyInto(from, to, ref copied, expected, token, progress);
        progress?.Invoke(copied, expected);
    }

    /// <summary>复制前的总量预扫描（只读元数据，为进度条提供分母；安全上限仍由复制时强制）。</summary>
    private static long MeasureTree(string from, CancellationToken token)
    {
        long total = 0;
        MeasureInto(from, ref total, token);
        return total;
    }

    private static void MeasureInto(string from, ref long total, CancellationToken token)
    {
        foreach (string file in Directory.GetFiles(from))
        {
            token.ThrowIfCancellationRequested();
            FileInfo info = new(file);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException(I18n.Get("host.import.err.reparse-file", file));
            }

            total += info.Length;
            if (total > MaxPackBytes)
            {
                throw new IOException(I18n.Get("host.import.err.pack-too-large", MaxPackBytes));
            }
        }

        foreach (string dir in Directory.GetDirectories(from))
        {
            token.ThrowIfCancellationRequested();
            DirectoryInfo info = new(dir);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException(I18n.Get("host.import.err.reparse-dir", dir));
            }

            MeasureInto(dir, ref total, token);
        }
    }

    private static void CopyInto(
        string from,
        string to,
        ref long copied,
        long expected,
        CancellationToken token,
        CopyProgressNotify? progress)
    {
        Directory.CreateDirectory(to);

        foreach (string file in Directory.GetFiles(from))
        {
            token.ThrowIfCancellationRequested();
            FileInfo info = new(file);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException(I18n.Get("host.import.err.reparse-file", file));
            }

            if (info.Length > MaxFileBytes)
            {
                throw new IOException(I18n.Get("host.import.err.file-too-large", MaxFileBytes) + file);
            }

            if (copied + info.Length > MaxPackBytes)
            {
                throw new IOException(I18n.Get("host.import.err.pack-too-large", MaxPackBytes));
            }

            CopyFile(file, Path.Combine(to, Path.GetFileName(file)), ref copied, expected, token, progress);
        }

        foreach (string dir in Directory.GetDirectories(from))
        {
            DirectoryInfo info = new(dir);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException(I18n.Get("host.import.err.reparse-dir", dir));
            }

            CopyInto(dir, Path.Combine(to, Path.GetFileName(dir)), ref copied, expected, token, progress);
        }
    }

    /// <summary>1 MiB 一块地复制一个文件：块边界检查取消、上报进度、补回时间戳。</summary>
    private static void CopyFile(
        string from,
        string to,
        ref long copied,
        long expected,
        CancellationToken token,
        CopyProgressNotify? progress)
    {
        const int bufferBytes = 1024 * 1024;
        var buffer = new byte[bufferBytes];
        using (var input = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.Read, bufferBytes, FileOptions.SequentialScan))
        using (var output = new FileStream(to, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferBytes, FileOptions.SequentialScan))
        {
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                token.ThrowIfCancellationRequested();
                output.Write(buffer, 0, read);
                copied += read;
                progress?.Invoke(copied, expected);
            }
        }

        try
        {
            File.SetLastWriteTimeUtc(to, File.GetLastWriteTimeUtc(from));
        }
        catch (Exception)
        {
            // 时间戳补不回不影响内容；目录权限异常时别把一次成功复制变成失败。
        }
    }
}
