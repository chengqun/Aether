using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Acme.Mobile.Mvvm.Events;
using Acme.Mobile.Mvvm.Models;
using Acme.Notes.Domain;
using Acme.Notes.Domain.Agents;
using Acme.Notes.Domain.Catalog;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Acme.Mobile.Mvvm.ViewModels;

/// <summary>
/// 通用 labs 资产列表页 VM（AI 设置 → 某类型入口）。按 <c>type</c> 参数（agent/workflow/skill/mcp/knowledge）
/// 加载对应列表，暴露「＋新建」（进对应 Create 页）与「编辑」（点列表项进对应编辑页）。
/// 5 类共用一页，故列表项统一用 <see cref="AssetListItem"/>（agent/workflow 走 Id，skill/mcp/knowledge 走 Name=Key）。
/// </summary>
public partial class AssetListViewModel : NavigableViewModelBase
{
    public const string TypeAgent = "agent";
    public const string TypeWorkflow = "workflow";
    public const string TypeSkill = "skill";
    public const string TypeMcp = "mcp";
    public const string TypeKnowledge = "knowledge";

    private readonly Func<string?>? _labsRoot;
    private readonly ICustomAgentCatalog? _agentCatalog;
    private readonly ICustomWorkflowCatalog? _workflowCatalog;

    /// <summary>当前列表类型（导航参数 type）。</summary>
    private string _type = TypeAgent;

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _statusMessage = string.Empty;

    /// <summary>页标题（按类型）。</summary>
    public string Title => _type switch
    {
        TypeAgent => "AI 助手",
        TypeWorkflow => "工作流",
        TypeSkill => "技能",
        TypeMcp => "MCP 服务器",
        TypeKnowledge => "知识对象",
        _ => "资产",
    };

    /// <summary>「＋新建」按钮文案。</summary>
    public string NewLabel => $"＋ 新建{(_type == TypeAgent ? "助手" : _type == TypeKnowledge ? "知识对象" : "")}";

    public ObservableCollection<AssetListItem> Items { get; } = new();

    public event EventHandler<NavigationRequestedEvent>? NavigationRequested;

    public AssetListViewModel(Func<string?>? labsRoot = null,
                              ICustomAgentCatalog? agentCatalog = null,
                              ICustomWorkflowCatalog? workflowCatalog = null)
    {
        _labsRoot = labsRoot;
        _agentCatalog = agentCatalog;
        _workflowCatalog = workflowCatalog;
    }

    /// <summary>接收导航参数 <c>type</c>（agent/workflow/skill/mcp/knowledge）。</summary>
    public void SetParameters(IDictionary<string, object> query)
    {
        if (query.TryGetValue("type", out var raw) && raw is string t && !string.IsNullOrWhiteSpace(t))
            _type = t;
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(NewLabel));
    }

    public override Task OnActivatedAsync()
    {
        _ = LoadAsync();
        return Task.CompletedTask;
    }

    private async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            Items.Clear();
            switch (_type)
            {
                case TypeAgent:
                    await LoadAgentsAsync();
                    break;
                case TypeWorkflow:
                    await LoadWorkflowsAsync();
                    break;
                case TypeSkill:
                    ScanFolderAssets(NoteNodeType.Skill, isFolder: true);
                    break;
                case TypeMcp:
                    ScanFlatAssets(NoteNodeType.McpServer, "*.json");
                    break;
                case TypeKnowledge:
                    ScanFlatAssets(NoteNodeType.Knowledge, "*.md");
                    break;
            }
            StatusMessage = Items.Count == 0 ? "暂无内容，点右上「新建」创建" : string.Empty;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AssetList] 加载失败: {ex.Message}");
            StatusMessage = $"加载失败: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task LoadAgentsAsync()
    {
        if (_agentCatalog == null) return;
        var agents = await Task.Run(() => _agentCatalog.ListAsync());
        foreach (var a in agents)
        {
            Items.Add(new AssetListItem
            {
                Id = a.Id,
                Name = a.Name,
                Subtitle = a.NodeType switch
                {
                    NoteNodeType.Workflow => "工作流",
                    NoteNodeType.Rule or NoteNodeType.CodeRule => "规则",
                    _ => "智能体",
                },
            });
        }
    }

    private async Task LoadWorkflowsAsync()
    {
        if (_workflowCatalog == null) return;
        var defs = await Task.Run(() => _workflowCatalog.ListAsync());
        foreach (var d in defs)
            Items.Add(new AssetListItem { Id = d.Id, Name = d.Name, Subtitle = d.Description });
    }

    private void ScanFolderAssets(NoteNodeType type, bool isFolder)
    {
        var root = _labsRoot?.Invoke();
        if (string.IsNullOrWhiteSpace(root)) return;
        var dir = Path.Combine(root, CatalogNoteDirectory.For(type).DirectoryName);
        if (!Directory.Exists(dir)) return;
        foreach (var sub in Directory.GetDirectories(dir))
        {
            var name = Path.GetFileName(sub);
            if (name.StartsWith(".")) continue;
            Items.Add(new AssetListItem { Name = name });
        }
    }

    private void ScanFlatAssets(NoteNodeType type, string pattern)
    {
        var root = _labsRoot?.Invoke();
        if (string.IsNullOrWhiteSpace(root)) return;
        var dir = Path.Combine(root, CatalogNoteDirectory.For(type).DirectoryName);
        if (!Directory.Exists(dir)) return;
        foreach (var f in Directory.GetFiles(dir, pattern))
            Items.Add(new AssetListItem { Name = Path.GetFileNameWithoutExtension(f) });
    }

    /// <summary>「＋新建」→ 进入对应类型的创建页（新建态，无参数）。
    /// 注：agent 的新建/编辑共用 AgentEditPage（无 agentId 参数 = 新建态）。</summary>
    [RelayCommand]
    private void New()
    {
        NavigationRequested?.Invoke(this, NavigationRequestedEvent.To(_type switch
        {
            TypeAgent => "AgentEditPage",
            TypeWorkflow => "WorkflowCreatePage",
            TypeSkill => "SkillCreatePage",
            TypeMcp => "McpCreatePage",
            TypeKnowledge => "KnowledgeCreatePage",
            _ => "AgentEditPage",
        }));
    }

    /// <summary>点列表项 → 进入对应编辑页（带 id/name 参数，编辑态）。</summary>
    [RelayCommand]
    private void Edit(AssetListItem item)
    {
        if (item == null) return;
        switch (_type)
        {
            case TypeAgent:
                NavigationRequested?.Invoke(this, NavigationRequestedEvent.To("AgentEditPage",
                    new Dictionary<string, object> { ["agentId"] = item.Id.ToString() }));
                break;
            case TypeWorkflow:
                NavigationRequested?.Invoke(this, NavigationRequestedEvent.To("WorkflowCreatePage",
                    new Dictionary<string, object> { ["id"] = item.Id.ToString() }));
                break;
            case TypeSkill:
                NavigationRequested?.Invoke(this, NavigationRequestedEvent.To("SkillCreatePage",
                    new Dictionary<string, object> { ["name"] = item.Name }));
                break;
            case TypeMcp:
                NavigationRequested?.Invoke(this, NavigationRequestedEvent.To("McpCreatePage",
                    new Dictionary<string, object> { ["name"] = item.Name }));
                break;
            case TypeKnowledge:
                NavigationRequested?.Invoke(this, NavigationRequestedEvent.To("KnowledgeCreatePage",
                    new Dictionary<string, object> { ["name"] = item.Name }));
                break;
        }
    }
}
