using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Acme.Mobile.Mvvm.Abstractions;
using Acme.Mobile.Mvvm.Events;
using Acme.Mobile.Mvvm.Models;
using Acme.Notes.Domain;
using Acme.Notes.Domain.Catalog;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Acme.Mobile.Mvvm.ViewModels;

/// <summary>
/// labs 目录浏览 VM（AI 设置 → labs 入口）。列出 &lt;tenantDir&gt;/labs 下各子目录（agents/rules/workflows/
/// knowledge/skills/mcp-servers 等）的文件，点开只读查看。这是手机端 labs 创作面的「列表入口」——后续各
/// 类型编辑页（Workflow/Knowledge/Skill/MCP）从这里的列表项进入。
/// labs 根经 <see cref="Func{TResult}"/> 注入（宿主绑 <c>() =&gt; Path.Combine(tenantDir, "labs")</c>，
/// 与 <c>DirectoryAgentCatalog</c> 同源——同一权威存储，无第二入口）。
/// </summary>
public partial class LabsViewModel : NavigableViewModelBase
{
    private readonly Func<string?> _labsRoot;
    private readonly IUIThreadDispatcher? _dispatcher;

    [ObservableProperty]
    private ObservableCollection<LabsSection> _sections = new();

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    /// <summary>待查看的文件（点列表项 → 详情页读内容；经导航参数传 relativePath）。</summary>
    public LabsFileItem? SelectedFile { get; set; }

    /// <summary>导航请求（VM 发 → 页面订阅执行；补 back / 前进）。</summary>
    public event EventHandler<NavigationRequestedEvent>? NavigationRequested;

    public LabsViewModel(Func<string?> labsRoot, IUIThreadDispatcher? dispatcher = null)
    {
        _labsRoot = labsRoot ?? throw new ArgumentNullException(nameof(labsRoot));
        _dispatcher = dispatcher;
    }

    public override Task OnActivatedAsync()
    {
        _ = LoadAsync();
        return Task.CompletedTask;
    }

    /// <summary>扫描 labs 根目录，按 <see cref="CatalogNoteDirectory"/> 权威映射分组列文件。</summary>
    private Task LoadAsync()
    {
        // 扫描是本地文件 I/O，跑后台避免阻塞 UI 线程。
        return Task.Run(() =>
        {
            var root = _labsRoot();
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                _dispatcher?.Post(() =>
                {
                    Sections.Clear();
                    StatusMessage = "labs 目录不存在";
                });
                return;
            }

            var sections = new List<LabsSection>();

            // 只列 labs 里「参与 labs 体系」的子目录（跳过 note/策略/Lean 等手机端语义外或空目录）。
            foreach (var type in CatalogNoteDirectory.AllTypes)
            {
                if (type is NoteNodeType.Note or NoteNodeType.CodeStrategy
                    or NoteNodeType.Strategy or NoteNodeType.LeanAlgorithm)
                    continue; // 手机端 labs 创作面不覆盖这些（Note 已删；策略/Lean 桌面专属）

                var layout = CatalogNoteDirectory.For(type);
                var dir = Path.Combine(root, layout.DirectoryName);
                if (!Directory.Exists(dir)) continue;

                var files = new List<LabsFileItem>();
                // Skill 是文件夹节点（skills/<name>/SKILL.md + 脚本），列全部文件；其余列匹配扩展名的文件。
                if (type == NoteNodeType.Skill)
                {
                    foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                        files.Add(MakeItem(f, root));
                }
                else
                {
                    foreach (var f in Directory.EnumerateFiles(dir, "*" + layout.Extension, SearchOption.TopDirectoryOnly))
                        files.Add(MakeItem(f, root));
                }

                files.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
                var section = new LabsSection
                {
                    Name = layout.DirectoryName,
                    Label = LabelFor(type),
                    NodeType = type,
                };
                foreach (var f in files) section.Files.Add(f);
                if (section.HasFiles) sections.Add(section);
            }

            _dispatcher?.Post(() =>
            {
                Sections.Clear();
                foreach (var s in sections) Sections.Add(s);
                StatusMessage = Sections.Count == 0 ? "labs 目录为空" : string.Empty;
            });
        });
    }

    private static LabsFileItem MakeItem(string fullPath, string root)
    {
        var rel = Path.GetRelativePath(root, fullPath).Replace('\\', '/');
        return new LabsFileItem
        {
            RelativePath = rel,
            Name = Path.GetFileNameWithoutExtension(fullPath),
            Extension = Path.GetExtension(fullPath),
            FullPath = fullPath,
        };
    }

    private static string LabelFor(NoteNodeType type) => type switch
    {
        NoteNodeType.Agent => "智能体",
        NoteNodeType.Rule => "规则",
        NoteNodeType.CodeRule => "代码规则",
        NoteNodeType.Workflow => "工作流",
        NoteNodeType.Knowledge => "知识对象",
        NoteNodeType.Skill => "技能",
        NoteNodeType.McpServer => "MCP 服务",
        _ => "其他",
    };

    /// <summary>点列表项 → 触发导航到只读查看页（relativePath 作为导航参数）。</summary>
    [RelayCommand]
    private void OpenFile(LabsFileItem item)
    {
        if (item == null) return;
        NavigationRequested?.Invoke(this, NavigationRequestedEvent.To("LabsFileViewPage",
            new Dictionary<string, object> { ["path"] = item.RelativePath }));
    }
}
