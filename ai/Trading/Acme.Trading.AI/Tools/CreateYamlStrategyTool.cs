using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using Acme.Notes.Application;
using Acme.Notes.Domain;
using Acme.Trading.Scripting;

namespace Acme.Trading.AI.Tools;

/// <summary>
/// 创建声明式 YAML 交易策略：把 AI 写的 YAML 配方存为 Strategy 笔记（NodeType=Strategy, syntax=yaml），
/// 立即解析验证返回错误。创建后 NotesChanged → StrategyScriptingService 自动解析注册到 StrategyRegistry，
/// 回测页策略选择器自动出现。镜像 <see cref="CreateStrategyTool"/>（C# 版），校验器用
/// <see cref="StrategyProgramYaml.TryDeserialize(string,out Acme.Trading.Domain.StrategyProgram,out string?)"/>。
/// </summary>
public class CreateYamlStrategyTool
{
    private readonly INoteService _notes;

    public CreateYamlStrategyTool(INoteService notes) => _notes = notes;

    public class CreateYamlStrategyOutput
    {
        public bool Success { get; set; }
        public string? Id { get; set; }
        public string? Name { get; set; }
        /// <summary>YAML 能否解析成 StrategyProgram（true=可立即回测；false=看 CompileError 修改）。</summary>
        public bool Compiled { get; set; }
        public string? CompileError { get; set; }
        public string? Error { get; set; }
    }

    [Description("创建一个声明式 YAML 交易策略笔记。schema: params(参数定义 key/label/default) + indicators(命名指标 {type,params}, type∈sma/ema/rsi/atr/macd/bollinger/stochastic, 参数值可含 {ParamKey} 模板) + rules(top-down 首匹配: signal∈Long/Flat/Hold + when 条件AST) + default(无匹配信号)。when 算子: gt/lt/gte/lte/cross_above/cross_below/and/or/not，叶子为指标名或数字。范例—params:[{key:FastPeriod,label:快线,default:5}]; indicators:{fast:{type:sma,period:\"{FastPeriod}\"},slow:{type:sma,period:\"{SlowPeriod}\"}}; rules:[{signal:Long,when:{cross_above:[fast,slow]}},{signal:Flat,when:{cross_below:[fast,slow]}}]; default:Hold。同名策略已存在则更新其源码。")]
    public async Task<CreateYamlStrategyOutput> CreateYamlStrategy(
        [Description("策略名称（笔记标题，需唯一，回测时按此名选择）")] string name,
        [Description("YAML 策略源码（params/indicators/rules/default 四段）")] string source,
        [Description("父节点 id（可选，不传建到根节点下）")] string? parentId = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            return new CreateYamlStrategyOutput { Error = "名称不能为空" };
        name = name.Trim();

        // 先解析验证：不过不落库——避免坏策略笔记留在树里、agent 重试又建一个产生重复。
        if (!StrategyProgramYaml.TryDeserialize(source, out _, out var parseErr))
            return new CreateYamlStrategyOutput { Success = false, Compiled = false, CompileError = parseErr };

        try
        {
            // 同名策略已存在则更新内容（幂等：agent 重试/修订同一策略不产生重复笔记）；
            // 同名但非 YAML 策略则拒绝——name 是 StrategyRegistry 的键，跨类型同名会覆盖产生歧义。
            var existing = (await _notes.GetAllAsync(cancellationToken))
                .FirstOrDefault(n => string.Equals(n.Title, name, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                if (existing.NodeType != NoteNodeType.Strategy)
                    return new CreateYamlStrategyOutput { Error = $"已存在同名笔记「{name}」（类型 {existing.NodeType}），请换名。" };
                await _notes.UpdateAsync(new UpdateNoteRequest
                {
                    Id = existing.Id,
                    Title = name,
                    Content = source,
                    Syntax = "yaml",
                    NodeType = NoteNodeType.Strategy,
                }, cancellationToken);
                return new CreateYamlStrategyOutput { Success = true, Id = existing.Id, Name = name, Compiled = true };
            }

            var dto = await _notes.CreateAsync(new CreateNoteRequest
            {
                Title = name,
                Content = source,
                NodeType = NoteNodeType.Strategy,
                Syntax = "yaml",
                ParentId = parentId,
            }, cancellationToken);

            // ScriptingService 随后经 NotesChanged 自动解析注册到 StrategyRegistry，回测页选择器自动出现。
            return new CreateYamlStrategyOutput { Success = true, Id = dto.Id, Name = dto.Title, Compiled = true };
        }
        catch (Exception ex)
        {
            return new CreateYamlStrategyOutput { Error = ex.Message };
        }
    }
}
