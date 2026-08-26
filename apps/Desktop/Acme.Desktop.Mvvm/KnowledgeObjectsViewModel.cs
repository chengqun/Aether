using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Acme.Notes.Domain;
using Acme.Notes.Domain.Agents;
using Acme.Notes.Domain.Catalog;
using Acme.Notes.Domain.Knowledge;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Desktop.Mvvm;

/// <summary>
/// labs 页 ViewModel（文档版知识对象的 nav 页升格为「labs 全类型统一编辑器」）。
/// 左树按 <see cref="CatalogNoteDirectory"/> 分组展示 labs 根下 9 类文件（目录名即 <see cref="NoteNodeType"/>），
/// 右栏按类型分流：form 类型（Agent/Rule/CodeRule/Workflow/Strategy/Knowledge）走上栏表单 + 下栏源码两栏，
/// 代码类型（Note/CodeStrategy/LeanAlgorithm）全宽源码编辑器。
/// 文件树 + watcher + 持久化 + 打开/关闭目录模板继承自 <see cref="FileTreeVmBase"/>。
/// form 类型的 edit VM 复用 <see cref="ExpertsViewModel"/>/<see cref="RulesViewModel"/>/
/// <see cref="WorkflowsViewModel"/>（经 <see cref="IShellHost"/>，已走目录版 catalog）；Knowledge 走
/// <see cref="DocumentKnowledgeViewModel"/>（路径 link 版，区别于 note 版 <see cref="KnowledgeEditViewModel"/>）。
/// </summary>
public partial class KnowledgeObjectsViewModel : FileTreeVmBase
{
    private readonly IKnowledgeDirectoryProvider _knowledgeDirProvider;
    private readonly IShellHost _shell;
    private readonly string _tenant;

    // —— 活动文件状态（所有类型共用：下栏源码 + 落盘路径）——
    private string? _activePath;
    private NoteNodeType? _activeType;
    private string _currentSource = string.Empty;

    /// <summary>目录树扫描时跳过的子目录（与 <c>DocumentKnowledgeTool._ignoredDirs</c> 同集，保证树=AI 工具所见）。</summary>
    private static readonly HashSet<string> IgnoredDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "node_modules", ".git", ".vs", ".idea", ".vscode",
        "packages", "TestResults", "dist", "build", "target", "__pycache__", ".venv", "venv", ".next", ".nuxt",
    };

    protected override string FontSizePrefKey => "ko.editorFontSize";
    protected override string RootPathPrefKey => "ko.rootPath";
    protected override void WriteBackDirectory(string? path) => _knowledgeDirProvider.CurrentDirectory = path;

    /// <summary>知识库名（目录尾名，显示在树头）。</summary>
    public string RootName => string.IsNullOrEmpty(RootPath)
        ? string.Empty
        : Path.GetFileName(RootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

    /// <summary>是否已打开知识库目录（驱动树空态切换）。</summary>
    public bool HasRoot => !string.IsNullOrEmpty(RootPath);

    protected override void RaiseRootDependentProperties()
    {
        OnPropertyChanged(nameof(RootName));
        OnPropertyChanged(nameof(HasRoot));
    }

    // —— 活动编辑 VM（form 类型专用；代码类型为 null）——

    /// <summary>当前活动文件的编辑 VM（form 类型 = 各 edit VM；代码类型 = null）。null = 无表单（全宽源码）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveEdit))]
    private object? _activeEditVm;

    /// <summary>是否有活动编辑表单（form 类型）。</summary>
    public bool HasActiveEdit => ActiveEditVm is not null;

    /// <summary>当前活动文件源码（下栏编辑器显示的内容）。View 经事件 + <see cref="SyncEditor"/> 读用
    ///（<c>TextEditor.Text</c> 非绑定属性，故不绑定；View 直接读此值）。</summary>
    public string CurrentSource => _currentSource;

    /// <summary>活动文件有未存改动（Ctrl+S 落盘后清）。</summary>
    [ObservableProperty] private bool _isDirty;

    /// <summary>下栏源码更新通知（表单自动保存/fast sync 后 View 刷编辑器）。</summary>
    public event Action<string>? KnowledgeSourceUpdated;

    /// <summary>外部（agent/外部编辑器）写盘后当前文件透明重载时引发。View 据此重载编辑器 + 重解析表单（非 dirty 时）。</summary>
    public event Action? KnowledgeExternallyReloaded;

    public KnowledgeObjectsViewModel(IServiceProvider serviceProvider,
        IKnowledgeDirectoryProvider knowledgeDirProvider,
        IShellHost shell,
        string tenant)
        : base(serviceProvider)
    {
        _knowledgeDirProvider = knowledgeDirProvider;
        _shell = shell;
        _tenant = tenant ?? string.Empty;
        SourceControl = new KnowledgeSourceControlViewModel(serviceProvider, knowledgeDirProvider);
        // 恢复上次打开的知识库目录；无持久化目录则回退打开默认 labs 根（provider 字段已就绪，无虚方法调用坑）
        LoadLastRootWithDefaultAsync().SafeFireAndForget();
    }

    /// <summary>是否展示 Trading 三类型（Strategy/CodeStrategy/LeanAlgorithm）——仅 Stockapp 租户
    /// （回测/实盘页用）；Qifu 不依赖 Trading，隐藏这三种类型（树不扫其目录、右键菜单不列）。</summary>
    public bool HasTradingTypes => string.Equals(_tenant, "stockapp", StringComparison.OrdinalIgnoreCase);

    /// <summary>先恢复上次持久化的知识库目录；无则回退打开 <see cref="IKnowledgeDirectoryProvider.DefaultRoot"/>
    /// （默认 <c>&lt;tenantDir&gt;/labs</c>，若不存在则创建）。这样 labs 页启动即有默认目录，用户仍可改目录/关闭。</summary>
    private async Task LoadLastRootWithDefaultAsync()
    {
        await LoadLastRootAsync(); // 基类：有持久化且存在的目录才打开
        if (RootPath is not null) return; // 已有目录（持久化恢复）→ 不用默认
        var def = _knowledgeDirProvider.DefaultRoot;
        if (string.IsNullOrWhiteSpace(def)) return;
        try
        {
            if (!Directory.Exists(def)) Directory.CreateDirectory(def);
            await OpenFolderAsync(def);
        }
        catch { /* 默认目录不可创建/打开 → labs 页保持空态，用户可手动打开 */ }
    }

    partial void OnActiveEditVmChanged(object? value)
        => SaveCommand.NotifyCanExecuteChanged();

    /// <summary>labs 页专属源代码管理 VM（独立知识库目录的 git；镜像 <c>MainViewModelBase.SourceControl</c>）。
    /// 由「labs·SCM」独立 nav tab 绑定——左栏 <c>SourceControlPanelView</c> + 右 <c>SourceControlView</c> 复用项目 SCM 视图。</summary>
    public KnowledgeSourceControlViewModel SourceControl { get; }

    // —— 类型派生属性（驱动右栏上栏按类型切换；由 ActiveNodeType 单点派生）——

    /// <summary>当前活动文件的 <see cref="NoteNodeType"/>（由打开文件时解析的类型，见 <see cref="_activeType"/>）。null=无活动文件。
    /// 不依赖 <see cref="SelectedNode"/>——「新建后直接打开」路径不设 SelectedNode，须以实际打开的文件为准。</summary>
    public NoteNodeType? ActiveNodeType => _activeType;

    public bool IsAgentNode => ActiveNodeType == NoteNodeType.Agent;
    public bool IsWorkflowNode => ActiveNodeType == NoteNodeType.Workflow;
    public bool IsKnowledgeNode => ActiveNodeType == NoteNodeType.Knowledge;
    public bool IsRuleNode => ActiveNodeType == NoteNodeType.Rule;
    public bool IsCodeRuleNode => ActiveNodeType == NoteNodeType.CodeRule;
    public bool IsStrategyNode => ActiveNodeType == NoteNodeType.Strategy;
    /// <summary>纯代码/纯文本类型（Note/CodeStrategy/LeanAlgorithm）——无上栏表单，全宽源码编辑器。</summary>
    public bool IsCodeOnlyNode => ActiveNodeType is NoteNodeType.Note or NoteNodeType.CodeStrategy or NoteNodeType.LeanAlgorithm;

    /// <summary>是否有上栏表单（= form 类型）。</summary>
    public bool IsFormNode => ActiveNodeType is not null
        && NoteNodeTypeRegistry.Get(ActiveNodeType.Value).IsFormNode;

    /// <summary>右栏是否显示「上表单 + 下源码」两栏（form 类型）；代码类型仅下栏。</summary>
    public bool HasRightPane => IsFormNode;

    /// <summary>源码编辑器可见（有活动文件即有；新建后直接打开的文件也显示）。</summary>
    public bool EditorVisible => _activePath is not null;

    /// <summary>上下分隔条可见（仅 form 类型）。</summary>
    public bool SplitterVisible => HasRightPane;

    /// <summary>上栏（表单）可见。</summary>
    public bool RightPaneVisible => HasRightPane;

    /// <summary>源码编辑器所在 Grid 行：form 类型=2（下栏，上栏表单占 0 行）；代码类型=0（占满全宽）。</summary>
    public int EditorRow => HasRightPane ? 2 : 0;

    /// <summary>源码编辑器跨行数：form 类型=1（只占下栏）；代码类型=3（占满 EditorGrid 三行）。</summary>
    public int EditorRowSpan => HasRightPane ? 1 : 3;

    private void RaiseLayout()
    {
        OnPropertyChanged(nameof(ActiveNodeType));
        OnPropertyChanged(nameof(IsAgentNode));
        OnPropertyChanged(nameof(IsWorkflowNode));
        OnPropertyChanged(nameof(IsKnowledgeNode));
        OnPropertyChanged(nameof(IsRuleNode));
        OnPropertyChanged(nameof(IsCodeRuleNode));
        OnPropertyChanged(nameof(IsStrategyNode));
        OnPropertyChanged(nameof(IsCodeOnlyNode));
        OnPropertyChanged(nameof(IsFormNode));
        OnPropertyChanged(nameof(HasRightPane));
        OnPropertyChanged(nameof(EditorVisible));
        OnPropertyChanged(nameof(SplitterVisible));
        OnPropertyChanged(nameof(RightPaneVisible));
        OnPropertyChanged(nameof(EditorRow));
        OnPropertyChanged(nameof(EditorRowSpan));
    }

    /// <summary>由文件路径反查类型：取路径相对 labs 根的第一段目录名 → <see cref="CatalogNoteDirectory.TypeForDirectory"/>。
    /// 目录名即类型（单一权威）；识别失败返回 null。</summary>
    private NoteNodeType? ResolveType(FileNode? node)
    {
        if (node is null || node.IsFolder) return null;
        if (string.IsNullOrEmpty(RootPath)) return null;
        string rel;
        try { rel = Path.GetRelativePath(RootPath, node.Path); }
        catch { return null; }
        var seg = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
        return CatalogNoteDirectory.TypeForDirectory(seg);
    }

    /// <summary>树节点选中：文件→按类型分流打开；文件夹→打开该目录 README（<c>readme.md</c>，无则仅展开/收起由 TreeViewItem 默认处理）。</summary>
    protected override void OnSelectedNodeChanged(FileNode? value)
    {
        RaiseLayout();
        if (value is null) return;
        if (value.IsFolder)
        {
            OpenFolderReadmeAsync(value.Path).SafeFireAndForget();
            return;
        }
        // 同路径（watcher 重建树恢复选中）→ 不重开，保持活动编辑态。
        if (string.Equals(value.Path, _activePath, StringComparison.OrdinalIgnoreCase)) return;
        // 切换文件：清旧表单（避免新类型 Border 短暂显示旧 form 的 DataContext）。
        if (ActiveEditVm is DocumentKnowledgeViewModel kvm)
            kvm.SourceChanged -= OnKnowledgeSourceChanged;
        ActiveEditVm = null;
        OpenFileAsync(value).SafeFireAndForget();
    }

    protected override void ClearActiveContent()
    {
        CloseActiveFile();
        SelectedNode = null;
    }

    protected override void ReloadActiveContent() => ReloadActiveFile();

    // —— 打开文件（按类型分流）——

    /// <summary>打开文件：读盘原文 → 按类型反查 → 分流建 <see cref="ActiveEditVm"/>（form）或置 null（代码全宽）
    /// → 设 <see cref="CurrentSource"/>。同路径跳过（树刷新恢复选中不重复读盘）。</summary>
    private async Task OpenFileAsync(FileNode node, CancellationToken ct = default)
    {
        var filePath = node.Path;
        if (string.Equals(filePath, _activePath, StringComparison.OrdinalIgnoreCase)) return;
        if (!File.Exists(filePath)) { StatusMessage = "文件不存在"; return; }

        string content;
        try { content = await File.ReadAllTextAsync(filePath, ct); }
        catch (Exception ex) { StatusMessage = $"打开失败：{ex.Message}"; return; }

        _activePath = filePath;
        var type = ResolveType(node);
        _activeType = type;
        _currentSource = content;
        IsDirty = false;
        StatusMessage = string.Empty;
        RaiseLayout();

        // form 类型先刷新对应列表（新建/外部新增文件时列表可能 stale，防 item 找不到 → 表单不显示）。
        await RefreshListForTypeAsync(type);
        // 建 edit VM（form 类型）或清空（代码类型全宽）。
        var editVm = await BuildEditVmAsync(type, filePath, content, NameSansExtension(filePath));
        ActiveEditVm = editVm;
        // 代码类型（ActiveEditVm=null）无 OnActiveEditVmChanged 触发 View 刷新，且「新建后直接打开」路径
        // 不设 SelectedNode —— 此处主动发源码刷新，确保下栏编辑器装载 _currentSource（否则看不到源码）。
        KnowledgeSourceUpdated?.Invoke(_currentSource);
    }

    /// <summary>显示名去复合扩展名（如 <c>foo.agent.md</c> → <c>foo</c>）。</summary>
    private static string NameSansExtension(string path)
    {
        var stem = Path.GetFileNameWithoutExtension(path);              // 去最后一节（.md/.yaml/.json/.cs）
        foreach (var prefix in new[] { ".agent", ".workflow", ".rule", ".coderule", ".strategy", ".lean" })
        {
            if (stem.EndsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return stem[..^prefix.Length];
        }
        return stem;
    }

    /// <summary>按类型构建 edit VM；返回 null 表示代码类型（无表单，全宽源码）。form 类型读文件解析 + 定位 item + 接线保存。</summary>
    private async Task<object?> BuildEditVmAsync(NoteNodeType? type, string filePath, string content, string name)
    {
        switch (type)
        {
            case NoteNodeType.Agent: return BuildAgentVm(content);
            case NoteNodeType.Rule: return BuildRuleVm(content);
            case NoteNodeType.CodeRule: return BuildCodeRuleVm(content);
            case NoteNodeType.Workflow: return await BuildWorkflowVmAsync(content);
            case NoteNodeType.Strategy: return BuildStrategyVm(filePath, content, name);
            case NoteNodeType.Knowledge: return BuildKnowledgeVm(filePath, content, name);
            default: return null; // Note/CodeStrategy/LeanAlgorithm/未知 → 全宽源码
        }
    }

    /// <summary>form 类型（Agent/Rule/CodeRule/Workflow）打开前刷新对应列表，使文件里的 <c>id:</c> 能被
    /// <see cref="BuildAgentVm"/> 等的 <c>FirstOrDefault(e.Id == id)</c> 命中共级 VM 的 item。
    /// 新建文件/外部新增文件时列表可能 stale（列表刷新由目录 watcher 防抖触发，有延时），此处同步补一次。
    /// Strategy/Knowledge 不走列表（codec/parser 直读文件），无需刷新。</summary>
    private async Task RefreshListForTypeAsync(NoteNodeType? type)
    {
        switch (type)
        {
            case NoteNodeType.Agent: await _shell.Experts.LoadAsync(); break;
            case NoteNodeType.Rule:
            case NoteNodeType.CodeRule: await _shell.Rules.LoadAsync(); break;
            case NoteNodeType.Workflow: await _shell.Workflows.LoadAsync(); break;
        }
    }

    // —— 各类型 edit VM 构建 + 保存接线 ——

    /// <summary>从文件 front-matter 解析出 <c>id:</c> Guid（labs 文件身份）。解析失败返 Guid.Empty。</summary>
    private Guid ParseIdFromContent(NoteNodeType type, string content)
    {
        try
        {
            CustomAgentDefinition? def = null;
            if (type == NoteNodeType.Rule)
            {
                ServiceProvider.GetRequiredService<IRuleSourceParser>().TryParse(content, out def, out _);
            }
            else if (type == NoteNodeType.CodeRule)
            {
                ServiceProvider.GetRequiredService<ICodeSourceParser>().TryParse(content, out def, out _);
            }
            else
            {
                ServiceProvider.GetRequiredService<IAgentSourceParser>().TryParse(content, out def, out _);
            }
            if (def is not null) return def.Id;
        }
        catch { /* 解析失败 → Empty（无列表项匹配，全宽源码兜底） */ }
        return Guid.Empty;
    }

    private object? BuildAgentVm(string content)
    {
        var id = ParseIdFromContent(NoteNodeType.Agent, content);
        var item = _shell.Experts.Experts.FirstOrDefault(e => e.Id == id);
        if (item is null) { StatusMessage = "未在专家列表中找到该 agent（id 不匹配）"; return null; }
        var vm = _shell.Experts.CreateEditViewModel(item);
        if (vm is null) return null;
        vm.SaveRequested += async saved =>
        {
            var r = await _shell.Experts.SaveAsync(saved);
            if (r.Success) { StatusMessage = "已保存"; await RefreshSourceFromFileAsync(); }
            else vm.Error = r.Error;
        };
        vm.AutoSaveRequested += async saved =>
        {
            var r = await _shell.Experts.SaveAsync(saved);
            if (r.Success) await RefreshSourceFromFileAsync();
        };
        vm.CancelRequested += () => { /* 保持当前活动文件，不额外动作 */ };
        return vm;
    }

    private object? BuildRuleVm(string content)
    {
        var id = ParseIdFromContent(NoteNodeType.Rule, content);
        var item = _shell.Rules.Rules.FirstOrDefault(r => r.Id == id);
        if (item is null) { StatusMessage = "未在规则列表中找到该规则（id 不匹配）"; return null; }
        var vm = _shell.Rules.CreateEditViewModel(item);
        if (vm is null) return null;
        vm.SaveRequested += async saved =>
        {
            var r = await _shell.Rules.SaveAsync(saved);
            if (r.Success) { StatusMessage = "已保存"; await RefreshSourceFromFileAsync(); }
            else vm.Error = r.Error;
        };
        vm.AutoSaveRequested += async saved =>
        {
            var r = await _shell.Rules.SaveAsync(saved);
            if (r.Success) await RefreshSourceFromFileAsync();
        };
        return vm;
    }

    private object? BuildCodeRuleVm(string content)
    {
        var id = ParseIdFromContent(NoteNodeType.CodeRule, content);
        var item = _shell.Rules.Rules.FirstOrDefault(r => r.Id == id);
        if (item is null) { StatusMessage = "未在规则列表中找到该代码规则（id 不匹配）"; return null; }
        var vm = _shell.Rules.CreateCodeRuleEditViewModel(item);
        if (vm is null) return null;
        vm.SaveRequested += async saved =>
        {
            if (_activePath is string p && File.Exists(p))
                saved.SystemPrompt = CodeBodyFromContent(File.ReadAllText(p));
            var r = await _shell.Rules.SaveAsync(saved);
            if (r.Success) { StatusMessage = "已保存"; await RefreshSourceFromFileAsync(); }
            else vm.Error = r.Error;
        };
        vm.AutoSaveRequested += async saved =>
        {
            if (_activePath is string p && File.Exists(p))
                saved.SystemPrompt = CodeBodyFromContent(File.ReadAllText(p));
            var r = await _shell.Rules.SaveAsync(saved);
            if (r.Success) await RefreshSourceFromFileAsync();
        };
        return vm;
    }

    /// <summary>从 front-matter + C# 源文本中取出 C# 正文（经 <see cref="ICodeSourceParser"/>。解析失败回退原文本）。</summary>
    private string CodeBodyFromContent(string content)
    {
        var parser = ServiceProvider.GetService<ICodeSourceParser>();
        if (parser is not null && parser.TryParse(content, out var def, out _) && def is not null)
            return def.SystemPrompt ?? string.Empty;
        return content;
    }

    private async Task<object?> BuildWorkflowVmAsync(string content)
    {
        Guid id = Guid.Empty;
        try
        {
            var parser = ServiceProvider.GetRequiredService<IWorkflowSourceParser>();
            if (parser.TryParse(content, out var def, out _) && def is not null) id = def.Id;
        }
        catch { /* 解析失败 → Empty */ }
        var item = _shell.Workflows.Workflows.FirstOrDefault(w => w.Id == id);
        if (item is null) { StatusMessage = "未在工作流列表中找到该工作流（id 不匹配）"; return null; }
        var vm = await _shell.Workflows.CreateEditViewModelAsync(item);
        if (vm is null) return null;
        vm.SaveRequested += async saved =>
        {
            var r = await _shell.Workflows.SaveAsync(saved);
            if (r.Success) { StatusMessage = "已保存"; await RefreshSourceFromFileAsync(); }
            else vm.Error = r.Error;
        };
        vm.AutoSaveRequested += async saved =>
        {
            var r = await _shell.Workflows.SaveAsync(saved);
            if (r.Success) await RefreshSourceFromFileAsync();
        };
        return vm;
    }

    private object? BuildStrategyVm(string filePath, string content, string name)
    {
        var codec = ServiceProvider.GetService<IStrategyFormCodec>();
        if (codec is null) { StatusMessage = "策略表单不可用（未注册 codec）"; return null; }
        if (!codec.TryParse(content, name, out var model, out var err))
        {
            StatusMessage = "解析失败：" + err;
            return null;
        }
        var id = DeterministicGuid(filePath);
        var vm = new StrategyEditViewModel(id, name, model);
        vm.SaveRequested += saved =>
        {
            var source = codec.Serialize(saved);
            WriteSourceAsync(source).SafeFireAndForget();
        };
        return vm;
    }

    private object? BuildKnowledgeVm(string filePath, string content, string name)
    {
        var parser = ServiceProvider.GetRequiredService<IDocumentKnowledgeSourceParser>();
        var currentFileDir = Path.GetDirectoryName(filePath);
        Func<string?, (string Title, KnowledgeObjectDefinition? Def)?> resolveTargetDef =
            targetPath => OkTargetResolver.ResolveTarget(parser, targetPath, RootPath, currentFileDir);

        var vm = new DocumentKnowledgeViewModel(parser, name, resolveTargetDef);
        vm.SourceChanged += OnKnowledgeSourceChanged;
        vm.Load(content);
        return vm;
    }

    /// <summary>策略文件无 front-matter id：用相对路径求稳定 Guid（策略走 <c>StrategyNoteSource</c>，身份=路径）。</summary>
    private static Guid DeterministicGuid(string path)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(path));
        return new Guid(bytes.AsSpan(0, 16));
    }

    /// <summary>保存后重读盘文件，刷新下栏源码（form 类型：保存序列化后的最终内容写回编辑器）。</summary>
    private async Task RefreshSourceFromFileAsync()
    {
        if (_activePath is not { } p || !File.Exists(p)) return;
        string content;
        try { content = await File.ReadAllTextAsync(p); }
        catch { return; }
        _currentSource = content;
        KnowledgeSourceUpdated?.Invoke(content);
    }

    /// <summary>异步写回活动文件源码（Strategy 表单保存用）。</summary>
    private async Task WriteSourceAsync(string source)
    {
        if (_activePath is not { } p) return;
        try
        {
            await File.WriteAllTextAsync(p, source);
            _currentSource = source;
            IsDirty = false;
            StatusMessage = "已保存";
            KnowledgeSourceUpdated?.Invoke(source);
        }
        catch (Exception ex) { StatusMessage = $"保存失败：{ex.Message}"; }
    }

    // —— 目录 README 自动解析 ——
    // labs 页 README 候选=*.md（知识对象本身可作目录概览）。

    /// <summary>点击目录时打开该目录下的 README（无则不打开，仅展开/收起由 TreeViewItem 默认处理）。</summary>
    public async Task OpenFolderReadmeAsync(string folderPath, CancellationToken ct = default)
    {
        var readme = TryFindReadme(folderPath);
        if (readme is null) return;
        await OpenFileAsync(new FileNode(Path.GetFileName(readme), readme, false), ct);
    }

    /// <summary>按路径打开文件（右键「打开」/新建后打开用）。薄包装 <see cref="OpenFileAsync"/>。</summary>
    public Task OpenKnowledgeByPathAsync(string filePath, CancellationToken ct = default)
        => OpenFileAsync(new FileNode(Path.GetFileName(filePath), filePath, false), ct);

    // —— 按类型新建文件（右键菜单）——

    /// <summary>按类型新建文件，落盘到对应类型目录（<c>CatalogNoteDirectory</c> 权威映射），返回新文件完整路径。
    /// form 类型（Agent/Rule/CodeRule/Workflow）经 parser.Serialize 生成含 <c>id</c> 的完整格式；代码/知识类型用注册表模板。</summary>
    public async Task<string?> CreateNodeAsync(NoteNodeType type, string name)
    {
        var root = RootPath ?? _knowledgeDirProvider.DefaultRoot;
        if (string.IsNullOrWhiteSpace(root)) { StatusMessage = "未打开 labs 目录"; return null; }
        var layout = CatalogNoteDirectory.For(type);
        var fullDir = Path.Combine(root, layout.DirectoryName);
        Directory.CreateDirectory(fullDir);
        var safe = SanitizeFileName(name);
        var path = Path.Combine(fullDir, safe + layout.Extension);
        if (File.Exists(path)) { StatusMessage = $"已存在同名项：{safe}"; return null; }
        try
        {
            await File.WriteAllTextAsync(path, BuildNewContent(type, name));
            StatusMessage = string.Empty;
            return path;
        }
        catch (Exception ex) { StatusMessage = $"新建失败：{ex.Message}"; return null; }
    }

    /// <summary>新建文件的完整正确内容：form 类型经 parser.Serialize（含 id）；代码/知识类型直接用注册表模板。</summary>
    private string BuildNewContent(NoteNodeType type, string name)
    {
        switch (type)
        {
            case NoteNodeType.Agent: return SerializeAgent(name);
            case NoteNodeType.Rule: return SerializeRule(name);
            case NoteNodeType.CodeRule: return SerializeCodeRule(name);
            case NoteNodeType.Workflow: return SerializeWorkflow(name);
            default: return NoteNodeTypeRegistry.Get(type).DefaultContent; // Knowledge/Strategy/CodeStrategy/LeanAlgorithm/Note
        }
    }

    private string SerializeAgent(string name)
    {
        var p = ServiceProvider.GetService<IAgentSourceParser>();
        if (p is null) return $"# {name}\n\n";
        return p.Serialize(new CustomAgentDefinition { Id = Guid.NewGuid(), Name = name, Category = "普通对话" });
    }

    private string SerializeRule(string name)
    {
        var p = ServiceProvider.GetService<IRuleSourceParser>();
        if (p is null) return $"name: {name}\n";
        // 默认程序用规则注册表模板（Serialize 内 Deserialize<RuleProgramDoc> 自动提取 steps/noMatchReply 等，忽略元数据）。
        var defaultProgram = NoteNodeTypeRegistry.Get(NoteNodeType.Rule).DefaultContent;
        return p.Serialize(new CustomAgentDefinition
        {
            Id = Guid.NewGuid(), Name = name, Category = "规则", NodeType = NoteNodeType.Rule,
            Engine = "Rule", Memory = AgentMemoryMode.Off, MaxHistory = 20,
            SystemPrompt = defaultProgram,
        });
    }

    private string SerializeCodeRule(string name)
    {
        var p = ServiceProvider.GetService<ICodeSourceParser>();
        if (p is null) return $"---\nname: {name}\n---\n";
        // 默认 C# body 从 CodeRule 注册表模板提取（去 front-matter）。
        var tmpl = NoteNodeTypeRegistry.Get(NoteNodeType.CodeRule).DefaultContent;
        string body = tmpl;
        if (p.TryParse(tmpl, out var tdef, out _) && tdef is not null && !string.IsNullOrWhiteSpace(tdef.SystemPrompt))
            body = tdef.SystemPrompt;
        return p.Serialize(new CustomAgentDefinition
        {
            Id = Guid.NewGuid(), Name = name, Category = "代码规则", NodeType = NoteNodeType.CodeRule,
            Engine = "Rule", Memory = AgentMemoryMode.Off, MaxHistory = 20,
            SystemPrompt = body,
        });
    }

    private string SerializeWorkflow(string name)
    {
        var p = ServiceProvider.GetService<IWorkflowSourceParser>();
        if (p is null) return "{\"name\":\"" + name + "\"}";
        return p.Serialize(new CustomWorkflowDefinition { Id = Guid.NewGuid(), Name = name, Category = "工作流" });
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (var c in name.Trim())
            sb.Append(invalid.Contains(c) ? '_' : c);
        var t = sb.ToString();
        return string.IsNullOrEmpty(t) ? "untitled" : t;
    }

    /// <summary>在目录顶层找 README（stem==readme 大小写不敏感，扩展名 .md）。</summary>
    private static string? TryFindReadme(string folderPath)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(folderPath, "*.md"))
            {
                if (string.Equals(Path.GetFileNameWithoutExtension(file), "readme", StringComparison.OrdinalIgnoreCase))
                    return file;
            }
        }
        catch { /* 目录无权限/不存在 → 不打开 README */ }
        return null;
    }

    // —— 知识对象表单 ↔ 源码 双向同步（Knowledge 类型专属；form 类型走 AutoSave 刷新）——

    /// <summary>表单→源码：表单字段变→Serialize 生成新源码→更新 <see cref="CurrentSource"/> + 置 dirty + 通知 View 刷编辑器。</summary>
    private void OnKnowledgeSourceChanged(string newSource)
    {
        _currentSource = newSource;
        IsDirty = true;
        KnowledgeSourceUpdated?.Invoke(newSource);
    }

    /// <summary>源码→表单：用户在下栏编辑源码后由 View 调，重解析回填表单（<see cref="DocumentKnowledgeViewModel"/>
    /// 的 <c>_syncing</c> 守卫不回环）+ 更新 <see cref="CurrentSource"/> + 置 dirty。</summary>
    public void SyncFromSource(string source)
    {
        _currentSource = source;
        IsDirty = true;
        (ActiveEditVm as DocumentKnowledgeViewModel)?.SyncFromSource(source);
    }

    /// <summary>清活动文件（不关树）：退订事件 + 清状态。打开新目录/关闭项目时调。</summary>
    private void CloseActiveFile()
    {
        if (ActiveEditVm is DocumentKnowledgeViewModel kvm)
            kvm.SourceChanged -= OnKnowledgeSourceChanged;
        ActiveEditVm = null;
        _activePath = null;
        _activeType = null;
        _currentSource = string.Empty;
        IsDirty = false;
        RaiseLayout();
    }

    private bool CanSave => _activePath is not null;

    /// <summary>保存当前文件源码回磁盘。Ctrl+S 绑此命令。</summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (_activePath is not { } path) return;
        try
        {
            await File.WriteAllTextAsync(path, _currentSource);
            IsDirty = false;
            StatusMessage = string.Empty;
        }
        catch (Exception ex)
        {
            StatusMessage = $"保存失败：{ex.Message}";
        }
    }

    /// <summary>关闭知识库（薄包装：<see cref="FileTreeVmBase.CloseDirectoryCore"/>）。</summary>
    [RelayCommand]
    private void CloseProject() => CloseDirectoryCore();

    // —— 树构建：按 CatalogNoteDirectory 分组，目录名即类型 ——

    /// <summary>递归构建 labs 树。根节点 = labs 根（只含 9 个类型子目录）；类型目录节点收该类型扩展名的文件 +
    /// 递归子目录（子目录继承类型）。文件循环按类型扩展名过滤，图标按类型（描边 Lucide）。</summary>
    protected override FileNode BuildNode(string fullPath, bool isRoot)
    {
        var name = isRoot
            ? Path.GetFileName(fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            : Path.GetFileName(fullPath);
        if (string.IsNullOrEmpty(name)) name = fullPath;
        var node = new FileNode(name, fullPath, isFolder: true);

        // 子目录：先目录后文件排序。类型目录内的子目录继承类型（knowledge/子目录 仍收 .md）。
        // 根层按租户过滤：非 stockapp 跳过策略三目录（strategies/codestrategies/leanalgorithms，Qifu 不依赖 Trading）。
        var subtreeType = isRoot ? (NoteNodeType?)null : ResolveDirType(fullPath);
        foreach (var dir in Directory.EnumerateDirectories(fullPath)
                     .Where(d => !IsIgnoredDir(Path.GetFileName(d)) && !IsSymlinkDir(d))
                     .Where(d => isRoot ? IsVisibleTypeDir(Path.GetFileName(d)) : true)
                     .OrderBy(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase))
        {
            try { node.Children.Add(BuildNode(dir, isRoot: false)); }
            catch (UnauthorizedAccessException) { /* 跳过无权限目录 */ }
            catch (IOException) { /* 跳过不可访问目录 */ }
        }

        // 文件：仅当该目录属于某个类型（subtreeType 非 null）时，收该类型扩展名的文件。
        if (subtreeType is { } t)
        {
            var ext = CatalogNoteDirectory.For(t).Extension;
            foreach (var file in Directory.EnumerateFiles(fullPath, "*" + ext)
                         .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase))
            {
                var stem = Path.GetFileName(file);
                var display = StripCompound(stem, ext);
                node.Children.Add(new FileNode(display, stem, file, isFolder: false, iconOverride: TypeIcon(t)));
            }
        }
        return node;
    }

    /// <summary>由目录绝对路径反查类型（取相对 labs 根首段目录名）。根/未知返回 null。</summary>
    private NoteNodeType? ResolveDirType(string dirPath)
    {
        if (string.IsNullOrEmpty(RootPath)) return null;
        string rel;
        try { rel = Path.GetRelativePath(RootPath, dirPath); }
        catch { return null; }
        var seg = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
        return CatalogNoteDirectory.TypeForDirectory(seg);
    }

    /// <summary>根层目录是否可见（按租户过滤策略三类型）。目录名 → 类型；Trading 三类型仅 stockapp 显示。</summary>
    private bool IsVisibleTypeDir(string dirName)
    {
        if (HasTradingTypes) return true;
        var type = CatalogNoteDirectory.TypeForDirectory(dirName);
        return type is not (NoteNodeType.Strategy or NoteNodeType.CodeStrategy or NoteNodeType.LeanAlgorithm);
    }

    /// <summary>类型图标（描边 Lucide，复用 <see cref="NoteNodeTypeRegistry"/> 的 path + 色）。</summary>
    private static FileIcons.Icon TypeIcon(NoteNodeType type)
    {
        var d = NoteNodeTypeRegistry.Get(type);
        return new FileIcons.Icon(d.IconPathData, d.IconColor, IsFill: false);
    }

    /// <summary>复合扩展名去首段（<c>foo.agent</c> → <c>foo</c>）；无复合段回退去整段扩展名。</summary>
    private static string StripCompound(string fileName, string extension)
    {
        var first = extension.TrimStart('.');
        var dot = first.IndexOf('.');
        if (dot > 0)
        {
            var firstPart = first[..dot]; // "agent"
            var stem = Path.GetFileNameWithoutExtension(fileName); // 去 ".md"
            if (stem.EndsWith("." + firstPart, StringComparison.OrdinalIgnoreCase))
                return stem[..^(firstPart.Length + 1)];
            return stem;
        }
        return Path.GetFileNameWithoutExtension(fileName);
    }

    private static bool IsIgnoredDir(string dirName)
        => dirName.StartsWith(".") || IgnoredDirs.Contains(dirName);

    private static bool IsSymlinkDir(string fullPath)
    {
        try { return (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0; }
        catch { return false; }
    }

    // —— 实时重载：内容变（Changed）→ 重载活动文件（非 dirty 时透明刷新表单+编辑器）——

    /// <summary>重载活动文件：非 dirty → 读盘新内容→更新 <see cref="CurrentSource"/> + 表单重解析（非 dirty 时）+ 通知 View 刷编辑器。
    /// dirty → 跳过（不覆盖用户未存改动）。文件 IO 移后台线程。</summary>
    private void ReloadActiveFile()
    {
        var path = _activePath;
        var dirty = IsDirty;
        if (path is null) return;
        _ = Task.Run(() =>
        {
            if (!File.Exists(path)) return;
            string content;
            try { content = File.ReadAllText(path); }
            catch { return; }
            var ui = ServiceProvider.GetService<IUiDispatcher>();
            if (ui is not null) ui.Post(() => ApplyReload(content, dirty));
            else ApplyReload(content, dirty);
        });
    }

    private void ApplyReload(string content, bool wasDirty)
    {
        if (wasDirty) return; // 用户有未存改动 → 不覆盖
        _currentSource = content;
        if (ActiveEditVm is DocumentKnowledgeViewModel kvm)
            kvm.Load(content); // 重解析表单（Load 内 _loading 守卫不回环 SourceChanged）
        KnowledgeExternallyReloaded?.Invoke(); // View 重载编辑器
    }
}
