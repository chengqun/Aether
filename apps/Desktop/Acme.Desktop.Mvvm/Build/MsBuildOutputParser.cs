using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Acme.Desktop.Mvvm.Build;

/// <summary>
/// 解析 MSBuild / dotnet build 的控制台输出为结构化诊断列表。纯函数，无 I/O，可单测。
/// </summary>
/// <remarks>
/// MSBuild 诊断行格式（标准化，跨平台）：
/// <code>
/// path(line,col): error CS0246: message
/// path(line,col): warning CS0168: message
/// path(line,col): error : message   （某些 target 错误无错误码）
/// </code>
/// 续行（以空格开头）忽略——错误主行已含完整消息。
/// </remarks>
public static class MsBuildOutputParser
{
    // file 非贪婪到 (line,col)；severity 固定 error|warning；code 可选（容忍 "error : msg"）。
    private static readonly Regex DiagnosticLine = new(
        @"^(?<file>.+?)\((?<line>\d+),(?<col>\d+)\):\s*(?<sev>error|warning)\s+(?<code>[^\s:]+)?:\s*(?<msg>.+)$",
        RegexOptions.Compiled);

    /// <summary>解析合并后的 stdout（已 2>&1），返回所有 error+warning（按 Severity 区分）。</summary>
    public static List<BuildError> Parse(string combinedOutput)
    {
        var result = new List<BuildError>();
        if (string.IsNullOrEmpty(combinedOutput)) return result;

        foreach (var raw in combinedOutput.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var m = DiagnosticLine.Match(line);
            if (!m.Success) continue;

            result.Add(new BuildError(
                File: m.Groups["file"].Value.Trim(),
                Line: int.TryParse(m.Groups["line"].Value, out var ln) ? ln : 0,
                Column: int.TryParse(m.Groups["col"].Value, out var col) ? col : 0,
                Severity: m.Groups["sev"].Value,
                Code: m.Groups["code"].Success ? m.Groups["code"].Value : string.Empty,
                Message: m.Groups["msg"].Value.Trim()));
        }
        return result;
    }
}

/// <summary>一条 MSBuild 诊断（error 或 warning）。</summary>
/// <param name="Severity"><c>"error"</c> 或 <c>"warning"</c>。</param>
/// <param name="Code">错误码，如 <c>CS0246</c>；无码时为空串。</param>
public sealed record BuildError(string File, int Line, int Column, string Severity, string Code, string Message);
