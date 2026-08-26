using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using Acme.Trading.Domain;
using Acme.Trading.Runtime.Domain;
using Acme.Trading.Scripting;

namespace Acme.Trading.AI.Tools;

/// <summary>
/// 创建交易策略工具：把 AI 写的 C# 策略源码存为 CodeStrategy 文件（<c>labs/codestrategies/*.cs</c>，纯 C#），
/// 立即编译验证返回错误（前馈传感器）。落盘后立即注册到 StrategyRegistry（复用 pre-flight 已编译 type，
/// 免目录 watcher 防抖延时），CreateStrategy 返回后调用方即可回测该策略。随后 watcher 防抖触发的
/// StrategyScriptingService.LoadAsync 重扫 adopt（幂等），回测页策略选择器自动出现（不需重启）。
/// 经 <see cref="IStrategyNoteSource"/> 端口写文件（host 实现落盘 labs 目录），不直接依赖 Notes.Application。
/// </summary>
public class CreateStrategyTool
{
    private readonly IStrategyNoteSource _strategies;

    public CreateStrategyTool(IStrategyNoteSource strategies) => _strategies = strategies;

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

    [Description("创建一个交易策略（C# 源码文件，实现 IStrategy）。创建后可立即用于回测。源码需 using Acme.Trading.Domain; 并实现 IStrategy 接口（WarmUpBars/Initialize/EvaluateSignal）。MarketBar 字段：Time/Open/High/Low/Close/Volume；SignalDirection：Hold/Long/Flat。同名策略已存在则更新其源码（幂等重试/修订不产生重复文件）。")]
    public async Task<CreateStrategyOutput> CreateStrategy(
        [Description("策略名称（文件 stem，需唯一，回测时按此名选择）")] string name,
        [Description("C# 策略源码：using Acme.Trading.Domain; + 一个 IStrategy 实现类（需无参构造）")] string source,
        [Description("父节点 id（可选，labs 版忽略）")] string? parentId = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            return new CreateStrategyOutput { Error = "名称不能为空" };
        name = name.Trim();

        // 先编译验证：不过不落盘——避免坏策略文件留在目录里、agent 重试又建一个产生重复。
        if (!StrategyScriptCompiler.TryCompile(source, out var instance, out var compileErr))
            return new CreateStrategyOutput { Success = false, Compiled = false, CompileError = compileErr };

        try
        {
            // 同名文件覆盖（SaveAsync 写固定路径 codestrategies/<name>.cs，幂等）。
            var id = await _strategies.SaveAsync(name, source, StrategyKind.Code, cancellationToken);

            // 立即注册到 StrategyRegistry（复用上面 pre-flight 已编译的 type），免 watcher 防抖延时。
            RegisterCompiled(name, instance);
            return new CreateStrategyOutput { Success = true, Id = id, Name = name, Compiled = true };
        }
        catch (Exception ex)
        {
            return new CreateStrategyOutput { Error = ex.Message };
        }
    }

    /// <summary>
    /// 把 pre-flight 已编译的 <paramref name="instance"/> 的 type 立即注册进 <see cref="StrategyRegistry"/>，
    /// 使该策略在 CreateStrategy 返回前即可被回测解析（跨过 StrategyScriptingService 的防抖延时）。
    /// 与 <c>StrategyScriptingService.LoadAsync</c> 的注册逻辑等价（同 type、同无参构造 factory）。
    /// </summary>
    private static void RegisterCompiled(string name, IStrategy instance)
    {
        var type = instance.GetType();
        StrategyRegistry.Register(name, () => (IStrategy)System.Activator.CreateInstance(type)!);
    }
}
