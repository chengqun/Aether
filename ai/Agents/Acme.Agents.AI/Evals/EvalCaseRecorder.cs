using Aether.AI.Core.Agents;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Acme.Agents.AI.Evals;

/// <summary>
/// 逐 case 检查闭包 + 判定记录器：四个固定检查（关键词/期望输出/工具调用/非空），每项按 query 反查
/// 所属 case 的断言配置——未配置的断言跳过不记录（报告零噪音）。判定走本地闭包（<c>LocalEvaluator</c>
/// 零云端零 judge 模型），结果同时记进本记录器供报告 DTO 用——不读 MEAI 的
/// <c>EvaluationResult</c>，Aether 代码不命名 <c>Microsoft.Extensions.AI.Evaluation</c> 类型（省显式引包）。
/// <para>MAF <c>LocalEvaluator</c> 按检查名写 <c>Metrics[CheckName]</c>——四个名字互异，无覆盖；
/// 逐 case 关键词不能直接用内置 <c>KeywordCheck</c>（它对全部 item 生效），故按 query→case 字典闭包分发。</para>
/// </summary>
internal sealed class EvalCaseRecorder
{
    private const string KeywordsCheckName = "关键词";
    private const string ExpectedOutputCheckName = "期望输出";
    private const string ToolsCheckName = "工具调用";
    private const string NonEmptyCheckName = "非空";

    private readonly IReadOnlyList<EvalCase> _cases;
    private readonly Dictionary<string, EvalCase> _casesByQuery;
    private readonly Dictionary<string, CaseRecord> _records = new(StringComparer.Ordinal);

    public EvalCaseRecorder(IReadOnlyList<EvalCase> cases)
    {
        _cases = cases;
        // 重复 query：首条配置生效、共享判定记录（ToDictionary 会因重复键抛异常炸掉整个套件，勿用）
        _casesByQuery = new Dictionary<string, EvalCase>(StringComparer.Ordinal);
        foreach (var c in cases)
            _casesByQuery.TryAdd(c.Query, c);
    }

    /// <summary>LocalEvaluator 检查集（每检查名唯一，防 Metrics 键覆盖）。</summary>
    public EvalCheck[] BuildChecks() => new EvalCheck[]
    {
        KeywordsCheck,
        ExpectedOutputCheck,
        ToolsCheck,
        NonEmptyCheck,
    };

    /// <summary>按 case 原始顺序取判定（检查跑过的 case 才出现——EvaluateAsync 全量跑故全部出现）。</summary>
    public IReadOnlyList<CaseRecord> GetResults()
        => _cases
            .Select(c => _records.GetValueOrDefault(c.Query))
            .Where(r => r is not null)
            .Select(r => r!)
            .ToList();

    // —— 四个检查：配置了断言才记录判定；未配置返回通过但不记录（报告零噪音） ——

    private EvalCheckResult KeywordsCheck(EvalItem item)
    {
        var rec = Record(item);
        if (Case(item)?.ExpectedKeywords is not { Count: > 0 } keywords)
            return new EvalCheckResult(true, "未配置", KeywordsCheckName);

        var missing = keywords
            .Where(k => !item.Response.Contains(k, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return missing.Count == 0
            ? Verdict(rec, KeywordsCheckName, true, $"包含全部关键词：{string.Join("、", keywords)}")
            : Verdict(rec, KeywordsCheckName, false, $"缺少关键词：{string.Join("、", missing)}");
    }

    private EvalCheckResult ExpectedOutputCheck(EvalItem item)
    {
        var rec = Record(item);
        if (Case(item)?.ExpectedOutput is not { } expected)
            return new EvalCheckResult(true, "未配置", ExpectedOutputCheckName);

        return item.Response.Contains(expected, StringComparison.OrdinalIgnoreCase)
            ? Verdict(rec, ExpectedOutputCheckName, true, "包含期望输出")
            : Verdict(rec, ExpectedOutputCheckName, false, $"回复未包含期望输出「{expected}」");
    }

    private EvalCheckResult ToolsCheck(EvalItem item)
    {
        var rec = Record(item);
        if (Case(item)?.ExpectedTools is not { Count: > 0 } expectedTools)
            return new EvalCheckResult(true, "未配置", ToolsCheckName);

        var called = ExtractCalledTools(item);
        var missing = expectedTools.Where(t => !called.Contains(t)).ToList();
        return missing.Count == 0
            ? Verdict(rec, ToolsCheckName, true, $"调用了全部期望工具：{string.Join("、", expectedTools)}")
            : Verdict(rec, ToolsCheckName, false, $"未调用期望工具：{string.Join("、", missing)}（实际：{(called.Count > 0 ? string.Join("、", called) : "无")}）");
    }

    private EvalCheckResult NonEmptyCheck(EvalItem item)
    {
        var rec = Record(item);
        if (Case(item)?.MinLength is not { } minLength)
            return new EvalCheckResult(true, "未配置", NonEmptyCheckName);

        return item.Response.Length >= minLength
            ? Verdict(rec, NonEmptyCheckName, true, $"长度 {item.Response.Length} ≥ {minLength}")
            : Verdict(rec, NonEmptyCheckName, false, $"回复过短：长度 {item.Response.Length} < {minLength}");
    }

    private EvalCase? Case(EvalItem item) => _casesByQuery.GetValueOrDefault(item.Query);

    /// <summary>按 query 取/建判定记录（首个检查触达时采集实际回复；重复 query 共享同一条）。</summary>
    private CaseRecord Record(EvalItem item)
        => _records.TryGetValue(item.Query, out var r)
            ? r
            : _records[item.Query] = new CaseRecord(item.Query, item.Response);

    private static EvalCheckResult Verdict(CaseRecord rec, string name, bool passed, string reason)
    {
        rec.Add(name, passed, reason);
        return new EvalCheckResult(passed, reason, name);
    }

    /// <summary>从会话消息提取实际调用过的工具名（镜像 MAF <c>EvalChecks.GetCalledTools</c>——该方法 private 不可复用）。</summary>
    internal static HashSet<string> ExtractCalledTools(EvalItem item)
    {
        var called = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var message in item.Conversation)
            foreach (var content in message.Contents)
                if (content is FunctionCallContent call)
                    called.Add(call.Name);
        return called;
    }

    /// <summary>单 case 判定记录（query + 实际回复 + 各已配置断言的判定）。</summary>
    internal sealed class CaseRecord
    {
        public CaseRecord(string query, string response)
        {
            Query = query;
            Response = response;
        }

        public string Query { get; }
        public string Response { get; }
        public List<AgentEvalCheckResult> Checks { get; } = new();

        /// <summary>case 级判定 = 全部已记录断言通过（未配置任何断言 → 空列表恒通过）。</summary>
        public bool Passed => Checks.All(c => c.Passed);

        public void Add(string name, bool passed, string reason)
            => Checks.Add(new AgentEvalCheckResult(name, passed, reason));
    }
}
