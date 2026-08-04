using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using Acme.Notes.Application;
using Acme.Notes.Domain;
using Acme.Trading.Scripting;

namespace Acme.Trading.AI.Tools;

/// <summary>
/// 创建交易策略工具：把 AI 写的 C# 策略源码存为策略笔记（tags=strategy, syntax=csharp），
/// 立即编译验证返回错误（前馈传感器）。创建后 NotesChanged → StrategyScriptingService 自动编译注册到
/// StrategyRegistry，回测页策略选择器自动出现（不需重启）。
/// </summary>
public class CreateStrategyTool
{
    private readonly INoteService _notes;

    public CreateStrategyTool(INoteService notes) => _notes = notes;

    public class CreateStrategyOutput
    {
        public bool Success { get; set; }
        public string? Id { get; set; }
        public string? Name { get; set; }
        /// <summary>源码能否编译通过（true=可立即回测；false=看 CompileError 修改）。</summary>
        public bool Compiled { get; set; }
        public string? CompileError { get; set; }
        public string? Error { get; set; }
    }

    [Description("创建一个交易策略（C# 源码笔记，实现 IStrategy）。创建后可立即用于回测。源码需 using Acme.Trading.Domain; 并实现 IStrategy 接口（WarmUpBars/Initialize/EvaluateSignal）。MarketBar 字段：Time/Open/High/Low/Close/Volume；SignalDirection：Hold/Long/Flat。")]
    public async Task<CreateStrategyOutput> CreateStrategy(
        [Description("策略名称（笔记标题，需唯一，回测时按此名选择）")] string name,
        [Description("C# 策略源码：using Acme.Trading.Domain; + 一个 IStrategy 实现类（需无参构造）")] string source,
        [Description("父节点 id（可选，不传建到根节点下）")] string? parentId = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(name))
                return new CreateStrategyOutput { Error = "名称不能为空" };

            var dto = await _notes.CreateAsync(new CreateNoteRequest
            {
                Title = name.Trim(),
                Content = source,
                NodeType = NoteNodeType.Note,
                Syntax = "csharp",
                Tags = "strategy",
                ParentId = parentId,
            }, cancellationToken);

            // 立即编译验证，把错误反馈给 AI（前馈传感器；ScriptingService 随后会自动注册）
            var compiled = StrategyScriptCompiler.TryCompile(source, out _, out var err);
            return new CreateStrategyOutput
            {
                Success = true,
                Id = dto.Id,
                Name = dto.Title,
                Compiled = compiled,
                CompileError = compiled ? null : err,
            };
        }
        catch (Exception ex)
        {
            return new CreateStrategyOutput { Error = ex.Message };
        }
    }
}
