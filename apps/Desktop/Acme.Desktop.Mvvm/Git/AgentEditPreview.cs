using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Acme.Desktop.Mvvm.Git;

/// <summary>
/// 文件编辑工具的判断 + Input 参数取值助手。事后 diff/撤销在 <c>ChatWorkspaceViewModel</c> 里用：
/// call 阶段 <see cref="IsFileEditTool"/> + <see cref="TryGetFilePath"/> 读盘快照 old；
/// result 阶段读盘 new 经 <see cref="LineDiffBuilder"/> 算 diff；撤销写回快照 old。
/// </summary>
/// <remarks>
/// 多文件批量工具 <c>ApplyPatch</c>/<c>ApplyPatchWithDelete</c> 走 <see cref="IsApplyPatchTool"/> +
/// <see cref="TryGetFilePaths"/>（从 patches 数组取所有 path），闭环做多文件快照/diff/独立撤销。
/// </remarks>
public static class AgentEditPreview
{
    /// <summary>内容编辑工具名（DeleteFile/MoveFile/edit_symbol 非内容编辑闭环——它们不改单文件快照路径：
    /// DeleteFile/MoveFile 不可逆走审批；edit_symbol 路径由 Roslyn 定位，call 阶段未知 → diff 看 SCM 页）。</summary>
    public static bool IsFileEditTool(string? toolName)
        => toolName is "EditFile" or "WriteFile" or "LineEditFile";

    /// <summary>多文件批量编辑工具名（ApplyPatch=纯编辑/创建 Safe；ApplyPatchWithDelete=含删除 Dangerous）。</summary>
    public static bool IsApplyPatchTool(string? toolName)
        => toolName is "ApplyPatch" or "ApplyPatchWithDelete";

    /// <summary>从工具 Input 字典取 filePath（规范成绝对路径）；取不到/非法返回 null。</summary>
    public static string? TryGetFilePath(Dictionary<string, object>? input)
    {
        if (input is null) return null;
        if (!TryGetString(input, "filePath", out var filePath) || string.IsNullOrWhiteSpace(filePath))
            return null;
        try { return Path.GetFullPath(filePath); }
        catch { return null; }
    }

    /// <summary>从 ApplyPatch/ApplyPatchWithDelete 的 Input 取所有 patch 的 filePath（去重 + 规范绝对路径）。
    /// MEAI 把 patches 参数反序列化成 JsonElement（数组），每个元素是含 filePath 的对象。</summary>
    public static List<string> TryGetFilePaths(Dictionary<string, object>? input)
    {
        var result = new List<string>();
        if (input is null) return result;
        if (!input.TryGetValue("patches", out var raw) || raw is null) return result;

        // JsonElement 数组（MEAI 反序列化路径）
        if (raw is JsonElement je && je.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in je.EnumerateArray())
            {
                var fp = GetFilePathFromElement(item);
                if (fp is not null) result.Add(fp);
            }
        }
        else if (raw is System.Collections.IEnumerable enumerable and not string)
        {
            // 已展开成对象数组（DI 测试路径）
            foreach (var item in enumerable)
            {
                var fp = GetFilePathFromObject(item);
                if (fp is not null) result.Add(fp);
            }
        }

        // 去重（同文件多 patch 只快照一次）+ 规范化
        return result
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => { try { return Path.GetFullPath(p); } catch { return null; } })
            .Where(p => p is not null)
            .Select(p => p!)
            .Distinct()
            .ToList();
    }

    private static string? GetFilePathFromElement(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (element.TryGetProperty("filePath", out var fpEl))
            return fpEl.ValueKind == JsonValueKind.String ? fpEl.GetString() : null;
        // 容忍 camelCase/pascalCase 差异
        if (element.TryGetProperty("FilePath", out var fpEl2))
            return fpEl2.ValueKind == JsonValueKind.String ? fpEl2.GetString() : null;
        return null;
    }

    private static string? GetFilePathFromObject(object? item)
    {
        if (item is null) return null;
        if (item is JsonElement je) return GetFilePathFromElement(je);
        // 反射兜底（字典/POCO）
        var t = item.GetType();
        var prop = t.GetProperty("FilePath") ?? t.GetProperty("filePath");
        return prop?.GetValue(item)?.ToString();
    }

    // —— Input 字典取值助手（MEAI 把 JSON 参数反序列化成 object，需按类型强转）——

    internal static bool TryGetString(Dictionary<string, object> input, string key, out string value)
    {
        value = string.Empty;
        if (!input.TryGetValue(key, out var raw) || raw is null) return false;
        value = raw switch
        {
            string s => s,
            JsonElement je => je.GetString() ?? string.Empty,
            _ => raw.ToString() ?? string.Empty,
        };
        return true;
    }
}
