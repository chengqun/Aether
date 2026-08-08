using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using Acme.Notes.Application;
using Acme.Notes.Domain;
using Acme.Trading.Scripting;

namespace Acme.Trading.AI.Tools;

/// <summary>
/// 创建交易策略工具：把 AI 写的 C# 策略源码存为 CodeStrategy 笔记（NodeType=CodeStrategy, syntax=csharp），
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

    [Description("创建一个交易策略（C# 源码笔记，实现 IStrategy）。创建后可立即用于回测。源码需 using Acme.Trading.Domain; 并实现 IStrategy 接口（WarmUpBars/Initialize/EvaluateSignal）。MarketBar 字段：Time/Open/High/Low/Close/Volume；SignalDirection：Hold/Long/Flat。同名策略已存在则更新其源码（幂等重试/修订不产生重复笔记）。")]
    public async Task<CreateStrategyOutput> CreateStrategy(
        [Description("策略名称（笔记标题，需唯一，回测时按此名选择）")] string name,
        [Description("C# 策略源码：using Acme.Trading.Domain; + 一个 IStrategy 实现类（需无参构造）")] string source,
        [Description("父节点 id（可选，不传建到根节点下）")] string? parentId = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            return new CreateStrategyOutput { Error = "名称不能为空" };
        name = name.Trim();

        // 先编译验证：不过不落库——避免坏策略笔记留在树里、agent 重试又建一个产生重复。
        if (!StrategyScriptCompiler.TryCompile(source, out _, out var compileErr))
            return new CreateStrategyOutput { Success = false, Compiled = false, CompileError = compileErr };

        try
        {
            // 同名策略已存在则更新内容（幂等：agent 重试/修订同一策略不产生重复笔记）；
            // 同名但非代码策略则拒绝——name 是 StrategyRegistry 的键，跨类型同名会覆盖产生歧义。
            var existing = (await _notes.GetAllAsync(cancellationToken))
                .FirstOrDefault(n => string.Equals(n.Title, name, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                if (existing.NodeType != NoteNodeType.CodeStrategy)
                    return new CreateStrategyOutput { Error = $"已存在同名笔记「{name}」（类型 {existing.NodeType}），请换名。" };
                await _notes.UpdateAsync(new UpdateNoteRequest
                {
                    Id = existing.Id,
                    Title = name,
                    Content = source,
                    Syntax = "csharp",
                    NodeType = NoteNodeType.CodeStrategy,
                }, cancellationToken);
                return new CreateStrategyOutput { Success = true, Id = existing.Id, Name = name, Compiled = true };
            }

            var dto = await _notes.CreateAsync(new CreateNoteRequest
            {
                Title = name,
                Content = source,
                NodeType = NoteNodeType.CodeStrategy,
                Syntax = "csharp",
                ParentId = parentId,
            }, cancellationToken);

            // ScriptingService 随后经 NotesChanged 自动编译注册到 StrategyRegistry，回测页选择器自动出现。
            return new CreateStrategyOutput { Success = true, Id = dto.Id, Name = dto.Title, Compiled = true };
        }
        catch (Exception ex)
        {
            return new CreateStrategyOutput { Error = ex.Message };
        }
    }
}
