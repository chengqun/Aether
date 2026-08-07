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
