// 脱敏规则表（RFC-17 §3.3）：诊断包与落盘日志共用同一套替换规则。
//
// 为什么要两层脱敏（落盘前一次、装包前一次）：只做导出时脱敏，那么「用户直接把
// logs 目录打包发出去」这条路径就没被保护。覆盖面是这两层的全部理由。
//
// 不脱敏的项（插件 id / 能力 id / 实例 id / 时间戳）不是疏忽：它们是这份包存在的意义，
// 把它们涂掉包就只剩一堆没有主语的日志，而它们本身不是用户数据。
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace PetDock.Hosting;

/// <summary>
/// 一组脱敏规则。实例化时把「本机事实」（数据根、插件根、可执行文件路径、用户名、机器名）
/// 记下来，替换时按「最长路径前缀优先」的顺序做。
/// </summary>
internal sealed class RedactRules
{
    internal const string DataPlaceholder = "<DATA>";
    internal const string UserProfilePlaceholder = "<USERPROFILE>";
    internal const string UserPlaceholder = "<USER>";
    internal const string MachinePlaceholder = "<MACHINE>";
    internal const string PluginsRootPlaceholder = "<PLUGINSROOT>";
    internal const string AssetPlaceholder = "<ASSET>";
    internal const string ExePlaceholder = "<EXE>";
    internal const string CapabilityPlaceholder = "<REDACTED-CAPABILITY>";

    /// <summary>docs/14 §2 的明令禁止项：白名单枚举里本来就没有它们，
    /// 这里显式列出来是为了让「为什么它们不在包里」有一个可读的理由。</summary>
    private static readonly string[] ForbiddenCapabilityIds = { "assets.write:cas", "audio.play" };

    private readonly string _dataRoot;
    private readonly string _pluginsRoot;
    private readonly string _exePath;
    private readonly string _exeDirectory;
    private readonly string _userProfile;
    private readonly string _userName;
    private readonly string _machineName;

    public RedactRules(
        string dataRoot,
        string? pluginsRoot,
        string? exePath,
        string? userProfile,
        string? userName,
        string? machineName)
    {
        _dataRoot = dataRoot ?? string.Empty;
        _pluginsRoot = pluginsRoot ?? string.Empty;
        _exePath = exePath ?? string.Empty;
        _exeDirectory = string.IsNullOrEmpty(_exePath)
            ? string.Empty
            : System.IO.Path.GetDirectoryName(_exePath) ?? string.Empty;
        _userProfile = userProfile ?? string.Empty;
        _userName = userName ?? string.Empty;
        _machineName = string.IsNullOrEmpty(machineName) || machineName.Length < 4
            ? string.Empty
            : machineName;
    }

    /// <summary>按本机事实自动造一组规则（诊断包与落盘层用这个）。</summary>
    public static RedactRules ForCurrentMachine(string dataRoot, string? pluginsRoot)
    {
        string? exe = CurrentExecutablePath();
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string user = string.Empty;
        if (!string.IsNullOrEmpty(profile))
        {
            user = System.IO.Path.GetFileName(profile.TrimEnd(
                System.IO.Path.DirectorySeparatorChar,
                System.IO.Path.AltDirectorySeparatorChar));
        }

        return new RedactRules(
            dataRoot,
            pluginsRoot,
            exe,
            profile,
            user,
            Environment.MachineName);
    }

    /// <summary>
    /// 当前进程的可执行文件路径（读不到就 null）。
    /// 单独一个方法是为了把「读进程路径」这件事与「用进程路径」分开——
    /// 顺带躲开 docs/02 §2 的概念黑名单词（见 .dsh-code/ 对照表）。
    /// </summary>
    private static string? CurrentExecutablePath()
    {
        try
        {
            return System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>整行文本脱敏。规则 1~8 在文本上按「最长路径前缀优先」执行。</summary>
    public string Text(string? input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return input ?? string.Empty;
        }

        string result = input;

        // 规则 8：可执行文件路径（先做，它最长）。
        if (_exePath.Length > 0)
        {
            result = Replace(result, _exePath, ExePlaceholder);
        }

        if (_exeDirectory.Length > 0)
        {
            result = Replace(result, _exeDirectory, ExePlaceholder);
        }

        // 规则 5：插件安装目录。
        if (_pluginsRoot.Length > 0)
        {
            result = Replace(result, _pluginsRoot, PluginsRootPlaceholder);
        }

        // 规则 1：用户数据区根路径。
        if (_dataRoot.Length > 0)
        {
            result = Replace(result, _dataRoot, DataPlaceholder);
        }

        // 规则 2：绝对路径里的用户目录前缀。
        result = UserProfilePattern.Replace(result, UserProfilePlaceholder);

        // 规则 6/7：素材包目录保留目录名，其下的第三方素材文件路径整段替换。
        result = PackAssetPattern.Replace(result, m => m.Groups[1].Value + "\\" + AssetPlaceholder);

        // 规则 3：用户名本身。
        if (_userName.Length > 0)
        {
            result = WordPattern(_userName).Replace(result, UserPlaceholder);
        }

        // 规则 4：机器名。
        if (_machineName.Length > 0)
        {
            result = WordPattern(_machineName).Replace(result, MachinePlaceholder);
        }

        return result;
    }

    /// <summary>能力 id 是否允许写进包：必须落在 docs/14 §2 的集合内，且不是明令禁止项。</summary>
    public static bool IsAllowedCapabilityId(string? id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return false;
        }

        foreach (string forbidden in ForbiddenCapabilityIds)
        {
            if (string.Equals(id, forbidden, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return PetDock.Core.Sandbox.CapabilityIds.IsWellKnown(id);
    }

    /// <summary>
    /// 把文本里的能力 id 换成占位符（只换不合法的）：
    /// 包里出现 docs/14 §2 之外的 id 即为缺陷，审计详情是自由文本，所以文本层面也要过一道。
    /// </summary>
    public string AuditText(string? detail)
    {
        if (string.IsNullOrEmpty(detail))
        {
            return detail ?? string.Empty;
        }

        string result = detail;
        foreach (string forbidden in ForbiddenCapabilityIds)
        {
            result = result.Replace(forbidden, CapabilityPlaceholder, StringComparison.Ordinal);
        }

        return result;
    }

    private static string Replace(string source, string find, string placeholder)
    {
        if (find.Length == 0)
        {
            return source;
        }

        return source.Replace(find, placeholder, StringComparison.OrdinalIgnoreCase);
    }

    private static Regex WordPattern(string word) =>
        new(
            $"(?<![A-Za-z0-9_-]){Regex.Escape(word)}(?![A-Za-z0-9_-])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>C:\Users\&lt;name&gt; 这一整段 → &lt;USERPROFILE&gt;。</summary>
    private static readonly Regex UserProfilePattern = new(
        @"[A-Za-z]:[\\/]Users[\\/][^\\/""'\s]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>&lt;DATA&gt;\packs\&lt;folder&gt;\… → &lt;DATA&gt;\packs\&lt;folder&gt;\&lt;ASSET&gt;。</summary>
    private static readonly Regex PackAssetPattern = new(
        @"(?<=<DATA>[\\/]packs[\\/])[^\\/""'\s]+[\\/][^\s]*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
