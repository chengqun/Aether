using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using Acme.Trading.Runtime.Domain;
using Acme.Trading.Scripting;

namespace Acme.Trading.AI.Tools;

/// <summary>
/// 创建声明式 YAML 交易策略：把 AI 写的 YAML 配方存为 Strategy 文件（<c>labs/strategies/*.strategy.yaml</c>），
/// 立即解析验证返回错误。落盘后 watcher → StrategyScriptingService 自动解析注册到 StrategyRegistry，
/// 回测页策略选择器自动出现。镜像 <see cref="CreateStrategyTool"/>（C# 版），校验器用
/// <see cref="StrategyProgramYaml.TryDeserialize(string,out Acme.Trading.Domain.StrategyProgram,out string?)"/>。
/// 经 <see cref="IStrategyNoteSource"/> 端口写文件（host 实现落盘 labs 目录）。
/// </summary>
public class CreateYamlStrategyTool
{
    private readonly IStrategyNoteSource _strategies;

    public CreateYamlStrategyTool(IStrategyNoteSource strategies) => _strategies = strategies;

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

    [Description("创建一个声明式 YAML 交易策略文件。schema: params(参数定义 key/label/default) + indicators(命名指标 {type,params}, type∈sma/ema/rsi/atr/macd/bollinger/stochastic, 参数值可含 {ParamKey} 模板) + rules(top-down 首匹配: signal∈Long/Flat/Hold + when 条件AST) + default(无匹配信号)。when 算子: gt/lt/gte/lte/cross_above/cross_below/and/or/not，叶子为指标名或数字。范例—params:[{key:FastPeriod,label:快线,default:5}]; indicators:{fast:{type:sma,period:\"{FastPeriod}\"},slow:{type:sma,period:\"{SlowPeriod}\"}}; rules:[{signal:Long,when:{cross_above:[fast,slow]}},{signal:Flat,when:{cross_below:[fast,slow]}}]; default:Hold。同名策略已存在则更新其源码。")]
    public async Task<CreateYamlStrategyOutput> CreateYamlStrategy(
        [Description("策略名称（文件 stem，需唯一，回测时按此名选择）")] string name,
        [Description("YAML 策略源码（params/indicators/rules/default 四段）")] string source,
        [Description("父节点 id（可选，labs 版忽略）")] string? parentId = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            return new CreateYamlStrategyOutput { Error = "名称不能为空" };
        name = name.Trim();

        // 先解析验证：不过不落盘——避免坏策略文件留在目录里。
        if (!StrategyProgramYaml.TryDeserialize(source, out _, out var parseErr))
            return new CreateYamlStrategyOutput { Success = false, Compiled = false, CompileError = parseErr };

        try
        {
            var id = await _strategies.SaveAsync(name, source, StrategyKind.Yaml, cancellationToken);
            // ScriptingService 随后经 watcher 自动解析注册到 StrategyRegistry，回测页选择器自动出现。
            return new CreateYamlStrategyOutput { Success = true, Id = id, Name = name, Compiled = true };
        }
        catch (Exception ex)
        {
            return new CreateYamlStrategyOutput { Error = ex.Message };
        }
    }
}
