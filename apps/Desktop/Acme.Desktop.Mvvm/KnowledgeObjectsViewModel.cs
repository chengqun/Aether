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
using Acme.Notes.Domain.Skills;
using Acme.Desktop.Mvvm.Knowledge;
// 仅引两个运行器接口的别名（非整命名空间）——Aether.AI.Core.Agents 与 Acme.Notes.Domain.Agents 各有一个
// AgentMemoryMode 平行枚举，整命名空间 using 会致 CS0104 歧义。
using ICustomAgentRunner = Aether.AI.Core.Agents.ICustomAgentRunner;
using ICustomWorkflowRunner = Aether.AI.Core.Agents.ICustomWorkflowRunner;
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
/// <see cref="DocumentKnowledgeViewModel"/>（路径 link 版，区别于原 note 版知识对象编辑器——已删）。
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
    /// <summary>当前活动文件是否为目录 README（markdown 两栏：上预览 + 下源码）。普通文件 false。</summary>
    private bool _isReadme;
    /// <summary>当前活动文件是否为 CSV 产物体文件（上栏表格 + 下栏源码两栏）。</summary>
    private bool _isCsv;
    /// <summary>当前选中是否为 skills 根目录（右栏内嵌技能卡片网格，镜像专家页技能 tab）。</summary>
    private bool _isSkillsRoot;
    /// <summary>当前选中是否为 mcp-servers 根目录（右栏内嵌 MCP 管理面板，镜像专家页 MCP tab）。</summary>
    private bool _isMcpRoot;
    /// <summary>当前选中是否为「可平铺卡片」的类型目录根（agents/workflows/rules/coderules/knowledge/strategies/
    /// codestrategies/leanalgorithms）——右栏内嵌文件卡片网格（对齐 skills/mcp 的「点根目录 → 卡片」范式）。</summary>
    private bool _isFileCardsRoot;
    /// <summary>文件卡片根目录对应的类型（<see cref="_isFileCardsRoot"/> 为 true 时非 null）。</summary>
    private NoteNodeType? _fileCardsType;
    /// <summary>README 的 markdown 文案（上栏预览绑定源；ObservableProperty 驱动 SelectableMarkdownViewer 重渲染）。</summary>
    [ObservableProperty] private string _readmeMarkdown = string.Empty;

    /// <summary>目录树扫描时跳过的子目录（与 <c>DocumentKnowledgeTool._ignoredDirs</c> 同集，保证树=AI 工具所见）。</summary>
    private static readonly HashSet<string> IgnoredDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "node_modules", ".git", ".vs", ".idea", ".vscode",
        "packages", "TestResults", "dist", "build", "target", "__pycache__", ".venv", "venv", ".next", ".nuxt",
    };

    /// <summary>产物体文件扩展名（csv/sql 等）。这类文件在类型目录内与 .md 并列显示（通用文件图标），
    /// 点击走全宽源码编辑器（纯文本，按扩展名经 SyntaxMap 高亮），不按所在目录误判为知识对象表单。</summary>
    private static readonly HashSet<string> ArtifactExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".csv", ".sql",
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

    /// <summary>当前活动文件完整路径（null=无活动文件）。供 View 按扩展名算语法高亮（不依赖 SelectedNode，
    /// 新建后直接打开/README 打开时 SelectedNode 与活动文件不一致）。</summary>
    public string? ActivePath => _activePath;

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
    /// <summary>技能节点（SKILL.md）——上栏 SkillEditView 两栏 form（镜像知识对象，无 links/图）。</summary>
    public bool IsSkillNode => ActiveNodeType == NoteNodeType.Skill;
    /// <summary>纯代码/纯文本类型（Note/CodeStrategy/LeanAlgorithm）——无上栏表单，全宽源码编辑器。</summary>
    public bool IsCodeOnlyNode => ActiveNodeType is NoteNodeType.Note or NoteNodeType.CodeStrategy or NoteNodeType.LeanAlgorithm;

    /// <summary>是否有上栏表单（= form 类型）。</summary>
    public bool IsFormNode => ActiveNodeType is not null
        && NoteNodeTypeRegistry.Get(ActiveNodeType.Value).IsFormNode;

    /// <summary>当前活动文件是否为目录 README（markdown 上预览 + 下源码两栏）。</summary>
    public bool IsReadme => _isReadme;

    /// <summary>当前活动文件是否为 CSV 产物体文件（上栏表格预览 + 下栏源码两栏，由 <see cref="CsvTableViewModel"/> 驱动）。</summary>
    public bool IsCsvNode => _isCsv;

    /// <summary>当前选中是否为 skills 根目录——右栏内嵌 SkillView 卡片网格（镜像专家页技能 tab）。
    /// 点树内 SKILL.md 走两栏表单，点兄弟 .py 走全宽代码编辑器。</summary>
    public bool IsSkillsRoot => _isSkillsRoot;

    private SkillViewModel? _skillVm;
    /// <summary>技能 tab VM（DI 单例，懒解析）。skills 根目录选中时 labs 右栏内嵌其卡片网格 + 触发 LoadAsync 刷新。</summary>
    public SkillViewModel? SkillVm => _skillVm ??= ServiceProvider.GetService<SkillViewModel>();

    /// <summary>当前选中是否为 mcp-servers 根目录——右栏内嵌 McpView 管理面板（镜像专家页 MCP tab）。
    /// 点树内 .json 走全宽代码编辑器（纯 JSON 配置文件，无 form）。</summary>
    public bool IsMcpRoot => _isMcpRoot;

    private McpViewModel? _mcpVm;
    /// <summary>MCP 工具 tab VM（DI 单例，懒解析）。mcp-servers 根目录选中时 labs 右栏内嵌其管理面板 + 触发 EnsureLoadedAsync 刷新。</summary>
    public McpViewModel? McpVm => _mcpVm ??= ServiceProvider.GetService<McpViewModel>();

    /// <summary>当前选中的类型目录根是否显示文件卡片网格（而非展开树）。drive View 覆盖显示 <c>FileCardsView</c>。</summary>
    public bool IsFileCardsRoot => _isFileCardsRoot;

    private FileCardsViewModel? _fileCardsVm;
    /// <summary>文件卡片 VM（懒建，纯容器——卡片数据由 <see cref="_isFileCardsRoot"/>/<see cref="BuildFileCardsAsync"/> 注入）。
    /// 根目录选中时 labs 右栏内嵌 + 触发 BuildFileCardsAsync 刷新。</summary>
    public FileCardsViewModel? FileCardsVm
    {
        get
        {
            if (!_isFileCardsRoot) return null;
            return _fileCardsVm ??= new FileCardsViewModel(
                ServiceProvider.GetRequiredService<IUiDispatcher>());
        }
    }

    /// <summary>右栏是否显示「上表单/预览 + 下源码」两栏（form 类型或 README 或 csv 表格）；代码类型仅下栏；skills/mcp 根目录走卡片/面板。</summary>
    public bool HasRightPane => (IsFormNode || IsReadme || IsCsvNode) && !IsSkillsRoot && !IsMcpRoot;

    /// <summary>源码编辑器可见（有活动文件即有；新建后直接打开的文件也显示）。skills/mcp 根目录走卡片/面板，隐藏编辑器。</summary>
    public bool EditorVisible => _activePath is not null && !IsSkillsRoot && !IsMcpRoot;

    /// <summary>上下分隔条可见（form 类型或 README）。</summary>
    public bool SplitterVisible => HasRightPane;

    /// <summary>上栏（表单/预览）可见。</summary>
    public bool RightPaneVisible => HasRightPane;

    /// <summary>源码编辑器所在 Grid 行：有右栏=2（下栏，上栏表单/预览占 0 行）；代码类型=0（占满全宽）。</summary>
    public int EditorRow => HasRightPane ? 2 : 0;

    /// <summary>源码编辑器跨行数：有右栏=1（只占下栏）；代码类型=3（占满 EditorGrid 三行）。</summary>
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
        OnPropertyChanged(nameof(IsSkillNode));
        OnPropertyChanged(nameof(IsCodeOnlyNode));
        OnPropertyChanged(nameof(IsFormNode));
        OnPropertyChanged(nameof(IsReadme));
        OnPropertyChanged(nameof(IsCsvNode));
        OnPropertyChanged(nameof(IsSkillsRoot));
        OnPropertyChanged(nameof(IsMcpRoot));
        OnPropertyChanged(nameof(IsFileCardsRoot));
        OnPropertyChanged(nameof(FileCardsVm));
        OnPropertyChanged(nameof(HasRightPane));
        OnPropertyChanged(nameof(EditorVisible));
        OnPropertyChanged(nameof(SplitterVisible));
        OnPropertyChanged(nameof(RightPaneVisible));
        OnPropertyChanged(nameof(EditorRow));
        OnPropertyChanged(nameof(EditorRowSpan));
        OnPropertyChanged(nameof(ActivePath));
    }

    /// <summary>由文件路径反查类型：取路径相对 labs 根的第一段目录名 → <see cref="CatalogNoteDirectory.TypeForDirectory"/>。
    /// 目录名即类型（单一权威）；识别失败返回 null。</summary>
    private NoteNodeType? ResolveType(FileNode? node)
    {
        if (node is null || node.IsFolder) return null;
        if (string.IsNullOrEmpty(RootPath)) return null;
        // 产物体文件（csv/sql 等）在任何类型目录下都作纯文本（Note）处理——全宽源码编辑器，无 form，
        // 不随所在目录（如 knowledge/）误判为知识对象表单解析。
        if (ArtifactExts.Contains(Path.GetExtension(node.Path)))
            return NoteNodeType.Note;
        string rel;
        try { rel = Path.GetRelativePath(RootPath, node.Path); }
        catch { return null; }
        var seg = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
        var type = CatalogNoteDirectory.TypeForDirectory(seg);
        // 技能是文件夹节点：skills/<name>/SKILL.md。仅 SKILL.md（不区分大小写）属 Skill 类型→走两栏 form；
        // 兄弟脚本（.py/.sh/.js）返 null→全宽代码编辑器（按扩展名经 SyntaxMap 高亮）。
        if (type == NoteNodeType.Skill
            && !string.Equals(Path.GetFileName(node.Path), "SKILL.md", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        return type;
    }

    /// <summary>树节点选中：文件→按类型分流打开；文件夹→打开该目录 README（<c>readme.md</c>，无则仅展开/收起由 TreeViewItem 默认处理）。</summary>
    protected override void OnSelectedNodeChanged(FileNode? value)
    {
        _isSkillsRoot = value is { IsFolder: true } && IsSkillsRootFolder(value);
        _isMcpRoot = value is { IsFolder: true } && IsMcpRootFolder(value);
        _isFileCardsRoot = value is { IsFolder: true } && IsFileCardsRootFolder(value);
        _fileCardsType = _isFileCardsRoot ? ResolveDirType(value!.Path) : null;
        _fileCardsVm = null; // 类型目录根切换/选中变化 → 重建（懒建在上次类型，须清空避免串类型）
        RaiseLayout();
        if (value is null) return;
        if (value.IsFolder)
        {
            if (_isSkillsRoot)
            {
                // skills 根目录 → 右栏内嵌技能卡片网格（SkillView，镜像专家页技能 tab），点树内 SKILL.md 走表单。
                // 清旧活动文件态（防旧源码/表单残留）；SkillVm.LoadAsync 后台重扫盘刷新卡片。
                _isReadme = false;
                _isCsv = false;
                ReadmeMarkdown = string.Empty;
                _activePath = null;
                ActiveEditVm = null;
                SkillVm?.LoadAsync().SafeFireAndForget();
                return;
            }
            if (_isMcpRoot)
            {
                // mcp-servers 根目录 → 右栏内嵌 MCP 管理面板（McpView，镜像专家页 MCP tab），点树内 .json 走全宽编辑器。
                // 清旧活动文件态（防旧源码/表单残留）；McpVm.EnsureLoadedAsync 后台重连+刷新工具列表。
                _isReadme = false;
                _isCsv = false;
                ReadmeMarkdown = string.Empty;
                _activePath = null;
                ActiveEditVm = null;
                McpVm?.EnsureLoadedAsync().SafeFireAndForget();
                return;
            }
            if (_isFileCardsRoot)
            {
                // 类型目录根（agents/workflows/rules/coderules/knowledge/strategies/...）→ 右栏内嵌资产卡片网格。
                // 清旧活动文件态（防旧源码/表单残留）；BuildFileCardsAsync 后台扫目录 + 解析 front-matter 取 name/description。
                _isReadme = false;
                _isCsv = false;
                ReadmeMarkdown = string.Empty;
                _activePath = null;
                ActiveEditVm = null;
                if (_fileCardsType is { } ct) BuildFileCardsAsync(ct).SafeFireAndForget();
                return;
            }
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

        // CSV 产物体文件 → 上栏表格预览 + 下栏源码两栏（不放 full-width code editor）。
        if (string.Equals(Path.GetExtension(filePath), ".csv", StringComparison.OrdinalIgnoreCase))
        {
            OpenCsv(filePath, content);
            return;
        }

        // 切换文件前退订旧 form 表单（防其 SourceChanged 在新文件上下文误触发 _currentSource）。
        // 调用方（OnSelectedNodeChanged 文件分支 / OpenFolderReadmeAsync knowledge 分支 / OpenKnowledgeByPathAsync）
        // 可能未先退订，此处自包含兜底——非 DocumentKnowledgeViewModel/SkillEditViewModel 时 no-op。
        if (ActiveEditVm is DocumentKnowledgeViewModel prevKvm)
            prevKvm.SourceChanged -= OnKnowledgeSourceChanged;
        if (ActiveEditVm is SkillEditViewModel prevSkill)
            prevSkill.SourceChanged -= OnKnowledgeSourceChanged;

        _activePath = filePath;
        var type = ResolveType(node);
        _activeType = type;
        _isSkillsRoot = false;
        _isMcpRoot = false;
        _isFileCardsRoot = false;
        _fileCardsType = null;
        _fileCardsVm = null;
        _isReadme = false;
        _isCsv = false;
        ReadmeMarkdown = string.Empty;
        _currentSource = content;
        IsDirty = false;
        StatusMessage = string.Empty;
        RaiseLayout();
        // 先无条件刷新下栏源码（任何情况下新文件的源码都要显示——否则切换文件时编辑器停留在上一个文件内容）。
        KnowledgeSourceUpdated?.Invoke(_currentSource);

        // 再建上栏表单（可能失败：列表 stale 找不到 item / codec 缺失 / parser 抛异常）。
        // 表单构建失败只影响上栏，不影响已刷新的源码。try/catch 防 RefreshList/LoadAsync 抛异常阻断源码刷新。
        object? editVm = null;
        try
        {
            // form 类型先刷新对应列表（新建/外部新增文件时列表可能 stale，防 item 找不到 → 表单不显示）。
            await RefreshListForTypeAsync(type);
            // 建 edit VM（form 类型）或清空（代码类型全宽）。
            // 技能显示名取父文件夹名（技能 slug）——SKILL.md 经 NameSansExtension 得 "SKILL" 无意义；
            // BuildSkillVm 用此名作 SkillEditViewModel.FolderName（ConfigSummary 「name: {slug} · {category}」）。
            var name = type == NoteNodeType.Skill
                ? Path.GetFileName(Path.GetDirectoryName(filePath)) ?? NameSansExtension(filePath)
                : NameSansExtension(filePath);
            editVm = await BuildEditVmAsync(type, filePath, content, name);
        }
        catch (Exception ex)
        {
            StatusMessage = $"表单加载失败：{ex.Message}";
        }
        ActiveEditVm = editVm;
    }

    /// <summary>打开 CSV 产物体文件为「上栏表格预览 + 下栏源码」两栏。
    /// 建 <see cref="CsvTableViewModel"/>（解析 + 详情/透视图切换）放 <see cref="ActiveEditVm"/>，
    /// 上栏 <see cref="Shared/Shell/Knowledge/CsvTableView"/> 读 <see cref="ActiveEditVm"/> 渲染。
    /// 下栏源码仍可编辑（保存覆盖原文件）。</summary>
    private void OpenCsv(string filePath, string content)
    {
        // 退订旧 form 表单（防 SourceChanged 误触发新文件上下文）。
        if (ActiveEditVm is DocumentKnowledgeViewModel kvm)
            kvm.SourceChanged -= OnKnowledgeSourceChanged;
        if (ActiveEditVm is SkillEditViewModel skill)
            skill.SourceChanged -= OnKnowledgeSourceChanged;

        _activePath = filePath;
        _activeType = null;
        _isReadme = false;
        _isCsv = true;
        _isSkillsRoot = false;
        _isMcpRoot = false;
        _isFileCardsRoot = false;
        _fileCardsType = null;
        _fileCardsVm = null;
        ReadmeMarkdown = string.Empty;
        _currentSource = content;
        IsDirty = false;
        StatusMessage = string.Empty;
        ActiveEditVm = new CsvTableViewModel(content);
        RaiseLayout();
        KnowledgeSourceUpdated?.Invoke(content);
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
            case NoteNodeType.Skill: return BuildSkillVm(filePath, content, name);
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
            // Workflow 走 JSON 解析器（CustomWorkflowDefinition），与 agent/rule 的 YAML+front-matter 解析器不同。
            if (type == NoteNodeType.Workflow)
            {
                var wparser = ServiceProvider.GetRequiredService<IWorkflowSourceParser>();
                return wparser.TryParse(content, out var wdef, out _) && wdef is not null ? wdef.Id : Guid.Empty;
            }
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

        // 传 IUiDispatcher 供 ScheduleSerialize/SyncFromSource 节流（200ms 合并，避免每键击卡 UI 线程）。
        var vm = new DocumentKnowledgeViewModel(parser, name, resolveTargetDef,
            ServiceProvider.GetService<IUiDispatcher>());
        vm.SourceChanged += OnKnowledgeSourceChanged;
        vm.Load(content);
        return vm;
    }

    /// <summary>技能 edit VM（镜像 <see cref="BuildKnowledgeVm"/>，去 links/图）。name=技能 slug（父文件夹名）。</summary>
    private object? BuildSkillVm(string filePath, string content, string name)
    {
        var parser = ServiceProvider.GetRequiredService<ISkillSourceParser>();
        var vm = new SkillEditViewModel(parser, name);
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
    // labs 各类型目录带 readme.md（说明该目录用途 + 文件格式）；点目录即显示。类型目录缺 readme 时懒生成
    //（内容=注册表说明文案），普通子目录（如 knowledge/ 下的自定义子目录）不生成、仅展开/收起。

    /// <summary>点击目录：有 readme.md 则打开；类型目录缺 readme 则先生成再打开；否则仅展开/收起。
    /// knowledge 目录的 README 本身是 .md 知识对象 → 走知识对象分流（与同级 .md 文件同构：上表单+下源码），
    /// 可在表单里给它声明 kind/links 让目录 README 成为链接到成员的一等知识对象；其它类型目录的 README 是
    /// 纯目录说明文档 → 仍走 markdown 预览。</summary>
    public async Task OpenFolderReadmeAsync(string folderPath, CancellationToken ct = default)
    {
        var readme = TryFindReadme(folderPath) ?? await EnsureTypeReadmeAsync(folderPath, ct);
        if (readme is null)
        {
            // 无 README（普通空目录/无说明文档）→ 仅展开/收起（TreeView 默认），不打开文件。
            // 但须清掉旧的 csv 表格上栏，否则「从 csv 切到无 README 目录」时表格残留。
            _isCsv = false;
            _isReadme = false;
            ReadmeMarkdown = string.Empty;
            ActiveEditVm = null;
            RaiseLayout();
            return;
        }
        if (IsKnowledgeSubtree(folderPath))
        {
            await OpenFileAsync(new FileNode(Path.GetFileName(readme), readme, false), ct);
            return;
        }
        await OpenReadmeAsync(readme, ct);
    }

    /// <summary>是否为 skills 根目录（labs 根的直接子目录 <c>skills/</c>，不含 <c>skills/&lt;name&gt;/</c> 子目录）。
    /// skills 根目录选中时右栏内嵌卡片网格；技能子目录仍走 README/默认。</summary>
    private bool IsSkillsRootFolder(FileNode node)
    {
        if (!node.IsFolder || string.IsNullOrEmpty(RootPath)) return false;
        if (ResolveDirType(node.Path) != NoteNodeType.Skill) return false;
        try
        {
            var rel = Path.GetRelativePath(RootPath, node.Path);
            return rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Length == 1;
        }
        catch { return false; }
    }

    /// <summary>是否为 mcp-servers 根目录（labs 根的直接子目录 <c>mcp-servers/</c>）。
    /// mcp-servers 根目录选中时右栏内嵌 MCP 管理面板；点树内 .json 走全宽编辑器。</summary>
    private bool IsMcpRootFolder(FileNode node)
    {
        if (!node.IsFolder || string.IsNullOrEmpty(RootPath)) return false;
        if (ResolveDirType(node.Path) != NoteNodeType.McpServer) return false;
        try
        {
            var rel = Path.GetRelativePath(RootPath, node.Path);
            return rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Length == 1;
        }
        catch { return false; }
    }

    /// <summary>是否为「可平铺卡片」的类型目录根（labs 根的直接子目录，且目录名映射为
    /// agent/workflow/rule/coderule/knowledge/strategy/codestrategy/leanalgorithm——不含 skills/mcp，
    /// 两者走各自专属面板）。这些类型目录点根时右栏平铺该类型直辖文件的卡片（对齐 skills/mcp 范式）。</summary>
    private bool IsFileCardsRootFolder(FileNode node)
    {
        if (!node.IsFolder || string.IsNullOrEmpty(RootPath)) return false;
        var type = ResolveDirType(node.Path);
        if (type is null) return false;
        if (type is NoteNodeType.Skill or NoteNodeType.McpServer or NoteNodeType.Note) return false;
        try
        {
            var rel = Path.GetRelativePath(RootPath, node.Path);
            return rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Length == 1;
        }
        catch { return false; }
    }

    /// <summary>为类型目录根构建资产卡片（后台扫文件 + 解析 front-matter 取 name/description，路径保留供点开）。
    /// agent/rule/coderule/workflow 经各自 parser 解析（name/description/category 真值）；
    /// knowledge 文件名 stem 为 name、<c>kind</c> 为描述；策略族/其它无 name 字段 → 文件名 stem 为 name、无描述。</summary>
    private async Task BuildFileCardsAsync(NoteNodeType type)
    {
        var root = RootPath;
        if (string.IsNullOrWhiteSpace(root)) return;
        var cards = await Task.Run(() =>
        {
            var list = new List<FileCardItem>();
            var (dir, ext) = CatalogNoteDirectory.For(type);
            var fullDir = Path.Combine(root, dir);
            if (!Directory.Exists(fullDir)) return list;
            foreach (var file in Directory.EnumerateFiles(fullDir, "*" + ext)
                         .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                var name = StripCompound(Path.GetFileName(file), ext); // 显示名默认=文件名 stem
                var card = new FileCardItem { Name = name, Path = file };
                string content;
                try { content = File.ReadAllText(file); } catch { list.Add(card); continue; }

                switch (type)
                {
                    case NoteNodeType.Agent:
                        var ap = ServiceProvider.GetService<IAgentSourceParser>();
                        if (ap is not null && ap.TryParse(content, out var adef, out _) && adef is not null)
                            card = new FileCardItem
                            {
                                Name = string.IsNullOrWhiteSpace(adef.Name) ? name : adef.Name,
                                Description = adef.Description ?? string.Empty,
                                Category = adef.Category,
                                Path = file,
                            };
                        break;
                    case NoteNodeType.Rule:
                        var rp = ServiceProvider.GetService<IRuleSourceParser>();
                        if (rp is not null && rp.TryParse(content, out var rdef, out _) && rdef is not null)
                            card = new FileCardItem
                            {
                                Name = string.IsNullOrWhiteSpace(rdef.Name) ? name : rdef.Name,
                                Description = rdef.Description ?? string.Empty,
                                Category = rdef.Category,
                                Path = file,
                            };
                        break;
                    case NoteNodeType.CodeRule:
                        var cp = ServiceProvider.GetService<ICodeSourceParser>();
                        if (cp is not null && cp.TryParse(content, out var cdef, out _) && cdef is not null)
                            card = new FileCardItem
                            {
                                Name = string.IsNullOrWhiteSpace(cdef.Name) ? name : cdef.Name,
                                Description = cdef.Description ?? string.Empty,
                                Category = cdef.Category,
                                Path = file,
                            };
                        break;
                    case NoteNodeType.Workflow:
                        var wp = ServiceProvider.GetService<IWorkflowSourceParser>();
                        if (wp is not null && wp.TryParse(content, out var wdef, out _) && wdef is not null)
                            card = new FileCardItem
                            {
                                Name = string.IsNullOrWhiteSpace(wdef.Name) ? name : wdef.Name,
                                Description = wdef.Description ?? string.Empty,
                                Category = wdef.Category,
                                Path = file,
                            };
                        break;
                    case NoteNodeType.Knowledge:
                        var kp = ServiceProvider.GetService<IDocumentKnowledgeSourceParser>();
                        if (kp is not null && kp.TryParse(content, out var kdef, out _) && kdef is not null)
                            card = new FileCardItem { Name = name, Description = kdef.Kind, Path = file };
                        break;
                    // Strategy/CodeStrategy/LeanAlgorithm/其它：无 name/description 字段 → 文件名 stem。
                }
                list.Add(card);
            }
            return list;
        }).ConfigureAwait(false);

        if (FileCardsVm is { } fvm)
            await fvm.LoadAsync(cards, NoteNodeTypeRegistry.Get(type).Display);
    }

    /// <summary>目录是否在 knowledge 类型子树下（相对根的首段目录名映射为 <see cref="NoteNodeType.Knowledge"/>）。
    /// 与 <see cref="ResolveType"/> 同构（首段即类型），故 knowledge/ 下任意深度的 README 均判为知识对象。</summary>
    private bool IsKnowledgeSubtree(string folderPath)
    {
        if (string.IsNullOrEmpty(RootPath)) return false;
        string rel;
        try { rel = Path.GetRelativePath(RootPath, folderPath); }
        catch { return false; }
        var seg = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
        return CatalogNoteDirectory.TypeForDirectory(seg) == NoteNodeType.Knowledge;
    }

    /// <summary>类型目录缺 readme.md 时生成说明文件（按 <see cref="CatalogNoteDirectory"/> 反查目录名 → 类型）。
    /// 非类型目录（普通子目录）返回 null。生成内容= <see cref="TypeDirectoryReadme"/> 文案。</summary>
    private async Task<string?> EnsureTypeReadmeAsync(string folderPath, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(RootPath)) return null;
        string rel;
        try { rel = Path.GetRelativePath(RootPath, folderPath); }
        catch { return null; }
        var seg = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
        var type = CatalogNoteDirectory.TypeForDirectory(seg);
        if (type is null) return null; // 非类型目录（如根下自定义目录）→ 不生成
        if (!IsVisibleTypeDir(seg)) return null; // 非本租户可见类型（Qifu 策略族）→ 不生成

        var readmePath = Path.Combine(folderPath, "readme.md");
        if (File.Exists(readmePath)) return readmePath;
        try
        {
            await File.WriteAllTextAsync(readmePath, TypeDirectoryReadme(type.Value), ct);
            return readmePath;
        }
        catch { return null; }
    }

    /// <summary>打开 readme.md 为 markdown 上下两栏（上=markdown 预览、下=markdown 源码）。
    /// 仅用于非 knowledge 类型目录的 README（agents/rules 等目录说明文档）——这些 README 是纯文档，
    /// 无运行时身份/无表单。knowledge 目录的 README 走 <see cref="OpenFileAsync"/> 知识对象分流（见 <see cref="OpenFolderReadmeAsync"/>）。</summary>
    private async Task OpenReadmeAsync(string readmePath, CancellationToken ct)
    {
        if (string.Equals(readmePath, _activePath, StringComparison.OrdinalIgnoreCase)) return;
        string content;
        try { content = await File.ReadAllTextAsync(readmePath, ct); }
        catch (Exception ex) { StatusMessage = $"打开失败：{ex.Message}"; return; }

        if (ActiveEditVm is DocumentKnowledgeViewModel kvm)
            kvm.SourceChanged -= OnKnowledgeSourceChanged;
        ActiveEditVm = null;
        _activePath = readmePath;
        _activeType = NoteNodeType.Note;
        _isFileCardsRoot = false;
        _fileCardsType = null;
        _fileCardsVm = null;
        _isCsv = false;
        _isReadme = true; // markdown 上预览 + 下源码两栏
        ReadmeMarkdown = content;
        _currentSource = content;
        IsDirty = false;
        StatusMessage = string.Empty;
        RaiseLayout();
        KnowledgeSourceUpdated?.Invoke(content);
    }

    /// <summary>各类型目录的 README 说明文案（目录用途 + 文件格式）。名字由 <see cref="CatalogNoteDirectory.For"/> 提供。</summary>
    private static string TypeDirectoryReadme(NoteNodeType type)
    {
        var (dir, ext) = CatalogNoteDirectory.For(type);
        var head = $"# {dir}/\n\n";
        return type switch
        {
            NoteNodeType.Agent => head + $"存放 **Agent（智能体）** 定义，文件扩展名 `{ext}`。\n\n" +
                "每个文件 = YAML front-matter（含 `id:` 稳定身份、name/description/category/tools/skills/model 等）+ Markdown 正文（系统提示词）。\n\n" +
                "在资料库页点击文件即打开「上表单 + 下源码」两栏编辑；右键「新建 → Agent」创建。",
            NoteNodeType.Workflow => head + $"存放 **Workflow（专家团）** 定义，文件扩展名 `{ext}`（JSON）。\n\n" +
                "JSON 含 `id`、name/description/category、entryAgent + participants（相对路径）与 handoffs 边。\n\n" +
                "在资料库页点击文件打开两栏编辑；右键「新建 → Workflow」创建。",
            NoteNodeType.Rule => head + $"存放 **规则 agent** 定义，文件扩展名 `{ext}`（单 YAML 文档）。\n\n" +
                "无 LLM 的确定性规则大脑：name/description/tools + steps（match→action）程序。\n\n" +
                "在资料库页点击文件打开两栏编辑；右键「新建 → 规则」创建。",
            NoteNodeType.CodeRule => head + $"存放 **代码规则** 定义，文件扩展名 `{ext}`。\n\n" +
                "YAML front-matter + C# 正文（`CodeRuleBrain` 子类，Roslyn 编译成确定性大脑）。\n\n" +
                "在资料库页点击文件打开两栏编辑；右键「新建 → 代码规则」创建。",
            NoteNodeType.Knowledge => head + $"存放 **知识对象** 定义，文件扩展名 `{ext}`。\n\n" +
                "YAML front-matter（kind + links 图边）+ Markdown 正文。typed links 指向其它知识对象文件（相对路径）。\n\n" +
                "在资料库页点击文件打开两栏编辑；右键「新建 → 知识对象」创建。",
            NoteNodeType.Strategy => head + $"存放 **声明式 YAML 策略**，文件扩展名 `{ext}`。\n\n" +
                "纯 YAML 配方：params + indicators + rules（top-down 首匹配）+ default。解析注册进策略选择器供回测。\n\n" +
                "仅 Stockapp 租户（回测/实盘）。右键「新建 → 策略（YAML）」创建。",
            NoteNodeType.CodeStrategy => head + $"存放 **C# 代码策略**，文件扩展名 `{ext}`（纯 C# 无 front-matter）。\n\n" +
                "一个 `IStrategy` 实现类，Roslyn 编译注册进策略选择器供回测。\n\n" +
                "仅 Stockapp 租户。右键「新建 → 代码策略」创建。",
            NoteNodeType.LeanAlgorithm => head + $"存放 **Lean 算法策略**，文件扩展名 `{ext}`（纯 C# 无 front-matter）。\n\n" +
                "一个 `LeanAlgorithmBase`（= QCAlgorithm）子类，复用 Lean 全部能力，编译注册进 Lean 引擎选择器。\n\n" +
                "仅 Stockapp 租户。右键「新建 → Lean 算法」创建。",
            NoteNodeType.Skill => head + $"存放 **技能** 定义（文件夹节点：{dir}/<name>/SKILL.md + 可选兄弟脚本）。\n\n" +
                "YAML front-matter（name/description/category）+ Markdown 正文，跨 agent 按需 load_skill 引用的提示词能力。\n\n" +
                "在资料库页点击 skills 根目录显示技能卡片网格；点击 SKILL.md 走两栏编辑；兄弟 .py 走全宽编辑器。",
            NoteNodeType.McpServer => head + $"存放 **MCP server 配置**，文件扩展名 `{ext}`（mcpServers 映射，兼容 Claude Code/Cursor）。\n\n" +
                "每个文件 = 一个 MCP server 配置（command/args/env），经 McpServerConfigLoader 扫描自动注册。\n\n" +
                "在资料库页点击 mcp-servers 根目录显示 MCP 管理面板；点击 .json 走全宽编辑器；右键「新建 → MCP」创建。",
            NoteNodeType.Note => head + $"存放 **文本笔记**，文件扩展名 `{ext}`（Markdown）。\n\n" +
                "纯文本/文档，无表单无运行时身份。在资料库页点击文件全宽编辑。",
            _ => head + "（未知类型目录）\n",
        };
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
        if (string.IsNullOrWhiteSpace(root)) { StatusMessage = "未打开资料库目录"; return null; }

        // 技能是文件夹节点：skills/<name>/SKILL.md（+ 可选兄弟脚本）。不建扁平 skills/<name>.md——
        // 运行时 SkillRegistry/AgentFileSkillsSource 扫子目录找 SKILL.md，扁平化则加载不到。
        if (type == NoteNodeType.Skill)
        {
            var skillDir = Path.Combine(root, CatalogNoteDirectory.For(type).DirectoryName, SanitizeFileName(name));
            Directory.CreateDirectory(skillDir);
            var skillFile = Path.Combine(skillDir, "SKILL.md");
            if (File.Exists(skillFile)) { StatusMessage = $"已存在同名技能：{name}"; return null; }
            try
            {
                await File.WriteAllTextAsync(skillFile, BuildNewContent(type, name));
                StatusMessage = string.Empty;
                return skillFile;
            }
            catch (Exception ex) { StatusMessage = $"新建失败：{ex.Message}"; return null; }
        }

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
                if (IsReadmeFile(Path.GetFileName(file)))
                    return file;
            }
        }
        catch { /* 目录无权限/不存在 → 不打开 README */ }
        return null;
    }

    /// <summary>是否目录 README 文件（stem==readme，扩展名 .md/.ok/.markdown 大小写不敏感）。</summary>
    private static bool IsReadmeFile(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        return string.Equals(stem, "readme", StringComparison.OrdinalIgnoreCase);
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
        _isReadme = false;
        _isCsv = false;
        _isSkillsRoot = false;
        _isMcpRoot = false;
        _isFileCardsRoot = false;
        _fileCardsType = null;
        _fileCardsVm = null;
        ReadmeMarkdown = string.Empty;
        _currentSource = string.Empty;
        IsDirty = false;
        RaiseLayout();
    }

    private bool CanSave => _activePath is not null;

    /// <summary>保存当前文件源码回磁盘。Ctrl+S / 顶部保存按钮绑此命令。</summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (_activePath is not { } path) return;
        try
        {
            await File.WriteAllTextAsync(path, _currentSource);
            IsDirty = false;
            StatusMessage = string.Empty;
            // B 路径（本页顶部保存按钮 / Ctrl+S）直接写盘，绕过了表单 VM 的 SaveAsync（后者含 _runner.Invalidate）。
            // agent 家族文件（Agent/Rule/CodeRule/Workflow）写盘后须补失效运行时缓存，否则「labs 改完保存 →
            // 聊天下拉选用」会跑旧缓存版本。幂等——表单路径已失效过则此处无副作用；非 agent 家族（Knowledge/
            // Strategy/Note）无运行时缓存，跳过。watcher 的 400ms 后续列表刷新不在此时序关键路径上。
            InvalidateActiveRuntimeCache();
        }
        catch (Exception ex)
        {
            StatusMessage = $"保存失败：{ex.Message}";
        }
    }

    /// <summary>写盘后失效活动 agent/规则/CodeRule/工作流的运行时缓存（<c>HarnessRuntime.HasAgent</c>→false），
    /// 使下次发消息按新 id 重读盘重建。id 解析自刚写盘的 <c>_currentSource</c>（同 BuildXxxVm 取 id 逻辑）。</summary>
    private void InvalidateActiveRuntimeCache()
    {
        if (_activeType is not NoteNodeType type) return;
        if (type != NoteNodeType.Agent && type != NoteNodeType.Rule
            && type != NoteNodeType.CodeRule && type != NoteNodeType.Workflow)
            return;
        var id = ParseIdFromContent(type, _currentSource);
        if (id == Guid.Empty) return;
        if (type == NoteNodeType.Workflow)
            ServiceProvider.GetService<ICustomWorkflowRunner>()?.Invalidate(id);
        else
            ServiceProvider.GetService<ICustomAgentRunner>()?.Invalidate(id);
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

        // labs 根的直接类型子目录（agents/rules/workflows/...）→ 节点显示中文语义名 + 描述 + Material 入口图标（入口卡样式），
        // 而非裸英文目录名。深层子目录（用户自建）保持原目录名。
        string? subtitle = null;
        string? fillCardIcon = null;
        FileIcons.Icon? entryIcon = null;
        if (!isRoot && !string.IsNullOrEmpty(RootPath))
        {
            var parent = Path.GetDirectoryName(fullPath);
            bool isDirectChild = string.Equals(
                parent?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                RootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
            if (isDirectChild && CatalogNoteDirectory.TypeForDirectory(name) is { } dirType)
            {
                var d = NoteNodeTypeRegistry.Get(dirType);
                name = d.Display;
                subtitle = d.Description;
                fillCardIcon = d.FillIconPath;
                // 入口节点必须显式传 iconOverride：否则 FileNode 走 FileIcons.ForFile(中文名) 回退灰色 Default，
                // 圆角方块底色（绑定 IconColor）会变灰而非类型色。
                entryIcon = new FileIcons.Icon(fillCardIcon ?? d.IconPathData, d.IconColor, IsFill: true);
            }
        }

        var node = new FileNode(name, name, fullPath, isFolder: true, iconOverride: entryIcon,
            subtitle: subtitle, fillCardIcon: fillCardIcon);

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
            // 技能是文件夹节点：skills/<name>/ 含 SKILL.md + 可选兄弟脚本（.py/.sh/.js）。列全部文件（无 .md
            // filter）——否则脚本被隐藏。SKILL.md 走技能图标（sparkles），脚本走扩展名图标（Python/Bash/JS，
            // null=FileIcons.ForFile 经 iconFileName 解析）。显示名=文件名（SKILL.md / foo.py，清晰直白）。
            if (t == NoteNodeType.Skill)
            {
                foreach (var file in Directory.EnumerateFiles(fullPath)
                             .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase))
                {
                    var fileName = Path.GetFileName(file);
                    if (IsReadmeFile(fileName)) continue;
                    var isSkillFile = string.Equals(fileName, "SKILL.md", StringComparison.OrdinalIgnoreCase);
                    node.Children.Add(new FileNode(fileName, fileName, file, isFolder: false,
                        iconOverride: isSkillFile ? TypeIcon(t) : null));
                }
            }
            else
            {
                var ext = CatalogNoteDirectory.For(t).Extension;
                foreach (var file in Directory.EnumerateFiles(fullPath, "*" + ext)
                             .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase))
                {
                    var fileName = Path.GetFileName(file);
                    // 排除目录 README（readme.md / readme.ok）——它是目录说明，点目录时经 OpenFolderReadmeAsync 显示，不进树。
                    if (IsReadmeFile(fileName)) continue;
                    var stem = fileName;
                    var display = StripCompound(stem, ext);
                    node.Children.Add(new FileNode(display, stem, file, isFolder: false, iconOverride: TypeIcon(t)));
                }
                // 产物体文件（csv/sql 等）：类型目录（尤其 knowledge/）内与 .md 并列显示，通用文件图标，
                // 点击走全宽源码编辑器（ResolveType 按扩展名返 Note）。列全部产物体文件（不按类型 ext filter）。
                foreach (var file in Directory.EnumerateFiles(fullPath)
                             .Where(f => ArtifactExts.Contains(Path.GetExtension(f)))
                             .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase))
                {
                    var fileName = Path.GetFileName(file);
                    if (IsReadmeFile(fileName)) continue;
                    node.Children.Add(new FileNode(fileName, fileName, file, isFolder: false,
                        iconOverride: null)); // 通用文件图标（FileIcons.ForFile：sql 蓝图标、csv 回退默认）
                }
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
