using Acme.Notes.Domain;

namespace Acme.Shell;

/// <summary>
/// 节点类型注册表：登记各类 <see cref="NoteNodeType"/> 的描述符。
/// 调用方通过 <see cref="Get(NoteNodeType)"/> 查找，避免散落的 switch。
/// 新增类型在此登记一行即可；未知类型回退到 <see cref="NoteNodeType.Note"/>。
/// </summary>
public static class NoteNodeTypeRegistry
{
    public static IReadOnlyList<NoteNodeTypeDescriptor> All { get; }

    public static NoteNodeTypeDescriptor Get(NoteNodeType type) =>
        All.FirstOrDefault(d => d.Type == type) ?? All[0];

    static NoteNodeTypeRegistry()
    {
        All = new List<NoteNodeTypeDescriptor>
        {
            new(NoteNodeType.Note,     "文本",      NavIcons.FileText, "#6B6B70", null,         null,   "新笔记",   ""),
            new(NoteNodeType.Agent,    "Agent",     NavIcons.Bot,      "#5B5FC7", "bot",        "yaml", "新 Agent", "# Agent 模板\n\n系统提示词..."),
            new(NoteNodeType.Workflow, "Workflow",  NavIcons.Workflow, "#1F8A4C", "workflow",   "json", "新专家团", "# Workflow 模板\n\n"),
            new(NoteNodeType.Knowledge, "知识对象", NavIcons.Knowledge, "#0891B2", "knowledge", "yaml", "新知识对象", KnowledgeTemplate),
            new(NoteNodeType.Rule,      "规则",      NavIcons.Rule,      "#B45309", "rule",      "yaml", "新规则",   RuleTemplate),
            new(NoteNodeType.CodeRule,  "代码规则",  NavIcons.Code,      "#1F8A4C", "code",      "csharp", "新代码规则", CodeRuleTemplate),
            new(NoteNodeType.CodeStrategy, "代码策略", NavIcons.LineChart, "#2563EB", "line",     "csharp", "新代码策略", CodeStrategyTemplate),
            new(NoteNodeType.Strategy,  "策略",      NavIcons.BarChart,  "#7C3AED", "bar",       "yaml",   "新策略",   YamlStrategyTemplate),
        };
    }

    /// <summary>
    /// KnowledgeObject 默认模板（front-matter + markdown 正文，与 KnowledgeSourceParser 往返一致）。
    /// kind + typed links + 正文。name = 笔记标题。
    /// </summary>
    private const string KnowledgeTemplate = @"---
kind: Entity
links: []
---
（在此写知识说明 / 粘贴结构化数据 / 任何 markdown）
";

    /// <summary>
    /// 代码规则默认模板：YAML front-matter（元数据，与 <c>CodeSourceParser</c> 往返一致）+ 原始 C# 正文。
    /// 正文是一个 <c>CodeRuleBrain</c> 子类——平台 Roslyn 服务编译它成代码大脑（无 LLM，确定性）。
    /// 基类 helper：UserText（用户消息）、Step（本回合已调工具数）、LastResult、AvailableTools、History。
    /// 作者模型为协程式：override <c>RunAsync</c>，<c>await Call("工具名", new { 参数 = 值 })</c> 调工具并取结果，
    /// <c>return "文本"</c> 终答（循环结束）。多步流程直接顺序 await，无需 Step 计数分支。
    /// name 同时是笔记标题与 front-matter 字段；engine 由解析器硬编码为 "Rule"。
    /// 注意：Roslyn 编译的源码无 ImplicitUsings，所需 using 须显式写出。
    /// </summary>
    private const string CodeRuleTemplate = @"---
name: 新代码规则
description: 代码规则示例：回显用户输入
category: 代码规则
tools: []
tool_approvals: {}
memory: Off
max_history: 20
model:
require_tool_approval: false
---
using System.Threading.Tasks;
using Aether.AI.Harness.Agents;

public class EchoRule : CodeRuleBrain
{
    protected override async Task<string> RunAsync()
    {
        // 协程式：await Call(""工具名"", new { 参数 = 值 }) 调工具并取结果；return ""文本"" 终答。
        // UserText = 用户消息；多步流程直接顺序 await，无需手动计步。
        return ""你说了："" + UserText;
    }
}
";

    /// <summary>
    /// 策略默认模板：纯 C# 源码（无 front-matter），一个 <c>IStrategy</c> 实现类。
    /// 由 <c>StrategyScriptCompiler</c>（Trading.Scripting）Roslyn 编译成 <c>IStrategy</c> 实例，注册进
    /// <c>StrategyRegistry</c>，供回测/实盘页策略选择器使用。name = 笔记标题（= StrategyRegistry 键）。
    /// 策略无可配置元数据（参数从代码 <c>ParameterDefinitions</c> 来），故是纯代码节点而非 form 节点。
    /// 复用与 YAML 策略同一套增量指标库（<c>IndicatorFactory</c>）——两形底层同源，对照回测结果一致。
    /// 注意：Roslyn 编译的源码无 ImplicitUsings，所需 using 须显式写出。
    /// </summary>
    private const string CodeStrategyTemplate = @"using System;
using System.Collections.Generic;
using Acme.Trading.Domain;
using Acme.Trading.Domain.Indicators;

public class SmaCrossStrategy : IStrategy
{
    private IIndicator _fast = null!;
    private IIndicator _slow = null!;

    public int WarmUpBars { get; private set; }

    // 策略参数定义：UI 据此动态渲染输入框。默认空；策略可选覆盖。
    public IReadOnlyList<StrategyParameterDef> ParameterDefinitions => new[]
    {
        new StrategyParameterDef(""FastPeriod"", ""快线周期"", ""5""),
        new StrategyParameterDef(""SlowPeriod"", ""慢线周期"", ""20""),
    };

    public void Initialize(StrategyParameters parameters)
    {
        int fast = parameters.Get(""FastPeriod"", 5);
        int slow = parameters.Get(""SlowPeriod"", 20);
        // 复用与 YAML 策略同一套增量指标库（IndicatorFactory）——两形底层同源，对照回测结果一致。
        _fast = IndicatorFactory.Create(""sma"", new Dictionary<string, string> { [""period""] = fast.ToString() }, parameters);
        _slow = IndicatorFactory.Create(""sma"", new Dictionary<string, string> { [""period""] = slow.ToString() }, parameters);
        WarmUpBars = Math.Max(_fast.RequiredBars, _slow.RequiredBars);
    }

    public SignalDirection EvaluateSignal(MarketBar bar)
    {
        _fast.Update(bar);
        _slow.Update(bar);
        if (!_fast.IsReady || !_slow.IsReady) return SignalDirection.Hold;
        // 快线在慢线上方→做多，否则清仓（均线带策略；穿越信号可自行加 prev 比较）
        return _fast.Value > _slow.Value ? SignalDirection.Long : SignalDirection.Flat;
    }
}
";

    /// <summary>
    /// 声明式 YAML 策略默认模板：纯 YAML 配方（无 front-matter），由 <c>StrategyProgramYaml</c>（Trading.Scripting）
    /// 解析成 <c>StrategyProgram</c> → <c>YamlStrategy</c>（Trading.Domain），注册进 <c>StrategyRegistry</c>。
    /// 结构：params（参数定义，UI 渲染输入框）+ indicators（命名指标，值可含 {ParamKey} 模板）+
    /// rules（top-down 首匹配：signal + when 条件 AST）+ default（无匹配信号）。
    /// 条件 AST 是结构化嵌套对象（非表达式字符串）——加新算子 = 加一个 ICondition 类，老 YAML 不动。
    /// 算子：gt/lt/gte/lte/cross_above/cross_below/and/or/not。叶子：指标名（或 name.output）/ 数字。
    /// 复杂逻辑（仓位 sizing / 自定义指标）走 CodeStrategy（C#）。name = 笔记标题（= StrategyRegistry 键）。
    /// </summary>
    private const string YamlStrategyTemplate = @"params:
  - { key: FastPeriod, label: 快线周期, default: 5 }
  - { key: SlowPeriod, label: 慢线周期, default: 20 }
indicators:
  fast: { type: sma, period: ""{FastPeriod}"" }
  slow: { type: sma, period: ""{SlowPeriod}"" }
rules:
  - signal: Long
    when: { cross_above: [fast, slow] }
  - signal: Flat
    when: { cross_below: [fast, slow] }
default: Hold
";

    /// <summary>
    /// 规则默认模板：单个 YAML 文档（元数据 + 规则程序顶层共存，无 front-matter 分隔、无 engine）。
    /// 由 <c>RuleSourceParser</c> 解析：元数据进 <c>CustomAgentDefinition</c> 字段，steps/noMatchReply/maxSteps
    /// 回填到 <c>SystemPrompt</c>（程序部分），再由 <c>RuleProgramYaml</c> 解析成 <c>RuleProgram</c>。
    /// name 同时是笔记标题与 YAML 顶层字段。engine 由解析器硬编码为 "Rule"。
    /// </summary>
    private const string RuleTemplate = @"name: 新规则
description: 规则示例：读<文件路径> → 读取文件内容并回显
category: 规则
tools:
  - ReadFile
toolApprovals: {}
memory: Off
maxHistory: 20
noMatchReply: 无匹配规则
maxSteps: 20
# 程序级捕获：从 user 消息提取一次，所有步骤可复用 {user.N}（步骤无需各自写 onUserRegex）
capture: '读(.+)'
steps:
  - name: 读取文件
    match:
      maxToolCalls: 0
    action:
      tool: ReadFile
      args:
        filePath: '{user.1}'
  - match:
      minToolCalls: 1
    action:
      replyText: '{result}'
";
}
