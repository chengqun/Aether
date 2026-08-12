using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using Acme.Notes.Application;
using Acme.Notes.Domain;
using Acme.Trading.Runtime.Domain;

namespace Acme.Trading.AI.Tools;

/// <summary>
/// 创建 Lean 算法策略工具：把 AI 写的 C# 算法源码存为 LeanAlgorithm 笔记（NodeType=LeanAlgorithm, syntax=csharp），
/// 落库前经 <see cref="ILeanAlgorithmScriptingService.TryCompile"/> 即时编译验证返回错误（前馈传感器——AI 据此在落库前
/// 修订源码，避免笔记里堆坏代码）。与 <see cref="CreateStrategyTool"/>（IStrategy 路径）平行，区别：编译验证经 Lean 中立端口
/// （<see cref="ILeanAlgorithmScriptingService"/>，adapter 不引 Trading.Runtime.Lean，保持 DDD 分层）。注册由 NotesChanged
/// 防抖触发的 <c>LeanAlgorithmScriptingService.LoadAsync</c> 幂等完成（回测页 Lean 引擎选择器随后自动出现）。
/// </summary>
public class CreateLeanAlgorithmTool
{
    private readonly INoteService _notes;
    private readonly ILeanAlgorithmScriptingService _lean;

    public CreateLeanAlgorithmTool(INoteService notes, ILeanAlgorithmScriptingService lean)
    {
        _notes = notes;
        _lean = lean;
    }

    public class CreateLeanAlgorithmOutput
    {
        public bool Success { get; set; }
        public string? Id { get; set; }
        public string? Name { get; set; }
        /// <summary>源码能否编译通过（true=已存为笔记，等 LoadAsync 注册后可回测；false=看 CompileError 修改）。</summary>
        public bool Compiled { get; set; }
        public string? CompileError { get; set; }
        public string? Error { get; set; }
    }

    [Description("创建一个 Lean 算法策略（C# 源码笔记，继承 LeanAlgorithmBase=QCAlgorithm，复用 Lean 全部能力：原生指标链/Portfolio/SetHoldings/Schedule/OnEndOfDay/History/多标的/Short/部分仓位）。创建后回测页切「Lean算法」引擎即可选用于回测。源码须：using QuantConnect; using QuantConnect.Algorithm; using QuantConnect.Data; using QuantConnect.Indicators; using Acme.Trading.Domain; using Acme.Trading.Runtime.Lean; 并继承 LeanAlgorithmBase。Initialize 里调 ConfigureFromConfig() 读 start/end/cash/symbol，用 AddCtpData(ticker, Resolution.Daily) 订阅本地 CtpBar 数据（写 AddSecurity 拿不到本地数据）；OnData(Slice) 里用 SMA/SetHoldings/Liquidate 等 Lean 原生 API。可选 public static IReadOnlyList<StrategyParameterDef> ParameterDefinitions 声明参数供 UI 渲染。注意：Roslyn 编译无 ImplicitUsings，所需 using 须显式写出。同名算法已存在则更新其源码（幂等重试/修订不产生重复笔记）。")]
    public async Task<CreateLeanAlgorithmOutput> CreateLeanAlgorithm(
        [Description("算法名称（笔记标题，需唯一，回测时按此名选择）")] string name,
        [Description("C# 算法源码：继承 LeanAlgorithmBase 的类（无 front-matter，纯 C#）")] string source,
        [Description("父节点 id（可选，不传建到根节点下）")] string? parentId = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            return new CreateLeanAlgorithmOutput { Error = "名称不能为空" };
        name = name.Trim();

        // 先编译验证：不过不落库（前馈传感器）
        var (compiled, compileErr) = _lean.TryCompile(source, name);
        if (!compiled)
            return new CreateLeanAlgorithmOutput { Success = false, Compiled = false, CompileError = compileErr };

        try
        {
            // 同名算法已存在则更新内容（幂等）
            var existing = (await _notes.GetAllAsync(cancellationToken))
                .FirstOrDefault(n => string.Equals(n.Title, name, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                if (existing.NodeType != NoteNodeType.LeanAlgorithm)
                    return new CreateLeanAlgorithmOutput { Error = $"已存在同名笔记「{name}」（类型 {existing.NodeType}），请换名。" };
                await _notes.UpdateAsync(new UpdateNoteRequest
                {
                    Id = existing.Id,
                    Title = name,
                    Content = source,
                    Syntax = "csharp",
                    NodeType = NoteNodeType.LeanAlgorithm,
                }, cancellationToken);
                return new CreateLeanAlgorithmOutput { Success = true, Id = existing.Id, Name = name, Compiled = true };
            }

            var dto = await _notes.CreateAsync(new CreateNoteRequest
            {
                Title = name,
                Content = source,
                NodeType = NoteNodeType.LeanAlgorithm,
                Syntax = "csharp",
                ParentId = parentId,
            }, cancellationToken);

            return new CreateLeanAlgorithmOutput { Success = true, Id = dto.Id, Name = dto.Title, Compiled = true };
        }
        catch (Exception ex)
        {
            return new CreateLeanAlgorithmOutput { Error = ex.Message };
        }
    }
}
