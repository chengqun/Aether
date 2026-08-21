using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using Acme.Notes.Domain.Knowledge;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Acme.Desktop.Mvvm;

/// <summary>
/// 文档版知识对象 link 行。与 note 版一致——表单只显示/编辑目标<b>名称</b>（targetId 路径的文件名部分 stem），
/// 目录前缀（指针）隐藏在 <see cref="TargetDirectory"/> 不绑表单（在源码编辑）。编辑名称时保留目录、只改 stem
/// （<see cref="DocumentKnowledgeViewModel.Build"/> 重建完整路径回写 targetId）。
/// </summary>
public partial class DocumentKnowledgeLinkRow : ObservableObject
{
    [ObservableProperty] private string _role = string.Empty;

    /// <summary>目标名称 = 路径的文件名部分（stem），表单显示/编辑。如 Hive表/fin_dw.dwd_xxx → fin_dw.dwd_xxx。</summary>
    [ObservableProperty] private string _target = string.Empty;

    /// <summary>目标路径的目录前缀（表单不绑；Build 时与 Target 拼回完整 targetId 路径）。仅 Load/SyncFromSource 设置。</summary>
    public string TargetDirectory { get; set; } = string.Empty;
}

/// <summary>
/// 文档（<c>.ok</c> 文件）知识对象表单 VM（UI-agnostic，零 Avalonia）。
/// 镜像 <see cref="KnowledgeEditViewModel"/> 的 kind/links/body + 双向同步 + 图，去掉 note 耦合：
/// 无 note-target 解析/选择器/自动存库——表单变更经 <see cref="IDocumentKnowledgeSourceParser.Serialize"/>
/// 生成源码（<see cref="SourceChanged"/>），由 <see cref="DocumentsViewModel"/> 回写 tab.Content（既有 Ctrl+S 落盘）。
/// link target 为相对路径文本（指向项目内另一个 .ok 文件）；body 为只读 markdown 预览（源码驱动，
/// <see cref="SyncFromSource"/> 刷新），不接 serialize。<b>图</b>：经 <see cref="BuildGraphModel"/> 构建出边闭包子图
/// （焦点=当前对象高亮，沿 targetId 路径读 .ok 文件递归跟出边——同 note 版 <see cref="KnowledgeGraphBuilder.BuildClosure"/>），
/// 由宿主 <see cref="DocumentsViewModel"/> 注入 <c>resolveTargetDef</c>（读 .ok 文件解析定义）；UI-agnostic，
/// 不引 Avalonia 窗口（窗口由 view code-behind 弹 <see cref="WorkflowGraphWindow"/>）。
/// </summary>
public sealed partial class DocumentKnowledgeViewModel : ObservableObject
{
    private readonly IDocumentKnowledgeSourceParser _parser;
    /// <summary>link target→(名称, 定义) 解析器（宿主注入，读 .ok 文件供闭包跟出边）；null=不建图（图按钮静默无效）。</summary>
    private readonly Func<string?, (string Title, KnowledgeObjectDefinition? Def)?>? _resolveTargetDef;
    private bool _loading = true; // 构造期 + Load 期守卫，防初始填充触发 SourceChanged
    private bool _syncing;        // 源码→表单同步期守卫，防回环

    public DocumentKnowledgeViewModel(
        IDocumentKnowledgeSourceParser parser,
        string name,
        Func<string?, (string Title, KnowledgeObjectDefinition? Def)?>? resolveTargetDef)
    {
        _parser = parser;
        Name = name ?? string.Empty;
        _resolveTargetDef = resolveTargetDef;
        Links.CollectionChanged += OnLinksCollectionChanged;
    }

    /// <summary>焦点知识对象名称（= .ok 文件名 stem，非表单可编辑字段；供图构建 focal 节点标题）。</summary>
    public string Name { get; }

    [ObservableProperty] private string _kind = string.Empty;
    [ObservableProperty] private string _body = string.Empty;

    public ObservableCollection<DocumentKnowledgeLinkRow> Links { get; } = new();

    /// <summary>配置折叠时的摘要文本（「kind: xxx · N 引用」），供 Expander.Header 显示。
    /// Kind 变化 + Links 增删都重算（ObservableCollection 的 Count 不通知，须手动 Raise）。</summary>
    public string ConfigSummary => $"kind: {(string.IsNullOrEmpty(Kind) ? "（未设置）" : Kind)} · {Links.Count} 引用";

    /// <summary>表单→源码：kind/links 变更后重新序列化的 YAML 源文本。DocumentsViewModel 订阅回写 tab.Content。</summary>
    public event Action<string>? SourceChanged;

    partial void OnKindChanged(string value)
    {
        OnPropertyChanged(nameof(ConfigSummary)); // 摘要里含 kind
        ScheduleSerialize();
    }
    // body 是只读预览（源码驱动），不接 serialize——OnBodyChanged 故意不定义。

    /// <summary>从源文本载入（初始 / 切到该 tab）。_loading 守卫不发 SourceChanged。</summary>
    public void Load(string source)
    {
        _loading = true;
        try
        {
            if (_parser.TryParse(source, out var def, out _) && def is not null)
            {
                Kind = def.Kind ?? string.Empty;
                Body = def.Body ?? string.Empty;
                RebuildLinks(def.Links ?? new());
            }
            else
            {
                // 解析失败：表单留空不崩，源码仍可编辑（下栏）
                Kind = string.Empty;
                Body = string.Empty;
                RebuildLinks(new());
            }
        }
        finally { _loading = false; }
    }

    /// <summary>源码→表单（用户在下栏编辑源码后）。_syncing 守卫不发 SourceChanged（防回环）。</summary>
    public void SyncFromSource(string source)
    {
        _syncing = true;
        try { Load(source); } // Load 置 _loading=true；_syncing 叠加使 OnKindChanged/CollectionChanged 跳过 serialize
        finally { _syncing = false; }
        OnPropertyChanged(nameof(ConfigSummary)); // Load 期守卫跳过了 CollectionChanged，此处补摘要刷新
    }

    /// <summary>重建 link 行（Load 期调用，_loading 守卫使 OnLinksCollectionChanged 早返，手动管订阅）。</summary>
    private void RebuildLinks(System.Collections.Generic.List<KnowledgeLink> links)
    {
        foreach (var row in Links) row.PropertyChanged -= OnLinkRowChanged;
        Links.Clear();
        foreach (var l in links)
        {
            // 文档版 link 的 targetId（路径）存于 KnowledgeLink.Target（note 版 Guid? TargetId 容不下路径）。
            // 与 note 版一致：表单只显示「名称」= 路径的文件名部分 stem；目录前缀保留在 TargetDirectory，
            // Build 时拼回完整路径写回 targetId。改名称只改 stem、目录不动。
            var (dir, name) = SplitPath(l.Target ?? string.Empty);
            var row = new DocumentKnowledgeLinkRow { Role = l.Role, Target = name, TargetDirectory = dir };
            row.PropertyChanged += OnLinkRowChanged;
            Links.Add(row);
        }
    }

    private void ScheduleSerialize()
    {
        if (_loading || _syncing) return;
        SourceChanged?.Invoke(_parser.Serialize(Build()));
    }

    /// <summary>
    /// 把可编辑字段投影回 Domain 模型。Target = 目录前缀 + 名称拼成的完整路径（写回 targetId），
    /// TargetId(Guid?) 留空——文档版路径是字符串，note 版 Guid? TargetId 容不下，故路径存 Target。
    /// </summary>
    public KnowledgeObjectDefinition Build() => new()
    {
        Kind = Kind ?? string.Empty,
        Body = Body ?? string.Empty,
        Links = Links.Select(l => new KnowledgeLink { Role = l.Role, Target = CombinePath(l.TargetDirectory, l.Target) }).ToList(),
    };

    /// <summary>拆分相对路径为（目录前缀, 文件名 stem）。无 "/" 则整体为名称、目录空。</summary>
    private static (string dir, string name) SplitPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return (string.Empty, string.Empty);
        var idx = path.LastIndexOf('/');
        return idx < 0 ? (string.Empty, path) : (path.Substring(0, idx), path.Substring(idx + 1));
    }

    /// <summary>目录前缀 + 名称 → 完整路径（名称空则仅目录；目录空则仅名称）。</summary>
    private static string CombinePath(string dir, string name)
    {
        if (string.IsNullOrEmpty(dir)) return name ?? string.Empty;
        if (string.IsNullOrEmpty(name)) return dir;
        return $"{dir}/{name}";
    }

    /// <summary>从当前知识对象出发构建出边闭包子图（焦点高亮，沿 targetId 路径读 .ok 文件递归跟出边）。
    /// 与 note 版 <see cref="KnowledgeEditViewModel.BuildGraphModel"/> 同构——Build() 重建完整路径存 KnowledgeLink.Target，
    /// <see cref="KnowledgeGraphBuilder.BuildClosure"/> 读 l.Target（路径）→传给 _resolveTargetDef 解析成 (名称,定义)。
    /// 无解析器→空图（图按钮静默无效，view 早返不弹窗）。</summary>
    public WorkflowGraphModel BuildGraphModel()
    {
        var resolver = _resolveTargetDef;
        if (resolver is null) return WorkflowGraphModel.Empty;
        return KnowledgeGraphBuilder.BuildClosure(Name, Build(), resolver);
    }

    /// <summary>从当前知识对象出发生成 Mermaid flowchart 字符串（供复制/导出，同 note 版）。</summary>
    public string BuildMermaid() => WorkflowGraphBuilder.ToMermaid(BuildGraphModel());

    [RelayCommand]
    private void AddLink()
    {
        var row = new DocumentKnowledgeLinkRow();
        Links.Add(row); // OnLinksCollectionChanged 订阅 + ScheduleSerialize
    }

    [RelayCommand]
    private void RemoveLink(DocumentKnowledgeLinkRow? row)
    {
        if (row is null) return;
        Links.Remove(row); // OnLinksCollectionChanged 退订 + ScheduleSerialize
    }

    private void OnLinksCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Load 期（_loading）由 RebuildLinks 手动管订阅，此处早返防双订阅；用户增删时正常订阅/退订
        if (_loading || _syncing) return;
        if (e.NewItems is not null) foreach (DocumentKnowledgeLinkRow r in e.NewItems) r.PropertyChanged += OnLinkRowChanged;
        if (e.OldItems is not null) foreach (DocumentKnowledgeLinkRow r in e.OldItems) r.PropertyChanged -= OnLinkRowChanged;
        OnPropertyChanged(nameof(ConfigSummary)); // 引用数变化
        ScheduleSerialize();
    }

    private void OnLinkRowChanged(object? sender, PropertyChangedEventArgs e) => ScheduleSerialize();
}
