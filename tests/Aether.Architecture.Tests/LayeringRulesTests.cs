namespace Aether.Architecture.Tests;

/// <summary>
/// CLAUDE.md「DDD layering rules」的机器强制。每条规则对应文档一节；例外白名单与文档化例外一一对应——
/// 新增例外 = 同时改这里的白名单 + CLAUDE.md 文档，二者不允许只改一处（这正是防止「例外悄悄累积」的闸门）。
/// </summary>
public class LayeringRulesTests
{
    private static readonly RepoModel Model = RepoModel.Load();

    // ────────────────────────── 例外白名单（与 CLAUDE.md 文档化例外一一对应） ──────────────────────────

    /// <summary>business/ → Aether.AI.* 的唯一合法出口：MCP 契约上移 Abstractions 后 McpClient 消费 IMcpServerRegistry（CLAUDE.md「AppAutomation context」节）。</summary>
    private static readonly (string Project, string Target)[] BusinessToPlatformWhitelist =
    {
        ("Acme.AppAutomation.McpClient", "Aether.AI.Abstractions"),
    };

    /// <summary>ai/ 适配器引用非 Application business 项目的文档化例外（CLAUDE.md「AI adapter convention」节）。</summary>
    private static readonly (string Project, string Target)[] AdapterToBusinessWhitelist =
    {
        // agent bridge（非 tool adapter 形状）：契约在稳定层 Notes.Domain.Agents / Aether.AI.Core.Agents / Abstractions
        ("Acme.Agents.AI", "Acme.Notes.Domain"),
        // CreateStrategyTool 需要 StrategyScriptCompiler 即时编译验证 + Trading.Runtime.Domain 运行时端口/IStrategyNoteSource
        ("Acme.Trading.AI", "Acme.Trading.Scripting"),
        ("Acme.Trading.AI", "Acme.Trading.Runtime.Domain"),
    };

    /// <summary>跨上下文「引擎桥」白名单（CLAUDE.md DDD layering rules 节）：前置（CtpFront）→ 交易所核心
    /// （CtpExchange 的 Simulator，Tier=Other）。前置是交易所之上的线协议翻译层，进程内直调核心门面
    /// （CtpFrontSession ↔ SimTradingCore）——2026-10-04 客户端/交易所/前置三上下文三分后的合法引用形态。</summary>
    private static readonly (string Project, string Target)[] CrossContextEngineBridgeWhitelist =
    {
        ("Acme.Ctp.Front", "Acme.Ctp.Simulator"),
    };

    /// <summary>允许引用 externals/ 的项目：Ctp.Net.Core（native 包装）唯一消费方 + Lean 引擎唯一桥接层（CLAUDE.md「Trading + Lean」节）
    /// + ProtoCapture（CTP 协议抓包/直连验收工具——全部用途就是驱动官方 SDK，见 CLAUDE.md「CTP 线协议前置」节）
    /// + Acme.Ctp.Front.Tests（CTP 前置 E2E——官方 SDK 客户端 CtpTrader 即裁判，同上节）。</summary>
    private static readonly HashSet<string> ExternalsWhitelist = new(StringComparer.Ordinal)
    {
        "business/Ctp/Acme.Ctp.Infrastructure/Acme.Ctp.Infrastructure.csproj",
        "business/Trading.Runtime/Acme.Trading.Runtime.Lean/Acme.Trading.Runtime.Lean.csproj",
        "apps/CtpSim/Acme.Ctp.ProtoCapture/Acme.Ctp.ProtoCapture.csproj",
        "tests/Acme.Ctp.Front.Tests/Acme.Ctp.Front.Tests.csproj",
    };

    // ────────────────────────── business/ 层规则 ──────────────────────────

    [Fact]
    public void BusinessDomainIsPure()
    {
        var violations = new List<string>();
        foreach (var p in Model.Projects.Where(p => p.Layer == "business" && p.Tier == "Domain"))
        {
            foreach (var r in p.ProjectRefs)
            {
                var t = Model.Resolve(r);
                if (t is null || t.Layer != "business" || t.Tier != "Domain")
                    violations.Add($"{p.RelativePath} → {r}（Domain 只能引用本/外上下文的 Domain——共享内核）");
            }
            foreach (var pkg in p.PackageRefs)
                violations.Add($"{p.RelativePath} 引用包 {pkg}（Domain 零包）");
        }

        Assert.True(violations.Count == 0,
            "违反「Domain = 纯：零包、ProjectReference 仅限 Domain」（CLAUDE.md DDD layering rules）：\n  " +
            string.Join("\n  ", violations));
    }

    [Fact]
    public void BusinessNeverLeaksOutsideBusinessAndPlatform()
    {
        var violations = new List<string>();
        foreach (var p in Model.Projects.Where(p => p.Layer == "business"))
        {
            foreach (var r in p.ProjectRefs)
            {
                if (r.StartsWith("externals/", StringComparison.Ordinal))
                    continue; // 由 ExternalsOnlyThroughWhitelistedProjects 管辖

                var t = Model.Resolve(r);
                if (t is null)
                {
                    violations.Add($"{p.RelativePath} → {r}（目标不在仓库一方项目内）");
                    continue;
                }
                if (t.Layer is not ("business" or "src"))
                {
                    violations.Add($"{p.RelativePath} → {r}（business 只能引用 business/ 或 src/，不得引 {t.Layer}/）");
                    continue;
                }
                if (t.Layer == "src" && t.Name.StartsWith("Aether.AI.", StringComparison.Ordinal))
                {
                    var whitelisted = BusinessToPlatformWhitelist.Any(w => w.Project == p.Name && w.Target == t.Name);
                    if (!whitelisted)
                        violations.Add(
                            $"{p.RelativePath} → {t.RelativePath}（business 不得引 Aether.AI.*；唯一合法出口见 BusinessToPlatformWhitelist。若确属新例外：白名单+CLAUDE.md 两处同步改）");
                }
                // src/Acme.Persistence（共享 SQLite 基类）合法，不在此限制
            }
        }

        Assert.True(violations.Count == 0,
            "违反「business/ 不得越出 business + 平台层」（CLAUDE.md DDD layering rules）：\n  " +
            string.Join("\n  ", violations));
    }

    [Fact]
    public void ApplicationOnlyReferencesDomains()
    {
        var violations = new List<string>();
        foreach (var p in Model.Projects.Where(p => p.Layer == "business" && p.Tier == "Application"))
        {
            foreach (var r in p.ProjectRefs)
            {
                var t = Model.Resolve(r);
                if (t is null || t.Layer != "business" || t.Tier != "Domain")
                    violations.Add($"{p.RelativePath} → {r}（Application 只能引本/外上下文 Domain，永不引 Infrastructure/AI/externals）");
            }
        }

        Assert.True(violations.Count == 0,
            "违反「Application → Domain (+ 外域 Domain port)」（CLAUDE.md DDD layering rules）：\n  " +
            string.Join("\n  ", violations));
    }

    /// <summary>Infrastructure 及同语境重项目（Scripting/Http/Bridge/McpClient 等）不得跨上下文引用非 Domain 项目。</summary>
    [Fact]
    public void BusinessHeavierTiersStayInsideTheirContext()
    {
        var violations = new List<string>();
        foreach (var p in Model.Projects.Where(p => p.Layer == "business" && p.Tier is "Infrastructure" or "Other"))
        {
            foreach (var r in p.ProjectRefs)
            {
                var t = Model.Resolve(r);
                if (t?.Layer != "business")
                    continue; // 跨层引用由其它规则管辖

                if (t.Context == p.Context)
                    continue; // 本上下文任意项目
                if (t.Tier == "Domain")
                    continue; // 外域 Domain 契约（共享内核）
                if (CrossContextEngineBridgeWhitelist.Any(w => w.Project == p.Name && w.Target == t.Name))
                    continue; // 引擎桥白名单（前置 → 交易所核心，进程内协议翻译直调）

                violations.Add(
                    $"{p.RelativePath} → {t.RelativePath}（跨上下文只允许引外域 Domain；不得引外上下文 Application/Infrastructure/其它）");
            }
        }

        Assert.True(violations.Count == 0,
            "违反「Infrastructure → Domain (+ externals + 外域 Domain contract)，永不引外上下文 Infrastructure」（CLAUDE.md DDD layering rules）：\n  " +
            string.Join("\n  ", violations));
    }

    // ────────────────────────── src/ 平台层规则 ──────────────────────────

    [Fact]
    public void PlatformNeverReferencesDownward()
    {
        var violations = new List<string>();
        foreach (var p in Model.Projects.Where(p => p.Layer == "src"))
        {
            foreach (var r in p.ProjectRefs)
            {
                var t = Model.Resolve(r);
                if (t is not null && t.Layer != "src")
                    violations.Add($"{p.RelativePath} → {r}（平台层只引 src/ 内部，永不引 business/ai/apps/server）");
            }
        }

        Assert.True(violations.Count == 0,
            "违反「Platform source never references business/ or ai/」（CLAUDE.md 顶层布局节）：\n  " +
            string.Join("\n  ", violations));
    }

    /// <summary>契约包 Abstractions（零 ProjectReference）与 Core（零依赖纯 record）的最小性锁。</summary>
    [Fact]
    public void PlatformContractPackagesStayMinimal()
    {
        var abstractions = Model.Projects.Single(p => p.Name == "Aether.AI.Abstractions");
        Assert.Empty(abstractions.ProjectRefs);
        var allowedPkgs = new HashSet<string>(StringComparer.Ordinal) { "Microsoft.Extensions.AI.Abstractions" };
        Assert.True(abstractions.PackageRefs.All(allowedPkgs.Contains),
            $"Aether.AI.Abstractions 只允许 {string.Join("/", allowedPkgs)}，实际：{string.Join(",", abstractions.PackageRefs)}");

        var core = Model.Projects.Single(p => p.Name == "Aether.AI.Core");
        Assert.Empty(core.ProjectRefs);
        Assert.Empty(core.PackageRefs);
    }

    /// <summary>Workflows 与 Harness 平级（CLAUDE.md：peer layer，does NOT depend on Harness）。</summary>
    [Fact]
    public void WorkflowsStaysPeerOfHarness()
    {
        var workflows = Model.Projects.Single(p => p.Name == "Aether.AI.Workflows");
        var harnessRefs = workflows.ProjectRefs.Where(r => Model.Resolve(r)?.Name == "Aether.AI.Harness").ToList();
        Assert.Empty(harnessRefs);
    }

    // ────────────────────────── ai/ 适配器规则 ──────────────────────────

    [Fact]
    public void AiAdaptersKeepTheToolAdapterShape()
    {
        var violations = new List<string>();
        foreach (var p in Model.Projects.Where(p => p.Layer == "ai"))
        {
            foreach (var r in p.ProjectRefs)
            {
                if (r.StartsWith("externals/", StringComparison.Ordinal))
                {
                    violations.Add($"{p.RelativePath} → {r}（适配器不引 externals）");
                    continue;
                }

                var t = Model.Resolve(r);
                if (t is null)
                {
                    violations.Add($"{p.RelativePath} → {r}（目标不在仓库一方项目内）");
                    continue;
                }
                switch (t.Layer)
                {
                    case "src":
                        if (!t.Name.StartsWith("Aether.AI.", StringComparison.Ordinal))
                            violations.Add($"{p.RelativePath} → {r}（src 侧只允许引 Aether.AI.*）");
                        break;
                    case "business":
                        if (t.Tier != "Application" &&
                            !AdapterToBusinessWhitelist.Any(w => w.Project == p.Name && w.Target == t.Name))
                            violations.Add(
                                $"{p.RelativePath} → {t.RelativePath}（适配器只引上下文 Application；例外见 AdapterToBusinessWhitelist。若确属新例外：白名单+CLAUDE.md 两处同步改）");
                        break;
                    default:
                        violations.Add($"{p.RelativePath} → {r}（适配器不得引 {t.Layer}/ 层）");
                        break;
                }
            }
        }

        Assert.True(violations.Count == 0,
            "违反「AI adapter convention：Hosting + 上下文 Application（例外见白名单）」（CLAUDE.md ai/ 节）：\n  " +
            string.Join("\n  ", violations));
    }

    // ────────────────────────── server/ + mcp/ 隔离规则 ──────────────────────────

    [Fact]
    public void ServerIsSelfContained()
    {
        var violations = Model.Projects.Where(p => p.Layer == "server")
            .SelectMany(p => p.ProjectRefs
                .Where(r => Model.Resolve(r) is { } t && t.Layer != "server")
                .Select(r => $"{p.RelativePath} → {r}（server/ 自包含：不引 src/business/ai/apps）"))
            .ToList();

        Assert.True(violations.Count == 0,
            "违反「server/ 是自包含 ABP 层，不属于 DDD/AI 分层」（CLAUDE.md Server context 节）：\n  " +
            string.Join("\n  ", violations));
    }

    [Fact]
    public void McpServersAreStandalone()
    {
        var violations = Model.Projects.Where(p => p.Layer == "mcp")
            .SelectMany(p => p.ProjectRefs
                .Where(r => Model.Resolve(r) is not null)
                .Select(r => $"{p.RelativePath} → {r}（mcp/ 自写 server 不引用 Aether 一方项目）"))
            .ToList();

        Assert.True(violations.Count == 0,
            "违反「mcp/ 放自写 MCP server 源码、不引用 Aether」（CLAUDE.md Agent 扩展能力节）：\n  " +
            string.Join("\n  ", violations));
    }

    [Fact]
    public void ExternalsOnlyThroughWhitelistedProjects()
    {
        var violations = Model.Projects
            .SelectMany(p => p.ProjectRefs
                .Where(r => r.StartsWith("externals/", StringComparison.Ordinal))
                .Select(r => (p, r)))
            .Where(x => !ExternalsWhitelist.Contains(x.p.RelativePath))
            .Select(x => $"{x.p.RelativePath} → {x.r}（externals 只经白名单项目进入：Ctp.Infrastructure（Ctp.Net.Core）/ Trading.Runtime.Lean（Lean 引擎桥）/ ProtoCapture 与 Front.Tests（官方 SDK 驱动），全表见 ExternalsWhitelist）")
            .ToList();

        Assert.True(violations.Count == 0,
            "违反「externals 只经白名单项目进入」（CLAUDE.md externals 节）：\n  " +
            string.Join("\n  ", violations));
    }

    // ────────────────────────── 全局结构规则 ──────────────────────────

    [Fact]
    public void DependencyGraphIsAcyclic()
    {
        const int white = 0, gray = 1, black = 2;
        var color = new Dictionary<string, int>(StringComparer.Ordinal);
        var stack = new List<string>();
        List<string>? cycle = null;

        void Dfs(string path)
        {
            color[path] = gray;
            stack.Add(path);
            foreach (var r in Model.Resolve(path)!.ProjectRefs)
            {
                if (!Model.ByPath.ContainsKey(r))
                    continue; // externals/缺失目标不参与环检测
                if (color.GetValueOrDefault(r) == gray)
                {
                    cycle = stack.SkipWhile(s => !string.Equals(s, r, StringComparison.Ordinal)).Append(r).ToList();
                    return;
                }
                if (color.GetValueOrDefault(r) == white)
                {
                    Dfs(r);
                    if (cycle is not null)
                        return;
                }
            }
            stack.RemoveAt(stack.Count - 1);
            color[path] = black;
        }

        foreach (var p in Model.Projects)
        {
            if (cycle is not null)
                break;
            if (color.GetValueOrDefault(p.RelativePath) == white)
                Dfs(p.RelativePath);
        }

        Assert.True(cycle is null,
            "ProjectReference 依赖图出现环（DDD 要求 DAG 无环）：\n  " + string.Join(" → ", (IEnumerable<string>?)cycle ?? Array.Empty<string>()));
    }

    [Fact]
    public void TestProjectsLiveOnlyInTestsDirectory()
    {
        var violations = Model.Projects
            .Where(p => p.Name.EndsWith(".Tests", StringComparison.Ordinal) && p.Layer != "tests")
            .Select(p => $"{p.RelativePath}（测试项目只能住 tests/，不得与源码混放）")
            .ToList();

        Assert.True(violations.Count == 0,
            "违反「tests/ — all test projects live here, never interleaved with source」（CLAUDE.md 顶层布局节）：\n  " +
            string.Join("\n  ", violations));
    }

    // ────────────────────────── UI 无关 VM 库规则 ──────────────────────────

    [Theory]
    [InlineData("Acme.Desktop.Mvvm", "Avalonia")]
    [InlineData("Acme.Mobile.Mvvm", "Microsoft.Maui")]
    public void ViewModelLibrariesStayUiAgnostic(string projectName, string uiNamespace)
    {
        var p = Model.Projects.Single(x => x.Name == projectName);

        var pkgOffenders = p.PackageRefs.Where(pkg => pkg.StartsWith(uiNamespace, StringComparison.Ordinal)).ToList();
        Assert.True(pkgOffenders.Count == 0,
            $"{projectName} 引入了 UI 包 {string.Join(",", pkgOffenders)}——VM 库必须零 UI 依赖（UI 无关 seam）");

        var projectDir = Path.Combine(Model.Root, Path.GetDirectoryName(p.RelativePath)!);
        var sourceOffenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(projectDir, "*.cs", SearchOption.AllDirectories))
        {
            var relInProject = Path.GetRelativePath(projectDir, file);
            var segs = relInProject.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (segs.Any(s => s is "bin" or "obj"))
                continue;
            foreach (var line in File.ReadLines(file))
            {
                if (line.TrimStart().StartsWith($"using {uiNamespace}", StringComparison.Ordinal))
                    sourceOffenders.Add($"{relInProject}: {line.Trim()}");
            }
        }

        Assert.True(sourceOffenders.Count == 0,
            $"{projectName} 源码出现 `using {uiNamespace}…`（VM 库零 UI 框架引用；需要 UI 类型时说明该代码应下沉到 Shared/视图层）：\n  " +
            string.Join("\n  ", sourceOffenders));
    }

    [Theory]
    [InlineData("Acme.Desktop.Mvvm")]
    [InlineData("Acme.Mobile.Mvvm")]
    public void ViewModelLibrariesReferenceOnlyDomainAndApplication(string projectName)
    {
        var p = Model.Projects.Single(x => x.Name == projectName);
        var violations = p.ProjectRefs
            .Select(r => (r, t: Model.Resolve(r)))
            .Where(x => x.t?.Layer == "business" && x.t!.Tier is not ("Domain" or "Application"))
            .Select(x => $"{projectName} → {x.r}（VM 库只消费 Domain/Application 契约，不拖 Infrastructure——Infrastructure 由宿主 composition root 装配）")
            .ToList();

        Assert.True(violations.Count == 0,
            "违反「UI-agnostic VM 库零 Infrastructure 引用」（CLAUDE.md Desktop/Mobile architecture 节）：\n  " +
            string.Join("\n  ", violations));
    }
}
