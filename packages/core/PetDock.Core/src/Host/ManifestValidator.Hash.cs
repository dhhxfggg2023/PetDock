// ManifestValidator 的热重载判据半边：manifest 归一化 JSON 的 SHA-256。
// 类主体与分发在 ManifestValidator.cs（partial 同类）。
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PetDock.Hosting;

internal static partial class ManifestValidator
{
    /// <summary>热重载判据（契约 §5.3）：manifest 归一化 JSON 的 SHA-256（大写十六进制）。
    /// 归一化 = 对象键 ordinal 排序 + 紧凑无空白 + 数值/字符串保持 JSON 原字面编码；
    /// 因此纯 mtime 变化或仅键序/空白重排不触发重载，语义内容变化才触发。</summary>
    public static string NormalizedHash(string manifestText)
    {
        JsonNode? node = JsonNode.Parse(manifestText);
        if (node is null)
        {
            throw new InvalidOperationException("manifest text parsed to null");
        }
        StringBuilder sb = new();
        Canonicalize(node, sb);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash);
    }

    private static void Canonicalize(JsonNode node, StringBuilder sb)
    {
        switch (node)
        {
            case JsonObject obj:
                sb.Append('{');
                bool first = true;
                foreach (KeyValuePair<string, JsonNode?> kv in obj.OrderBy(k => k.Key, StringComparer.Ordinal))
                {
                    if (!first)
                    {
                        sb.Append(',');
                    }
                    first = false;
                    sb.Append(JsonSerializer.Serialize(kv.Key)).Append(':');
                    if (kv.Value is null)
                    {
                        sb.Append("null");
                    }
                    else
                    {
                        Canonicalize(kv.Value, sb);
                    }
                }
                sb.Append('}');
                break;
            case JsonArray arr:
                sb.Append('[');
                bool firstItem = true;
                foreach (JsonNode? item in arr)
                {
                    if (!firstItem)
                    {
                        sb.Append(',');
                    }
                    firstItem = false;
                    if (item is null)
                    {
                        sb.Append("null");
                    }
                    else
                    {
                        Canonicalize(item, sb);
                    }
                }
                sb.Append(']');
                break;
            default:
                // 字符串/数值/布尔：保持 JSON 编码原样（数值字面 1.0 与 1.00 视为不同内容，
                // 宁可多一次重载，不可漏一次 —— 保守方向）。
                sb.Append(node.ToJsonString());
                break;
        }
    }
}
