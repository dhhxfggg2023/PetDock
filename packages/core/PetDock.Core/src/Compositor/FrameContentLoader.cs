// 实现 docs/10「内核白名单」里的插件宿主职责：内核只认识 IFrameContentProvider 这一个契约，
// 插件程序集在运行期被发现并加载，内核自始至终不知道素材是什么、在哪、长什么样（docs/40 §3.1）。
using System.Reflection;

namespace PetDock.Core.Compositor;

/// <summary>
/// 运行期内容装配点。发现顺序：显式环境变量指定的程序集 → 仓库 packages/plugins 下的插件产物目录。
/// 任何一步失败（找不到插件、加载失败、没有实现契约、工厂返回 null 或抛异常）都只是「上层没提供内容」，
/// 调用方据此退回内核纯色兜底，窗口仍然可见、仍然可命中。
/// </summary>
internal static class FrameContentLoader
{
    private const string AssemblyOverride = "PETDOCK_FRAME_CONTENT_ASSEMBLY";
    private const string PluginsSegment = "plugins";
    private const string SelfAssemblyPrefix = "PetDock.Core";

    /// <summary>
    /// 尝试由上层内容源工厂产出一帧内容源。note 里带上「用了谁、为什么没用」，
    /// 自检期经 KernelLog 落到 stderr，供取证；不影响任何 stdout 检查行。
    /// </summary>
    internal static IFrameContentSource? TryLoad(out string note)
    {
        List<string> candidates = new();
        string? overridden = Environment.GetEnvironmentVariable(AssemblyOverride);
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            candidates.Add(overridden);
        }

        candidates.AddRange(PluginAssemblies());

        foreach (string candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate) || !File.Exists(candidate))
            {
                continue;
            }

            string fileName;
            try
            {
                fileName = Path.GetFileName(candidate);
            }
            catch (ArgumentException)
            {
                continue;
            }

            if (fileName.StartsWith(SelfAssemblyPrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Assembly assembly;
            try
            {
                assembly = Assembly.LoadFrom(candidate);
            }
            catch (Exception error)
            {
                note = $"assembly-load-failed {fileName}: {error.GetType().Name}";
                continue;
            }

            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException)
            {
                note = $"assembly-types-unreadable {fileName}";
                continue;
            }

            foreach (Type type in types)
            {
                if (!typeof(IFrameContentProvider).IsAssignableFrom(type)
                    || type.IsAbstract
                    || type.IsInterface
                    || type.GetConstructor(Type.EmptyTypes) is null)
                {
                    continue;
                }

                object? instance;
                try
                {
                    instance = Activator.CreateInstance(type);
                }
                catch (Exception error)
                {
                    note = $"provider-construct-failed {type.FullName}: {error.GetType().Name}";
                    continue;
                }

                if (instance is not IFrameContentProvider provider)
                {
                    continue;
                }

                try
                {
                    IFrameContentSource? content = provider.Create();
                    if (content is not null)
                    {
                        note = $"source={type.FullName} assembly={fileName} {provider.Note}";
                        return content;
                    }

                    note = $"provider-declined {type.FullName} {provider.Note}";
                }
                catch (Exception error)
                {
                    note = $"provider-failed {type.FullName}: {error.GetType().Name}";
                }
            }

            note = $"no-provider {fileName}";
        }

        note = candidates.Count == 0
            ? "no-plugin-assembly"
            : $"no-content-source ({candidates.Count} candidate(s))";
        return null;
    }

    /// <summary>
    /// 从进程目录逐级向上找到含 packages/plugins 的仓库根，再枚举插件产物目录里的程序集。
    /// 插件产物与内核产物同构（bin/配置/tfm），所以按目录深度固定取第三层，不解码 csproj。
    /// </summary>
    private static IEnumerable<string> PluginAssemblies()
    {
        DirectoryInfo? root = RepoRoot();
        if (root is null)
        {
            yield break;
        }

        DirectoryInfo plugins;
        try
        {
            plugins = new DirectoryInfo(Path.Combine(root.FullName, "packages", PluginsSegment));
        }
        catch (ArgumentException)
        {
            yield break;
        }

        if (!plugins.Exists)
        {
            yield break;
        }

        IEnumerable<string> assemblies;
        try
        {
            assemblies = plugins.EnumerateFiles("*.dll", SearchOption.AllDirectories)
                .Where(file => file.FullName.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                .OrderBy(file => file.FullName, StringComparer.OrdinalIgnoreCase)
                .Select(file => file.FullName)
                .ToList();
        }
        catch (Exception)
        {
            yield break;
        }

        foreach (string assembly in assemblies)
        {
            yield return assembly;
        }
    }

    private static DirectoryInfo? RepoRoot()
    {
        string? baseDirectory = AppContext.BaseDirectory;
        if (string.IsNullOrWhiteSpace(baseDirectory))
        {
            return null;
        }

        try
        {
            for (DirectoryInfo? current = new DirectoryInfo(baseDirectory);
                 current is not null;
                 current = current.Parent)
            {
                if (Directory.Exists(Path.Combine(current.FullName, "packages", PluginsSegment)))
                {
                    return current;
                }
            }
        }
        catch (Exception)
        {
            return null;
        }

        return null;
    }
}
