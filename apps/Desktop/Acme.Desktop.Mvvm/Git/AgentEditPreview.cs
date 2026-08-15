using System.Collections.Generic;
using System.IO;

namespace Acme.Desktop.Mvvm.Git;

/// <summary>
/// 文件编辑工具的判断 + Input 参数取值助手。事后 diff/撤销在 <c>ChatWorkspaceViewModel</c> 里用：
/// call 阶段 <see cref="IsFileEditTool"/> + <see cref="TryGetFilePath"/> 读盘快照 old；
/// result 阶段读盘 new 经 <see cref="LineDiffBuilder"/> 算 diff；撤销写回快照 old。
/// </summary>
public static class AgentEditPreview
{
    /// <summary>内容编辑工具名（DeleteFile/MoveFile 非内容编辑，不在此列——它们仍走审批）。</summary>
    public static bool IsFileEditTool(string? toolName)
        => toolName is "EditFile" or "WriteFile" or "LineEditFile";

    /// <summary>从工具 Input 字典取 filePath（规范成绝对路径）；取不到/非法返回 null。</summary>
    public static string? TryGetFilePath(Dictionary<string, object>? input)
    {
        if (input is null) return null;
        if (!TryGetString(input, "filePath", out var filePath) || string.IsNullOrWhiteSpace(filePath))
            return null;
        try { return Path.GetFullPath(filePath); }
        catch { return null; }
    }

    // —— Input 字典取值助手（MEAI 把 JSON 参数反序列化成 object，需按类型强转）——

    internal static bool TryGetString(Dictionary<string, object> input, string key, out string value)
    {
        value = string.Empty;
        if (!input.TryGetValue(key, out var raw) || raw is null) return false;
        value = raw switch
        {
            string s => s,
            System.Text.Json.JsonElement je => je.GetString() ?? string.Empty,
            _ => raw.ToString() ?? string.Empty,
        };
        return true;
    }
}
