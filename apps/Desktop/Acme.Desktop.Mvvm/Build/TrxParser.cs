using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;

namespace Acme.Desktop.Mvvm.Build;

/// <summary>
/// 解析 <c>dotnet test --logger trx</c> 生成的 TRX（Visual Studio Test Results XML）为结构化测试摘要。
/// 纯函数、无 I/O，可单测。比 <see cref="TestOutputParser"/> 靠默认 console logger 更准——TRX 是结构化 XML，
/// 不受 console 格式/本地化/截断影响。
/// </summary>
/// <remarks>
/// TRX 结构（只取需要的字段）：
/// <code>
/// &lt;TestRun&gt;
///   &lt;Results&gt;
///     &lt;UnitTestResult testName="..." outcome="Passed|Failed|NotExecuted|NotRunnable" duration="..."&gt;
///       &lt;Output&gt;
///         &lt;ErrorInfo&gt;&lt;Message&gt;...&lt;/Message&gt;&lt;StackTrace&gt;...&lt;/StackTrace&gt;&lt;/ErrorInfo&gt;
///       &lt;/Output&gt;
///     &lt;/UnitTestResult&gt;
///   &lt;/Results&gt;
/// &lt;/TestRun&gt;
/// </code>
/// outcome 映射：Passed→passed，Failed→failed，NotExecuted/NotRunnable/其它→skipped。
/// 失败用例的 ErrorMessage/StackTrace 从 ErrorInfo 取。
/// </remarks>
public static class TrxParser
{
    /// <summary>解析 TRX XML 文本。非 TRX/空/格式错返回 null（调用方回退 console parser）。</summary>
    public static TestSummary? Parse(string trxXml)
    {
        if (string.IsNullOrWhiteSpace(trxXml)) return null;
        XDocument doc;
        try { doc = XDocument.Parse(trxXml); }
        catch { return null; } // 非 XML——交给调用方回退

        var results = doc.Descendants(XName.Get("UnitTestResult")).ToList();
        if (results.Count == 0) return null; // 没 UnitTestResult → 不是 TRX 或空

        int passed = 0, failed = 0, skipped = 0, total = results.Count;
        var failures = new List<TestFailure>();

        foreach (var r in results)
        {
            var outcome = (string?)r.Attribute("outcome") ?? string.Empty;
            var testName = (string?)r.Attribute("testName") ?? "(unknown test)";

            switch (outcome)
            {
                case "Passed":
                    passed++;
                    break;
                case "Failed":
                {
                    failed++;
                    var (msg, stack) = ExtractErrorInfo(r);
                    failures.Add(new TestFailure(testName, msg, stack));
                    break;
                }
                default:
                    // NotExecuted / NotRunnable / 其它 → skipped
                    skipped++;
                    break;
            }
        }

        return new TestSummary(
            Success: failed == 0,
            Total: total,
            Passed: passed,
            Failed: failed,
            Skipped: skipped,
            Failures: failures);
    }

    /// <summary>从 UnitTestResult 的 Output/ErrorInfo 取 Message + StackTrace（纯文本，去首尾空白）。</summary>
    private static (string message, string? stackTrace) ExtractErrorInfo(XElement result)
    {
        string message = string.Empty;
        string? stackTrace = null;

        var errorInfo = result.Descendants(XName.Get("ErrorInfo")).FirstOrDefault();
        if (errorInfo is not null)
        {
            var msgEl = errorInfo.Element(XName.Get("Message"));
            if (msgEl is not null)
                message = msgEl.Value.Trim();
            var stackEl = errorInfo.Element(XName.Get("StackTrace"));
            if (stackEl is not null && !string.IsNullOrWhiteSpace(stackEl.Value))
                stackTrace = stackEl.Value.Trim();
        }

        // 没有 ErrorInfo 但 outcome=Failed（极少见）——给个占位避免空消息
        if (string.IsNullOrEmpty(message))
            message = "(测试失败但 TRX 未提供错误消息)";

        return (message, stackTrace);
    }
}
