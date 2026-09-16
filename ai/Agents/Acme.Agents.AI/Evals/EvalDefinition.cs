namespace Acme.Agents.AI.Evals;

/// <summary>
/// 评测套件定义（labs <c>evals/*.eval.yaml</c>）。纯数据，与 YAML 面 camelCase 键一一对应。
/// agent 身份按 Guid 引用（agent 文件 front-matter 的 <c>id:</c> 字段，与 eval 的 <c>agentId</c> 对齐）。
/// </summary>
public sealed class EvalDefinition
{
    /// <summary>套件名（必填，报告标题）。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>套件描述（可选，报告副标题）。</summary>
    public string? Description { get; set; }

    /// <summary>被评 agent 的 Guid（必填，指向 labs agents/rules 等文件的 id）。</summary>
    public Guid AgentId { get; set; }

    /// <summary>评测用例（必填非空）。</summary>
    public List<EvalCase> Cases { get; set; } = new();
}

/// <summary>
/// 单条评测 case：query + 期望断言。断言全可选——未配置的断言自动跳过（不产生检查记录，报告零噪音）；
/// case 级判定 = 全部已配置断言通过（未配置任何断言的 case 恒通过，仅采集回复）。
/// </summary>
public sealed class EvalCase
{
    /// <summary>发给 agent 的查询（必填；同套件内重复 query 会共享判定记录，勿重复）。</summary>
    public string Query { get; set; } = string.Empty;

    /// <summary>回复须包含的关键词（全部满足，不区分大小写）。</summary>
    public List<string>? ExpectedKeywords { get; set; }

    /// <summary>期望输出子串（不区分大小写的包含匹配）。</summary>
    public string? ExpectedOutput { get; set; }

    /// <summary>须全部被调用过的工具名（不区分大小写；从会话消息的 FunctionCallContent 提取）。</summary>
    public List<string>? ExpectedTools { get; set; }

    /// <summary>回复最小字符长度。</summary>
    public int? MinLength { get; set; }
}
