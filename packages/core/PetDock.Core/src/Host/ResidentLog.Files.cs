// ResidentLog 的文件那一半：开文件、滚动、收尾（去掉 .part）、目录总量上限。
//
// 为什么要拆出来：D-03c 的单文件 400 行上限。主文件 ResidentLog.cs 讲的是
// 「队列与节拍」，这里讲的是「文件生命周期」——两件事的失败模式与不变量不同，
// 混在一个文件里时谁也说不清 commit 与滚动各自依赖什么。
//
// 不变量（与主文件同一套）：
//   只有后台写入线程碰 _writer；其它线程只看见 _activePath 这一个字符串。
//   本文件的所有方法都在 _commitGate 之内被调用（持锁者就是那一瞬间的唯一写者）。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace PetDock.Hosting;

/// <summary>常驻形态的运行期日志落盘层。单实例、单写者（后台线程）。</summary>
internal sealed partial class ResidentLog
{
    /// <summary>打开（或复用）当前活动文件；需要滚动时先收尾旧文件。</summary>
    private void EnsureWriter()
    {
        if (_writer is not null && _fileBytes < MaxFileBytes)
        {
            return;
        }

        if (_writer is not null)
        {
            RollToNextFile();
        }

        Directory.CreateDirectory(_directory);
        string path = CurrentFilePath(_fileIndex);
        _writer = new StreamWriter(
            new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        _fileBytes = 0;
        lock (_gate)
        {
            _activePath = path;
        }
    }

    /// <summary>滚动：关掉当前文件并去掉 .part，再开下一个。</summary>
    private void RollToNextFile()
    {
        FinishFile();
        _fileIndex++;
        _fileBytes = 0;
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
            // 关不掉的文件就留着 .part —— 残留的 .part 会被导出标注为未完成。
        }

        _writer = null;
        lock (_gate)
        {
            _activePath = null;
        }

        try
        {
            string part = CurrentFilePath(_fileIndex);
            string done = part.EndsWith(".part", StringComparison.Ordinal)
                ? part[..^".part".Length]
                : part;
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
            // 同上。
        }
    }

    /// <summary>活动文件路径（带 .part 后缀）。</summary>
    private string CurrentFilePath(int index)
    {
        string name = index <= 0
            ? _baseName + ".log"
            : _baseName + "-" + index.ToString(CultureInfo.InvariantCulture) + ".log";
        return Path.Combine(_directory, name + ".part");
    }

    /// <summary>目录总量超上限时，从最旧的**已完结**文件开始删。</summary>
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
            foreach (string file in Directory.EnumerateFiles(_directory, "kernel-*.log"))
            {
                var info = new FileInfo(file);
                total += info.Length;
                done.Add((file, info.Length, info.LastWriteTimeUtc));
            }

            if (total <= MaxDirBytes)
            {
                return;
            }

            done.Sort((a, b) => a.Written.CompareTo(b.Written));

            foreach ((string path, long size, _) in done)
            {
                if (total <= MaxDirBytes)
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
