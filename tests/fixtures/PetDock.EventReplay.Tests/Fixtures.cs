// 夹具的进程级共享访问点：全部判据都从同一个 %TEMP% 目录读同一批夹具（构造器产物，D-13a）。
//
// 为什么共享而不是每个 [Fact] 各建一份：夹具是**构造器产物**、内容与身份无关；各建一份会让
// 每次判据都重写 19 个文件、并且让「同一夹具两次重放相同」这句判据的参照物变成两个不同路径的
// 同名文件（可读性上更容易误读）。生命周期由 Runner 进程决定：首次访问时建，进程退出时删
// （AppDomain.ProcessExit / Dispose）。目录**只落 %TEMP%**，绝不写仓库。
using System;
using System.IO;

namespace PetDock.EventReplay.Tests;

/// <summary>共享夹具目录（懒建，进程退出时删除）。</summary>
internal static class Fixtures
{
    private static FixtureDirectory? _directory;
    private static readonly object Gate = new();

    /// <summary>共享目录的根（首次调用时创建全部夹具）。</summary>
    internal static string Root
    {
        get
        {
            lock (Gate)
            {
                if (_directory is null)
                {
                    _directory = FixtureDirectory.Create();
                    AppDomain.CurrentDomain.ProcessExit += (_, _) => DisposeIfCreated();
                }

                return _directory.Root;
            }
        }
    }

    /// <summary>读一个夹具（按名字）。</summary>
    internal static ReplayFixture Read(string name) =>
        ReplayFixtureIo.Read(name, Path.Combine(Root, name + ".replay.jsonl"));

    /// <summary>读夹具的原始文本（R6 的墙钟禁令要判原始键名）。</summary>
    internal static string RawText(string name) => File.ReadAllText(Path.Combine(Root, name + ".replay.jsonl"));

    /// <summary>全部夹具名（构造器产物的键）。</summary>
    internal static System.Collections.Generic.IReadOnlyList<string> Names()
    {
        var names = new System.Collections.Generic.List<string>();
        foreach (System.Collections.Generic.KeyValuePair<string, ReplayFixture> pair in FixtureConstructor.All())
        {
            names.Add(pair.Key);
        }

        return names;
    }

    private static void DisposeIfCreated()
    {
        lock (Gate)
        {
            _directory?.Dispose();
            _directory = null;
        }
    }
}
