using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Acme.Desktop.Mvvm;
using Xunit;

namespace Acme.Desktop.Mvvm.Tests;

/// <summary>
/// 资料库分区切换器单测：labs 根下固定类型目录的 ‹ › 横向切换（树只显当前分区，分区目录提升为树根）。
/// 覆盖：分区列表计算（固定顺序/存在性/租户过滤）、初始分区回退链、打开即提升（RootPath 恒为 labs 根——
/// AI 工具/类型解析/新建落盘都依赖它）、切换选中分区根 + 持久化 + wrap-around、跨分区展开记忆、
/// 非 labs 根（自定义知识库）隐藏切换器且全树不变、持久化分区恢复、跨分区打开文件联动（先切分区再选中）。
/// shell 用全 throw 桩——分区测试只选目录/开 McpServer json（非 form 类型不触 shell）。
/// </summary>
public class KnowledgeObjectsScopeTests
{
    // —— stub 依赖（镜像 ChatTargetPoolRefreshTests.FakeDispatcher，可独立构造 KOVM）——

    private sealed class FakeDispatcher : IUiDispatcher
    {
        public Task InvokeAsync(Action action) { action(); return Task.CompletedTask; }
        public void Post(Action action) => action();
    }

    /// <summary>内存偏好存储（Task.FromResult 同步完成，镜像 JsonPreferencesStore 构造期语义）。</summary>
    private sealed class InMemoryPreferencesStore : IPreferencesStore
    {
        public readonly Dictionary<string, string?> Values = new();

        public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
            => Task.FromResult(Values.TryGetValue(key, out var v) ? v : null);

        public Task SetAsync(string key, string? value, CancellationToken cancellationToken = default)
        {
            Values[key] = value;
            return Task.CompletedTask;
        }
    }

    /// <summary>全 throw 的 shell 宿主——分区测试不应触达（触达即失败信号）。</summary>
    private sealed class ThrowingShellHost : IShellHost
    {
        public ExpertsViewModel Experts => throw new InvalidOperationException("shell not expected in scope tests");
        public WorkflowsViewModel Workflows => throw new InvalidOperationException("shell not expected in scope tests");
        public RulesViewModel Rules => throw new InvalidOperationException("shell not expected in scope tests");
    }

    private sealed class TestServiceProvider : IServiceProvider
    {
        private readonly Dictionary<Type, object?> _services;

        public TestServiceProvider(IUiDispatcher ui, IPreferencesStore store)
            => _services = new() { [typeof(IUiDispatcher)] = ui, [typeof(IPreferencesStore)] = store };

        public object? GetService(Type serviceType) => _services.TryGetValue(serviceType, out var s) ? s : null;
    }

    private static (KnowledgeObjectsViewModel vm, InMemoryPreferencesStore store) CreateVm(string tenant = "qifu")
    {
        var store = new InMemoryPreferencesStore();
        // DefaultRoot=null → ctor 的 LoadLastRootWithDefaultAsync fire-and-forget 无持久化目录时 no-op（无背景噪音）。
        var provider = new KnowledgeDirectoryProvider { DefaultRoot = null };
        var vm = new KnowledgeObjectsViewModel(
            new TestServiceProvider(new FakeDispatcher(), store), provider, new ThrowingShellHost(), tenant);
        return (vm, store);
    }

    /// <summary>建临时 labs 根：每项 (子目录名, 文件名?, 内容?)；file 为 null 只建目录。</summary>
    private static string TempRoot(params (string dir, string? file, string? content)[] entries)
    {
        var root = Path.Combine(Path.GetTempPath(), "ko-scope-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        foreach (var (dir, file, content) in entries)
        {
            Directory.CreateDirectory(Path.Combine(root, dir));
            if (file is not null)
                File.WriteAllText(Path.Combine(root, dir, file), content ?? string.Empty);
        }
        return root;
    }

    private static void Cleanup(KnowledgeObjectsViewModel vm, string root)
    {
        vm.CloseProjectCommand.Execute(null); // 停 watcher + 取消挂起重载/重建
        try { Directory.Delete(root, recursive: true); }
        catch { /* 临时目录清理失败忽略（watcher 事件竞态） */ }
    }

    /// <summary>等 ActivePath 就位（OnSelectedNodeChanged → OpenFileAsync 是 fire-and-forget，读盘异步）。</summary>
    private static async Task WaitActivePathAsync(KnowledgeObjectsViewModel vm, string expected, int timeoutMs = 2000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline
               && !string.Equals(vm.ActivePath, expected, StringComparison.OrdinalIgnoreCase))
            await Task.Delay(20);
    }

    // —— 纯函数 ——

    [Fact]
    public void ComputeScopeDirNames_OrdersFiltersByExistenceAndTenant()
    {
        var root = TempRoot(
            ("rules", null, null),
            ("knowledge", null, null),
            ("agents", null, null),
            ("strategies", null, null),
            ("zzz-custom", null, null));
        try
        {
            // qifu：策略三目录排除、自建目录不进列表、固定顺序 knowledge→agents→rules
            var qifu = KnowledgeObjectsViewModel.ComputeScopeDirNames(root, hasTradingTypes: false);
            Assert.Equal(new[] { "knowledge", "agents", "rules" }, qifu);

            // stockapp：策略目录按固定顺序附后
            var stockapp = KnowledgeObjectsViewModel.ComputeScopeDirNames(root, hasTradingTypes: true);
            Assert.Equal(new[] { "knowledge", "agents", "rules", "strategies" }, stockapp);

            // 空根 → 空列表（非 labs 根）
            Assert.Empty(KnowledgeObjectsViewModel.ComputeScopeDirNames(root + "-missing", hasTradingTypes: false));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void ResolveInitialScopeDirName_FallbackChain()
    {
        var scopes = new[] { "knowledge", "agents", "rules" };
        Assert.Equal("agents", KnowledgeObjectsViewModel.ResolveInitialScopeDirName(scopes, "agents")); // 持久化命中
        Assert.Equal("agents", KnowledgeObjectsViewModel.ResolveInitialScopeDirName(scopes, " AGENTS ")); // trim + 忽略大小写
        Assert.Equal("knowledge", KnowledgeObjectsViewModel.ResolveInitialScopeDirName(scopes, "bogus")); // 假值回退 knowledge
        Assert.Equal("agents", KnowledgeObjectsViewModel.ResolveInitialScopeDirName(new[] { "agents", "rules" }, null)); // 无 knowledge → 首个
        Assert.Null(KnowledgeObjectsViewModel.ResolveInitialScopeDirName(Array.Empty<string>(), "knowledge")); // 空列表
    }

    // —— 打开即挂分区子节点为树根集 ——

    [Fact]
    public async Task OpenFolder_MountsScopeChildrenAndKeepsLabsRootPath()
    {
        var root = TempRoot(
            ("knowledge", "a.md", "# a"),
            ("agents", "b.agent.md", "---\nname: b\n---\n"));
        var (vm, _) = CreateVm();
        try
        {
            await vm.OpenFolderAsync(root);

            Assert.True(vm.HasScopeSwitcher);
            Assert.Equal(root, vm.RootPath); // 铁律：RootPath 恒为 labs 根（AI 工具/类型解析/新建落盘依赖）
            var child = Assert.Single(vm.Roots); // 树=当前分区子节点（分区名在树头显示，无根层包装）
            Assert.Equal("a", child.Name);
            Assert.EndsWith("knowledge/a.md", child.Path, StringComparison.OrdinalIgnoreCase);
            Assert.False(child.IsFolder);
            Assert.Equal("knowledge", vm.CurrentScopeDirName);
            Assert.Equal("知识对象", vm.CurrentScopeDisplay);
        }
        finally { Cleanup(vm, root); }
    }

    // —— 切换：树/右栏直驱/持久化/wrap-around ——

    [Fact]
    public async Task SetScope_SwitchesTreeDrivesRightPaneAndPersists()
    {
        var root = TempRoot(
            ("knowledge", "a.md", "# a"),
            ("agents", "b.agent.md", "x"),
            ("rules", "c.rule.yaml", "x"));
        var (vm, store) = CreateVm();
        try
        {
            await vm.OpenFolderAsync(root);
            Assert.Equal("knowledge", vm.CurrentScopeDirName);

            await vm.SetScopeAsync("agents");
            Assert.Equal("agents", vm.CurrentScopeDirName);
            var b = Assert.Single(vm.Roots);
            Assert.Equal("b", b.Name);
            Assert.EndsWith("agents/b.agent.md", b.Path, StringComparison.OrdinalIgnoreCase);
            Assert.Null(vm.SelectedNode); // 分区根不在树中——不选中（避免 TreeView 双绑对非树节点回写 null）
            Assert.True(vm.IsFileCardsRoot); // 右栏经 ShowFolderContent 直驱卡片网格（不走 SelectedNode）
            Assert.Equal("agents", store.Values["ko.scope"]); // 持久化记住上次

            // wrap-around：knowledge(0) 点左 → rules(2)
            await vm.SetScopeAsync("knowledge");
            await vm.ScopeLeftCommand.ExecuteAsync(null);
            Assert.Equal("rules", vm.CurrentScopeDirName);
        }
        finally { Cleanup(vm, root); }
    }

    // —— 跨分区展开记忆 ——

    [Fact]
    public async Task SetScope_RemembersExpansionAcrossSwitches()
    {
        var root = TempRoot(("knowledge", null, null), ("agents", "b.agent.md", "x"));
        Directory.CreateDirectory(Path.Combine(root, "knowledge", "sub"));
        File.WriteAllText(Path.Combine(root, "knowledge", "sub", "s.md"), "# s");
        var (vm, _) = CreateVm();
        try
        {
            await vm.OpenFolderAsync(root);
            vm.Roots.Single(c => c.Name == "sub").IsExpanded = true; // sub 现为顶层节点

            await vm.SetScopeAsync("agents");
            Assert.Equal("b", Assert.Single(vm.Roots).Name);

            await vm.SetScopeAsync("knowledge");
            Assert.True(vm.Roots.Single(c => c.Name == "sub").IsExpanded); // 按路径命中恢复
        }
        finally { Cleanup(vm, root); }
    }

    // —— 非 labs 根（自定义知识库）：切换器隐藏、全树不变 ——

    [Fact]
    public async Task NonLabsRoot_HidesSwitcherAndShowsFullTree()
    {
        var root = TempRoot(("mynotes", "n.md", "# n"), ("knowledge", "k.md", "# k")); // 仅 1 个固定目录
        var (vm, _) = CreateVm();
        try
        {
            await vm.OpenFolderAsync(root);
            Assert.False(vm.HasScopeSwitcher);
            Assert.Equal(root, vm.Roots[0].Path); // 树根 = 根本身（不提升）
            var names = vm.Roots[0].Children.Select(c => c.Name).ToList();
            Assert.Contains("知识对象", names); // knowledge/ 照样走入口卡改名（树行为与今日一致）
            Assert.Contains("mynotes", names); // 自建目录照常显示（裸目录名）
        }
        finally { Cleanup(vm, root); }
    }

    // —— 持久化分区恢复 ——

    [Fact]
    public async Task PersistedScopeRestoredOnOpen()
    {
        var root = TempRoot(
            ("knowledge", "a.md", "x"),
            ("agents", "b.agent.md", "x"),
            ("rules", "c.rule.yaml", "x"));

        var (vm, store) = CreateVm();
        store.Values["ko.scope"] = "agents";
        try
        {
            await vm.OpenFolderAsync(root);
            Assert.Equal("agents", vm.CurrentScopeDirName);
            Assert.Equal("b", Assert.Single(vm.Roots).Name); // 直接落在持久化分区的子节点
        }
        finally { Cleanup(vm, root); }

        var root2 = TempRoot( // Cleanup 已删 root，第二段用全新根
            ("knowledge", "a.md", "x"),
            ("agents", "b.agent.md", "x"),
            ("rules", "c.rule.yaml", "x"));
        var (vm2, store2) = CreateVm();
        store2.Values["ko.scope"] = "bogus"; // 已不存在的分区 → 回退 knowledge
        try
        {
            await vm2.OpenFolderAsync(root2);
            Assert.Equal("knowledge", vm2.CurrentScopeDirName);
        }
        finally { Cleanup(vm2, root2); }
    }

    // —— 跨分区打开文件联动：先切分区再选中目标文件 ——

    [Fact]
    public async Task OpenKnowledgeByPath_SwitchesScopeFirstThenSelectsFile()
    {
        var root = TempRoot(("knowledge", "a.md", "# a"), ("mcp-servers", "foo.json", "{}"));
        var (vm, _) = CreateVm();
        try
        {
            await vm.OpenFolderAsync(root);
            Assert.Equal("knowledge", vm.CurrentScopeDirName);

            var file = Path.Combine(root, "mcp-servers", "foo.json");
            await vm.OpenKnowledgeByPathAsync(file);
            Assert.Equal("mcp-servers", vm.CurrentScopeDirName); // 先切分区（树可见该文件）
            Assert.Equal("foo", Assert.Single(vm.Roots).Name);
            Assert.Equal(file, vm.SelectedNode?.Path); // 树选中目标文件（同步高亮）
            await WaitActivePathAsync(vm, file); // OnSelectedNodeChanged → OpenFileAsync fire-and-forget 读盘
            Assert.Equal(file, vm.ActivePath); // 文件已打开
        }
        finally { Cleanup(vm, root); }
    }

    // —— 空分区：挂空树（合法状态）且不锁死切换 ——

    [Fact]
    public async Task EmptyScope_MountsEmptyTreeAndCanSwitchAway()
    {
        var root = TempRoot(("knowledge", "a.md", "x"), ("workflows", null, null), ("rules", "c.rule.yaml", "x"));
        var (vm, _) = CreateVm();
        try
        {
            await vm.OpenFolderAsync(root);
            Assert.Single(vm.Roots); // knowledge 有内容

            await vm.SetScopeAsync("workflows"); // 空分区
            Assert.True(vm.HasScopeSwitcher);
            Assert.Empty(vm.Roots); // 空分区=空树（合法状态，不是「未打开」——不能当作丢弃守卫）

            await vm.SetScopeAsync("rules"); // 能切走（空树不锁死切换）
            Assert.Equal("rules", vm.CurrentScopeDirName);
            Assert.Equal("c", Assert.Single(vm.Roots).Name);
        }
        finally { Cleanup(vm, root); }
    }
}
