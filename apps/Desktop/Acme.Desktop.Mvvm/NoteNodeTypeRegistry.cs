using Acme.Notes.Domain;

namespace Acme.Desktop.Mvvm;

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
            //                          IsFormNode HasAgentRunner IsChatTarget  Description          FillIconPath
            new(NoteNodeType.Note,     "文本笔记",  NavIcons.FileText, "#6B6B70", null,         null,   "新笔记",   "",                  false, false, false, "纯文本/markdown 文档", I.Description),
            new(NoteNodeType.Agent,    "AI 助手",   NavIcons.Bot,      "#5B5FC7", "bot",        "yaml", "新 Agent", "# Agent 模板\n\n系统提示词...", true,  true,  true, "智能体 + 规则 + 代码规则", I.SmartToy),
            new(NoteNodeType.Workflow, "工作流",    NavIcons.Workflow, "#1F8A4C", "workflow",   "json", "新专家团", "# Workflow 模板\n\n",              true,  true,  true, "多智能体协作编排", I.Tree),
            new(NoteNodeType.Knowledge, "知识对象", NavIcons.Knowledge, "#0891B2", "knowledge", "yaml", "新知识对象", KnowledgeTemplate,            true,  false, false, "typed links 知识图谱", I.Book),
            new(NoteNodeType.Rule,      "规则",      NavIcons.Rule,      "#B45309", "rule",      "yaml", "新规则",   RuleTemplate,                       true,  true,  true, "无 LLM 确定性规则大脑", I.Receipt),
            new(NoteNodeType.CodeRule,  "代码规则",  NavIcons.Code,      "#1F8A4C", "code",      "csharp", "新代码规则", CodeRuleTemplate,             true,  true,  true, "C# 源码编译成规则大脑", I.Code),
            new(NoteNodeType.CodeStrategy, "代码策略", NavIcons.LineChart, "#2563EB", "line",     "csharp", "新代码策略", CodeStrategyTemplate,       false, false, false, "C# IStrategy 实现类", I.QueryStats),
            new(NoteNodeType.Strategy,  "策略(YAML)", NavIcons.BarChart, "#7C3AED", "bar",       "yaml",   "新策略",   YamlStrategyTemplate,           true,  false, false, "声明式 YAML 配方", I.BarChart),
            new(NoteNodeType.LeanAlgorithm, "Lean 算法", NavIcons.Cpu,    "#0EA5E9", "cpu",       "csharp", "新 Lean 算法", LeanAlgorithmTemplate,       false, false, false, "直写 QCAlgorithm 子类", I.Memory),
            // 技能是文件夹节点 skills/<name>/SKILL.md（+ 可选兄弟脚本）。镜像知识对象两栏 form（无 links）。
            // HasAgentRunner=false——技能无运行时缓存需失效；IsChatTarget=false——技能非聊天目标，是跨 agent 按需 load_skill 引用的提示词能力。
            new(NoteNodeType.Skill,     "技能",      NavIcons.Sparkles,  "#7C3AED", "sparkles",  "markdown", "新技能",   SkillTemplate,                  true,  false, false, "跨 agent 按需 load 的能力", I.Extension),
            // MCP server 配置：扁平 mcp-servers/<name>.json（mcpServers 映射格式，兼容 Claude Code/Cursor）。
            // 纯代码节点（全宽 JSON 编辑器，无 form）；点击 mcp-servers 根目录内嵌 MCP 管理面板（镜像专家页 MCP tab）。
            new(NoteNodeType.McpServer, "MCP 服务器", NavIcons.Plug,      "#6366F1", "plug",      "json",     "新 MCP 配置", McpTemplate,                  false, false, false, "外部工具服务器配置", I.Cloud),
        };
    }

    /// <summary>入口节点的 Material filled 图标（24×24 白填充字形，与手机 AiSettingsPage 同源）。
    /// 5 类直接搬手机端 path，其余按 Material Symbols (Material Icons) 官方 filled path 补齐。命名按语义。</summary>
    private static class I
    {
        public const string Description = "M14 2H6c-1.1 0-2 .9-2 2v16c0 1.1.9 2 2 2h12c1.1 0 2-.9 2-2V8l-6-6zm2 16H8v-2h8v2zm0-4H8v-2h8v2zm-3-5V3.5L18.5 9H13z";
        public const string SmartToy = "M20 9V7c0-1.1-.9-2-2-2h-3c0-1.66-1.34-3-3-3S9 3.34 9 5H6c-1.1 0-2 .9-2 2v2c-1.66 0-3 1.34-3 3s1.34 3 3 3v4c0 1.1.9 2 2 2h12c1.1 0 2-.9 2-2v-4c1.66 0 3-1.34 3-3s-1.34-3-3-3zm-9 7H8v-2h3v2zm0-4H8v-2h3v2zm5 4h-3v-2h3v2zm0-4h-3v-2h3v2z";
        public const string Tree = "M3 4h4v4H3zM17 4h4v4h-4zM10 16h4v4h-4zM5 10.4h14v1.2H5zM4.4 8h1.2v2.4H4.4zM18.4 8h1.2v2.4H18.4zM11.4 11h1.2v5h-1.2z";
        public const string Book = "M18 2H6a2 2 0 00-2 2v16a2 2 0 002 2h12a2 2 0 002-2V4a2 2 0 00-2-2zM6 4h5v8l-2.5-1.5L6 12V4z";
        public const string Receipt = "M19.5 3.5L18 2l-1.5 1.5L15 2l-1.5 1.5L12 2l-1.5 1.5L9 2 7.5 3.5 6 2 4.5 3.5 3 2v20l1.5-1.5L6 22l1.5-1.5L9 22l1.5-1.5L12 22l1.5-1.5L15 22l1.5-1.5L18 22l1.5-1.5L21 22V2l-1.5 1.5zM19 19.09H5V4.91h14v14.18zM6 15h12v2H6zm0-4h12v2H6zm0-4h12v2H6z";
        public const string Code = "M9.4 16.6 4.8 12l4.6-4.6L8 6l-6 6 6 6 1.4-1.4zm5.2 0 4.6-4.6-4.6-4.6L16 6l6 6-6 6-1.4-1.4z";
        public const string QueryStats = "M19.88 18.47c.44-.7.7-1.51.7-2.39 0-2.49-2.01-4.5-4.5-4.5s-4.5 2.01-4.5 4.5 2.01 4.5 4.5 4.5c.88 0 1.69-.26 2.39-.7L21.58 23 23 21.58l-3.12-3.11zm-3.8.11a2.5 2.5 0 010-5 2.5 2.5 0 010 5zm-.36-8.5c-.74.02-1.45.18-2.1.45l-.55-.83-3.8 6.18-3.01-3.52-3.63 5.81L1.5 20.17 3 21.83l1.61-2.58 3.63-5.81 3.01 3.52 3.99-6.46.28.42c.28-.46.62-.88 1.02-1.25z";
        public const string BarChart = "M5 9.2h3V19H5V9.2zM10.6 5h2.8v14h-2.8V5zm5.6 8H19v6h-2.8v-6z";
        public const string Memory = "M15 9H9v6h6V9zm-2 4h-2v-2h2v2zm8-2V9h-2V7c0-1.1-.9-2-2-2h-2V3h-2v2h-2V3H9v2H7c-1.1 0-2 .9-2 2v2H3v2h2v2H3v2h2v2c0 1.1.9 2 2 2h2v2h2v-2h2v2h2v-2h2c1.1 0 2-.9 2-2v-2h2v-2h-2V9h2zm-4 8H7V7h10v10z";
        public const string Extension = "M20.5 11H19V7c0-1.1-.9-2-2-2h-4V3.5A2.5 2.5 0 0010.5 1 2.5 2.5 0 008 3.5V5H4c-1.1 0-1.99.9-1.99 2v3.8H3.5c1.49 0 2.7 1.21 2.7 2.7s-1.21 2.7-2.7 2.7H2V20c0 1.1.9 2 2 2h3.8v-1.5c0-1.49 1.21-2.7 2.7-2.7 1.49 0 2.7 1.21 2.7 2.7V22H17c1.1 0 2-.9 2-2v-4h1.5a2.5 2.5 0 000-5 2.5 2.5 0 00-2.5 2.5V11z";
        public const string Cloud = "M19.35 10.04A7.49 7.49 0 0012 4C9.11 4 6.6 5.55 5.35 8.04A5.99 5.99 0 000 14a6 6 0 006 6h13a5 5 0 005-5c0-2.64-2.05-4.78-4.65-4.96z";
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
    /// 代码规则 F# 变体模板（labs <c>coderules/*.coderule.fs</c>，front-matter <c>language: fsharp</c>）。
    /// 每步决策式（<c>IFsRuleBrain</c> + <c>Step</c> DU + Regex active pattern）是 F#-first 作者契约的展示形态；
    /// 线性流程式（<c>FsCodeRuleBrain</c> + task CE）见正文注释。由 FCS（<c>FsRuleScriptCompiler</c>）编译。
    /// 模板是公开 const——资料库页「新建 → F# 代码规则」直接取用（描述符 DefaultContent 仍是 C# 模板，单一 NodeType）。
    /// </summary>
    public const string CodeRuleFSharpTemplate = @"---
name: 新 F# 代码规则
description: F# 代码规则示例：正则匹配 → 调工具 → 终答
category: 代码规则
language: fsharp
tools: []
tool_approvals: {}
memory: Off
max_history: 20
model:
require_tool_approval: false
---
open Aether.AI.Harness.FSharp

// 每步决策式：每轮 Decide 返回一个 Step（Call 调工具 / Reply 终答）；DU exhaustive matching——漏配分支是编译错。
type MyFsRule() =
    interface IFsRuleBrain with
        member _.Decide(ctx) =
            match ctx.UserText with
            | Regex @""查询\s*(\d+)"" [code] ->
                Call(""Query"", [ ""code"", box code ])
            | ""帮助"" ->
                Reply ""说「查询 123」试试""
            | _ ->
                Reply ""没听懂，说「帮助」""

// 线性流程式（另一种形态）：继承 FsCodeRuleBrain，task { let! } 顺序调工具、字段存状态：
// type MyFlow() =
//     inherit FsCodeRuleBrain()
//     override this.RunAsync() =
//         task {
//             let! r = this.Call(""Query"", [ ""code"", box ""1"" ])
//             return ""结果："" + r
//         }
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
    /// Lean 算法策略默认模板：C# 源码（无 front-matter），一个继承 <c>LeanAlgorithmBase</c>（= QCAlgorithm）的类。
    /// 由 <c>LeanAlgorithmScriptCompiler</c>（Trading.Runtime.Lean）Roslyn 编译成 dll，注册进 <c>LeanAlgorithmRegistry</c>，
    /// 供回测页 Lean 引擎选择器使用。直接复用 Lean 全部能力（原生指标/Portfolio/Schedule/History/OnEndOfDay…）——
    /// 绕过 IStrategy 壳，与 CodeStrategy（IStrategy，Lean 中立）/ Strategy（YAML 声明式）三条路径平行、互不耦合。
    /// <para>数据约定：必须 <c>AddCtpData</c> 订阅本地 CtpBar（回测读预取 CSV），写 AddSecurity 拿不到数据。</para>
    /// <para>参数：runner 把 start/end/cash/symbol + 用户参数设进 Lean Config，<c>ConfigureFromConfig</c> 读前四者；
    /// 用户自定义参数直接 <c>GetParameter("MyParam")</c>。UI 据静态 <c>ParameterDefinitions</c> 渲染输入框。</para>
    /// 注意：Roslyn 编译的源码无 ImplicitUsings，所需 using 须显式写出。name = 笔记标题（= 注册键）。
    /// </summary>
    private const string LeanAlgorithmTemplate = @"using System.Collections.Generic;
using QuantConnect;
using QuantConnect.Algorithm;
using QuantConnect.Data;
using QuantConnect.Indicators;
using Acme.Trading.Domain;
using Acme.Trading.Runtime.Lean;

public class SmaCrossLeanAlgo : LeanAlgorithmBase
{
    private Symbol _symbol;
    private SimpleMovingAverage _fast;
    private SimpleMovingAverage _slow;

    // UI 据此静态成员渲染参数输入框（静态约定，免实例化）。无参数则返回空。
    public static IReadOnlyList<StrategyParameterDef> ParameterDefinitions => new[]
    {
        new StrategyParameterDef(""FastPeriod"", ""快线周期"", ""5""),
        new StrategyParameterDef(""SlowPeriod"", ""慢线周期"", ""20""),
    };

    public override void Initialize()
    {
        // 从 Config 读 start/end/cash 并设置（runner 透传）。用户参数直接 GetParameter(""FastPeriod"")。
        ConfigureFromConfig();

        int fast = GetParameter(""FastPeriod"") is { Length: > 0 } f && int.TryParse(f, out var fv) ? fv : 5;
        int slow = GetParameter(""SlowPeriod"") is { Length: > 0 } s && int.TryParse(s, out var sv) ? sv : 20;

        // 必须用 AddCtpData 订阅本地 CtpBar（回测读预取 CSV）。写 AddSecurity 拿不到本地数据。
        _symbol = AddCtpData(GetParameter(""symbol"") ?? ""600000"", Resolution.Daily);
        _fast = SMA(_symbol, fast, Resolution.Daily);
        _slow = SMA(_symbol, slow, Resolution.Daily);
        SetBenchmark(_symbol);
    }

    public override void OnData(Slice slice)
    {
        if (!(_fast.IsReady && _slow.IsReady)) return;
        if (_fast > _slow && !Portfolio.Invested)
            SetHoldings(_symbol, 1.0m);          // Lean 原生仓位 API——可自由 sizing（区别于 IStrategy 壳的 1/N 均分）
        else if (_fast < _slow && Portfolio.Invested)
            Liquidate(_symbol);
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

    /// <summary>
    /// 技能默认模板：YAML front-matter（name/description/category）+ markdown 正文，与 <c>SkillSourceParser</c> 往返一致。
    /// 技能是跨 agent 按需 <c>load_skill</c> 引用的提示词能力——agent 跑时 <c>AgentFileSkillsSource</c> 直读
    /// <c>skills/&lt;name&gt;/SKILL.md</c>。name 同时是文件夹名(slug)与 front-matter 字段。
    /// 文件夹布局（SKILL.md + 可选兄弟脚本 .py/.sh/.js）由运行时强制，不可扁平化。
    /// </summary>
    private const string SkillTemplate = @"---
name: 新技能
description: 一句话描述技能用途（供 agent 索引判断是否 load）
category: 通用
---
（在此写技能指令 / 脚本调用说明 / 任何 markdown）
";

    /// <summary>
    /// MCP server 配置默认模板：mcpServers 映射格式（兼容 Claude Code/Cursor .mcp.json）。
    /// 每个文件 = 一个 MCP server 配置，经 <c>McpServerConfigLoader</c> 扫描注册。name = 文件名 stem。
    /// </summary>
    private const string McpTemplate = @"{
  ""mcpServers"": {
    ""my-server"": {
      ""command"": ""npx"",
      ""args"": [""-y"", ""@modelcontextprotocol/server-filesystem"", ""/path/to/dir""]
    }
  }
}
";
}
