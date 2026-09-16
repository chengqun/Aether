using Aether.AI.Core.Agents;
using Aether.AI.Harness.Agents;
using Acme.Notes.Domain.Agents;
using Microsoft.Agents.AI;

namespace Acme.Agents.AI.Evals;

/// <summary>
/// <see cref="ICustomAgentEvalRunner"/> 实现：扫 labs <c>evals/*.eval.yaml</c>，跑指向指定 agent 的全部套件。
/// agent 经 <see cref="AgentFactory"/> 按 catalog 定义构建一次、跨套件复用（每 query 独立消息列表，无跨 query
/// 状态）；逐 case 检查走 <c>LocalEvaluator</c> + <see cref="EvalCaseRecorder"/> 闭包（零云端零 judge 模型，
/// 规则 agent 可确定性评测）。labs 根经委托注入（镜像 <c>DirectoryAgentCatalog</c>——适配器不引 apps 层，
/// 桌面宿主绑 <c>IKnowledgeDirectoryProvider</c>）。
/// </summary>
public sealed class AgentEvalRunner : ICustomAgentEvalRunner
{
    private static readonly EvalSourceParser _parser = new();

    private readonly Func<string?> _labsRootProvider;
    private readonly ICustomAgentCatalog _catalog;
    private readonly AgentFactory _agentFactory;

    public AgentEvalRunner(Func<string?> labsRootProvider, ICustomAgentCatalog catalog, AgentFactory agentFactory)
    {
        _labsRootProvider = labsRootProvider;
        _catalog = catalog;
        _agentFactory = agentFactory;
    }

    public async Task<AgentEvalRunResult> RunAsync(Guid agentId, CancellationToken cancellationToken = default)
    {
        var def = await _catalog.GetAsync(agentId).ConfigureAwait(false);
        if (def is null)
            return new AgentEvalRunResult("(未知)", [], $"找不到 agent：{agentId}（先保存再评测）");

        var labsRoot = _labsRootProvider();
        if (string.IsNullOrWhiteSpace(labsRoot))
            return new AgentEvalRunResult(def.Name, [], "labs 根目录未配置（先在资料库打开 labs 目录）");

        var evalsDir = Path.Combine(labsRoot, "evals");
        var files = Directory.Exists(evalsDir)
            ? Directory.EnumerateFiles(evalsDir, "*.eval.yaml")
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList()
            : [];

        if (files.Count == 0)
            return new AgentEvalRunResult(def.Name, []); // 无评测文件：HasSuites=false → UI 提示「暂无评测文件」

        AIAgent? agent = null;
        var suites = new List<AgentEvalSuiteResult>();
        foreach (var file in files)
        {
            var rel = Path.GetRelativePath(labsRoot, file);
            EvalDefinition? evalDef = null;
            AgentEvalSuiteResult suite;
            try
            {
                var source = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
                if (!_parser.TryParse(source, out evalDef, out var parseError))
                {
                    // 解析失败拿不到 agentId 无法归因到 agent——按错误套件上报（文件名当套件名）
                    suite = new AgentEvalSuiteResult(Path.GetFileName(file), null, rel, [], $"解析失败：{parseError}");
                }
                else if (evalDef!.AgentId != agentId)
                {
                    continue; // 指向其它 agent 的评测文件——静默跳过
                }
                else
                {
                    agent ??= _agentFactory.Create(CustomAgentRunner.ToAgentDefinition(agentId.ToString(), def));
                    suite = await RunSuiteAsync(agent, evalDef, file, labsRoot, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                var name = evalDef?.Name ?? Path.GetFileName(file);
                suite = new AgentEvalSuiteResult(name, evalDef?.Description, rel, [], $"运行失败：{ex.Message}");
            }

            suites.Add(suite);
        }

        return new AgentEvalRunResult(def.Name, suites);
    }

    private static async Task<AgentEvalSuiteResult> RunSuiteAsync(
        AIAgent agent, EvalDefinition evalDef, string file, string labsRoot, CancellationToken ct)
    {
        var recorder = new EvalCaseRecorder(evalDef.Cases);
        var evaluator = new LocalEvaluator(recorder.BuildChecks());

        // EvaluateAsync 逐 query 建新消息列表跑 agent（无 session 依赖，规则 agent 兼容）；
        // 返回的 AgentEvaluationResults 刻意不读——判定记录以 recorder 为权威
        _ = await agent.EvaluateAsync(
            evalDef.Cases.Select(c => c.Query).ToList(),
            evaluator,
            evalName: evalDef.Name,
            cancellationToken: ct).ConfigureAwait(false);

        var cases = recorder.GetResults()
            .Select(r => new AgentEvalCaseResult(r.Query, Truncate(r.Response, 400), r.Passed, r.Checks))
            .ToList();
        return new AgentEvalSuiteResult(evalDef.Name, evalDef.Description, Path.GetRelativePath(labsRoot, file), cases);
    }

    /// <summary>回复摘录截断（报告可读性）。</summary>
    private static string? Truncate(string? text, int max)
        => string.IsNullOrEmpty(text) || text.Length <= max ? text : text[..max] + "…";
}
