using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Acme.Desktop.Mvvm.Build;

/// <summary>
/// 解析 <c>dotnet test</c> 默认 console logger 输出为结构化测试摘要。纯函数，无 I/O，可单测。
/// </summary>
/// <remarks>
/// 解析两类信息：
/// <list type="bullet">
/// <item>摘要行：<c>Passed!  - Failed: N, Passed: M, Skipped: K, Total: T, Duration: ...</c>（或 <c>Failed!</c>）</item>
/// <item>失败块：<c>Failed &lt;name&gt; [time]</c> + <c>Error Message:</c> 段 + <c>Stack Trace:</c> 段（状态机收集）</item>
/// </list>
/// 依赖默认 console logger 格式；若用户改用 TRX/JUnit logger，格式会变，解析可能失准。
/// </remarks>
public static class TestOutputParser
{
    private static readonly Regex SummaryLine = new(
        @"^\s*(?<result>Passed|Failed)!\s+- Failed:\s+(?<failed>\d+),\s+Passed:\s+(?<passed>\d+),\s+Skipped:\s+(?<skipped>\d+),\s+Total:\s+(?<total>\d+)",
        RegexOptions.Compiled);

    private static readonly Regex FailedTestLine = new(
        @"^\s*Failed\s+(?<name>.+)\s+\[[^\]]+\]\s*$",
        RegexOptions.Compiled);

    /// <summary>解析合并后的 stdout（已 2>&1）。</summary>
    public static TestSummary Parse(string combinedOutput)
    {
        var failures = new List<TestFailure>();
        bool success = false;
        int total = 0, passed = 0, failed = 0, skipped = 0;
        bool sawSummary = false;

        if (string.IsNullOrEmpty(combinedOutput))
            return new TestSummary(false, 0, 0, 0, 0, failures);

        // 失败块状态机
        string? curName = null;
        var errMsg = new List<string>();
        var stack = new List<string>();
        int mode = 0; // 0=none, 1=errorMessage, 2=stackTrace

        void Flush()
        {
            if (curName is null) return;
            failures.Add(new TestFailure(
                curName,
                string.Join("\n", errMsg).Trim(),
                stack.Count > 0 ? string.Join("\n", stack).Trim() : null));
            curName = null;
            errMsg.Clear();
            stack.Clear();
            mode = 0;
        }

        foreach (var raw in combinedOutput.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var trimmed = line.Trim();

            // 摘要行（通常在最后）——先 flush 再记录。
            var sm = SummaryLine.Match(line);
            if (sm.Success)
            {
                Flush();
                success = sm.Groups["result"].Value == "Passed";
                failed = ParseInt(sm.Groups["failed"].Value);
                passed = ParseInt(sm.Groups["passed"].Value);
                skipped = ParseInt(sm.Groups["skipped"].Value);
                total = ParseInt(sm.Groups["total"].Value);
                sawSummary = true;
                continue;
            }

            // 新失败用例行——flush 上一个，开始新块。
            var fm = FailedTestLine.Match(line);
            if (fm.Success)
            {
                Flush();
                curName = fm.Groups["name"].Value.Trim();
                mode = 0;
                continue;
            }

            if (curName is null) continue; // 失败块之外的内容跳过

            if (trimmed == "Error Message:")
            {
                mode = 1;
                continue;
            }
            if (trimmed == "Stack Trace:")
            {
                mode = 2;
                continue;
            }

            // 块内内容行（按当前 mode 归类）
            if (mode == 1) errMsg.Add(trimmed);
            else if (mode == 2) stack.Add(trimmed);
        }
        Flush(); // 尾部块（无 summary 收尾的情况）

        return new TestSummary(
            Success: sawSummary && success,
            Total: total,
            Passed: passed,
            Failed: failed,
            Skipped: skipped,
            Failures: failures);
    }

    private static int ParseInt(string s) => int.TryParse(s, out var v) ? v : 0;
}

/// <summary>测试结果摘要。</summary>
public sealed record TestSummary(bool Success, int Total, int Passed, int Failed, int Skipped, List<TestFailure> Failures);

/// <summary>一个失败用例：名字 + 错误消息 + 可选堆栈。</summary>
public sealed record TestFailure(string Name, string ErrorMessage, string? StackTrace);
