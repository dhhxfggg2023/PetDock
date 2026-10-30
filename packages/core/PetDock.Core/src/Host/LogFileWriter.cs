// 日志落盘器的文件生命周期（2026-10-28，D 类清理）。
//
// 为什么抽出来：AuditTrail 与 ResidentLog 原本各有一套几乎逐行的「开文件 / 写 / 收尾 / 删旧文件」，
// 已经分叉出两个真 bug ——
//   · AuditTrail 用 string.Length 计「字节」，UTF-8 多字节字符会让上限永远偏小；
//   · AuditTrail 的目录上限通配 audit-*.jsonl* 会把正在写的 .part 算进总量，删到活动文件自己。
// 现在只有这一个写者：行格式、队列、锁、失败语义仍归调用方（它们各不相同），
// 文件生命周期归本类。
//
// 线程：**不是线程安全的**。调用方必须已经握住自己的 commit 锁（AuditTrail._commitGate /
// ResidentLog._commitGate），这与原来「持锁者就是那一瞬间的唯一写者」的不变量一致。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace PetDock.Hosting;

/// <summary>
/// 按批写日志文件：单文件超限滚动、活动文件带 .part、正常收尾去后缀、目录总量上限。
/// 只处理**已经格式化好**的行（每行自带换行），不关心日志内容。
/// </summary>
internal sealed class LogFileWriter
{
    private readonly string _directory;
    private readonly string _baseName;
    private readonly string _extension;
    private readonly long _maxFileBytes;
    private readonly long _maxDirBytes;
    private readonly string _searchPattern;

    private StreamWriter? _writer;
    private long _fileBytes;
    private int _fileIndex;

    /// <param name="directory">日志目录（不存在时第一次写入会建出来）。</param>
    /// <param name="baseName">文件名主干（含时间戳，不含扩展名与序号）。</param>
    /// <param name="extension">含点扩展名（".log" / ".jsonl"）。</param>
    /// <param name="maxFileBytes">单文件上限；用一个很大的值表示不滚动。</param>
    /// <param name="maxDirBytes">目录总量上限；超了从最旧的**已完结**文件开始删。</param>
    public LogFileWriter(string directory, string baseName, string extension, long maxFileBytes, long maxDirBytes)
    {
        _directory = directory ?? throw new ArgumentNullException(nameof(directory));
        _baseName = baseName ?? throw new ArgumentNullException(nameof(baseName));
        _extension = extension ?? throw new ArgumentNullException(nameof(extension));
        _maxFileBytes = maxFileBytes;
        _maxDirBytes = maxDirBytes;

        // 只扫已完结文件：活动文件带 .part，不能被当成「最旧的一份」删掉。
        int dash = _baseName.IndexOf('-', StringComparison.Ordinal);
        string family = dash > 0 ? _baseName[..dash] : _baseName;
        _searchPattern = family + "-*" + _extension;
    }

    /// <summary>当前活动文件路径（带 .part）；还没写过任何一行时为 null。</summary>
    public string? ActivePath { get; private set; }

    /// <summary>
    /// 写一批行。每行已经带换行；按 UTF-8 真实字节数计上限（不是字符数）。
    /// 单文件超限时先收尾当前文件、序号 +1 再继续。
    /// </summary>
    public void Write(IReadOnlyList<string> lines)
    {
        foreach (string line in lines)
        {
            int bytes = Encoding.UTF8.GetByteCount(line);
            EnsureWriter(bytes);
            _writer!.Write(line);
            _fileBytes += bytes;
        }

        _writer?.Flush();
        EnforceDirCap();
    }

    /// <summary>收尾：落盘、关文件、去掉 .part。幂等；再写会新开一份。</summary>
    public void Complete()
    {
        FinishFile();
        _fileIndex = 0;
        _fileBytes = 0;
    }

    private void EnsureWriter(int incomingBytes)
    {
        if (_writer is null)
        {
            OpenNewFile();
            return;
        }

        // 已经在写的文件非空、且再写一行会超上限 ⇒ 滚动。
        // 空文件不滚：上限比一行还小时也至少能写进这一行，否则循环永远开不出文件。
        if (_fileBytes > 0 && _fileBytes + incomingBytes > _maxFileBytes)
        {
            FinishFile();
            _fileIndex++;
            _fileBytes = 0;
            OpenNewFile();
        }
    }

    private void OpenNewFile()
    {
        Directory.CreateDirectory(_directory);
        string path = CurrentPartPath();
        _writer = new StreamWriter(
            new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        _fileBytes = 0;
        ActivePath = path;
    }

    /// <summary>关掉活动文件并去掉 .part 后缀（它从这一刻起才是硬证据）。</summary>
    private void FinishFile()
    {
        if (_writer is null)
        {
            return;
        }

        try
        {
            _writer.Flush();
            _writer.Dispose();
        }
        catch (Exception)
        {
            // 关不掉的文件就留着 .part —— 导出时会被标注为未完成。
        }

        _writer = null;

        try
        {
            string part = CurrentPartPath();
            string done = part[..^".part".Length];
            if (File.Exists(part))
            {
                if (File.Exists(done))
                {
                    File.Delete(done);
                }

                File.Move(part, done);
            }
        }
        catch (Exception)
        {
            // 同上：移动失败不该把日志写者拖垮。
        }
    }

    private string CurrentPartPath() => Path.Combine(_directory, FileName(_fileIndex) + ".part");

    private string FileName(int index) =>
        index <= 0
            ? _baseName + _extension
            : _baseName + "-" + index.ToString(CultureInfo.InvariantCulture) + _extension;

    /// <summary>目录总量超上限时，从最旧的已完结文件开始删（活动 .part 不在扫描范围内）。</summary>
    private void EnforceDirCap()
    {
        try
        {
            if (!Directory.Exists(_directory))
            {
                return;
            }

            var done = new List<(string Path, long Size, DateTime Written)>();
            long total = 0;
            foreach (string file in Directory.EnumerateFiles(_directory, _searchPattern))
            {
                var info = new FileInfo(file);
                total += info.Length;
                done.Add((file, info.Length, info.LastWriteTimeUtc));
            }

            if (total <= _maxDirBytes)
            {
                return;
            }

            done.Sort((a, b) => a.Written.CompareTo(b.Written));
            foreach ((string path, long size, _) in done)
            {
                if (total <= _maxDirBytes)
                {
                    break;
                }

                try
                {
                    File.Delete(path);
                    total -= size;
                }
                catch (Exception)
                {
                    // 删不掉就留着（可能正被别的进程打开）。
                }
            }
        }
        catch (Exception)
        {
            // 目录扫描失败不该影响日志本身。
        }
    }
}
