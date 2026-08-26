using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using Acme.Trading.Runtime.Domain;

namespace Acme.Trading.AI.Tools;

/// <summary>
/// 创建 Lean 算法策略工具：把 AI 写的 C# 算法源码存为 LeanAlgorithm 文件（<c>labs/leanalgorithms/*.lean.cs</c>，纯 C#），
/// 落盘前经 <see cref="ILeanAlgorithmScriptingService.TryCompile"/> 即时编译验证返回错误（前馈传感器——AI 据此在落盘前
/// 修订源码）。与 <see cref="CreateStrategyTool"/>（IStrategy 路径）平行，区别：编译验证经 Lean 中立端口
/// （<see cref="ILeanAlgorithmScriptingService"/>，adapter 不引 Trading.Runtime.Lean）。落盘后 watcher 触发的
/// <c>LeanAlgorithmScriptingService.LoadAsync</c> 幂等注册（回测页 Lean 引擎选择器随后自动出现）。
/// 经 <see cref="ILeanAlgorithmNoteSource"/> 端口写文件（host 实现落盘 labs 目录）。
/// </summary>
public class CreateLeanAlgorithmTool
{
    private readonly ILeanAlgorithmNoteSource _leanNotes;
    private readonly ILeanAlgorithmScriptingService _lean;

    public CreateLeanAlgorithmTool(ILeanAlgorithmNoteSource leanNotes, ILeanAlgorithmScriptingService lean)
    {
        _leanNotes = leanNotes;
        _lean = lean;
    }

    public class CreateLeanAlgorithmOutput
    {
        public bool Success { get; set; }
        public string? Id { get; set; }
        public string? Name { get; set; }
        /// <summary>源码能否编译通过（true=已存为文件，等 LoadAsync 注册后可回测；false=看 CompileError 修改）。</summary>
        public bool Compiled { get; set; }
        public string? CompileError { get; set; }
        public string? Error { get; set; }
    }

    [Description("创建一个 Lean 算法策略（C# 源码文件，继承 LeanAlgorithmBase=QCAlgorithm，复用 Lean 全部能力：原生指标链/Portfolio/SetHoldings/Schedule/OnEndOfDay/History/多标的/Short/部分仓位）。创建后回测页切「Lean算法」引擎即可选用于回测。源码须：using QuantConnect; using QuantConnect.Algorithm; using QuantConnect.Data; using QuantConnect.Indicators; using Acme.Trading.Domain; using Acme.Trading.Runtime.Lean; 并继承 LeanAlgorithmBase。Initialize 里调 ConfigureFromConfig() 读 start/end/cash/symbol，用 AddCtpData(ticker, Resolution.Daily) 订阅本地 CtpBar 数据（写 AddSecurity 拿不到本地数据）；OnData(Slice) 里用 SMA/SetHoldings/Liquidate 等 Lean 原生 API。可选 public static IReadOnlyList<StrategyParameterDef> ParameterDefinitions 声明参数供 UI 渲染。注意：Roslyn 编译无 ImplicitUsings，所需 using 须显式写出。同名算法已存在则更新其源码（幂等）。")]
    public async Task<CreateLeanAlgorithmOutput> CreateLeanAlgorithm(
        [Description("算法名称（文件 stem，需唯一，回测时按此名选择）")] string name,
        [Description("C# 算法源码：继承 LeanAlgorithmBase 的类（无 front-matter，纯 C#）")] string source,
        [Description("父节点 id（可选，labs 版忽略）")] string? parentId = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            return new CreateLeanAlgorithmOutput { Error = "名称不能为空" };
        name = name.Trim();

        // 先编译验证：不过不落盘（前馈传感器）
        var (compiled, compileErr) = _lean.TryCompile(source, name);
        if (!compiled)
            return new CreateLeanAlgorithmOutput { Success = false, Compiled = false, CompileError = compileErr };

        try
        {
            var id = await _leanNotes.SaveAsync(name, source, cancellationToken);
            return new CreateLeanAlgorithmOutput { Success = true, Id = id, Name = name, Compiled = true };
        }
        catch (Exception ex)
        {
            return new CreateLeanAlgorithmOutput { Error = ex.Message };
        }
    }
}
