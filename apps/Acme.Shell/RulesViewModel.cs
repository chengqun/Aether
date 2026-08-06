using System.Collections.ObjectModel;
using Acme.Agents.AI;
using Acme.Notes.Domain;
using Acme.Notes.Domain.Agents;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Acme.Shell;

/// <summary>
/// 规则 tab ViewModel：规则 agent 列表（召唤/编辑/删除）。规则 agent 是 agent 的一种
///（<see cref="NoteNodeType.Rule"/> + <c>Engine=Rule</c>），复用 <see cref="ICustomAgentCatalog"/>/
/// <see cref="ICustomAgentRunner"/>/<see cref="ExpertItem"/>，但用独立的 <see cref="RuleEditViewModel"/>
/// 编辑器（规则笔记是单个 YAML 文档，非 agent 的 front-matter+body）。按 <see cref="CustomAgentDefinition.NodeType"/>==<see cref="NoteNodeType.Rule"/> 过滤。
/// </summary>
public partial class RulesViewModel : ObservableObject
{
    private readonly ICustomAgentCatalog _catalog;
    private readonly ICustomAgentRunner _runner;
    private readonly IUiDispatcher _dispatcher;

    /// <summary>MCP server 状态（供规则编辑器 MCP 区展示工具勾选）。</summary>
    public McpViewModel Mcp { get; }

    public RulesViewModel(ICustomAgentCatalog catalog, ICustomAgentRunner runner, IUiDispatcher dispatcher, McpViewModel mcp)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        Mcp = mcp ?? throw new ArgumentNullException(nameof(mcp));
    }

    public ObservableCollection<ExpertItem> Rules { get; } = new();

    [ObservableProperty] private string _statusMessage = string.Empty;

    /// <summary>召唤规则到聊天（聊天页订阅）。</summary>
    public event Action? SummonChanged;

    /// <summary>规则列表增删改后触发（笔记页订阅，刷新树）。</summary>
    public event Action? RulesChanged;

    /// <summary>从目录加载所有规则 agent（NodeType==Rule）。</summary>
    public async Task LoadAsync()
    {
        try
        {
            var all = await _catalog.ListAsync();
            await _dispatcher.InvokeAsync(() =>
            {
                // 保留召唤状态：切 tab/NotesChanged 触发 reload 时按 Id 把 IsSummoned 迁移到新实例，
                // 否则卡片显示成未召唤（聊天下拉框持有旧实例仍能用，但卡片与 GetSummonedRules 不一致）。
                var prevSummoned = Rules.Where(r => r.IsSummoned).Select(r => r.Id).ToHashSet();
                Rules.Clear();
                foreach (var def in all)
                {
                    if (def.NodeType != NoteNodeType.Rule) continue;
                    var item = new ExpertItem
                    {
                        Id = def.Id,
                        Name = def.Name,
                        Description = def.Description,
                        Category = string.IsNullOrWhiteSpace(def.Category) ? "规则" : def.Category,
                        SystemPrompt = def.SystemPrompt,
                        Engine = def.Engine,
                        NodeType = def.NodeType,
                        ModelId = def.ModelId,
                        Tools = def.Tools.ToList(),
                        ToolApprovals = def.ToolApprovals ?? new Dictionary<string, string>(),
                        Skills = def.Skills.ToList(),
                        Memory = def.Memory,
                        MaxHistory = def.MaxHistory,
                        RequireToolApproval = def.RequireToolApproval,
                        Source = def.Source,
                        MarketplaceId = def.MarketplaceId,
                    };
                    if (prevSummoned.Contains(item.Id)) item.IsSummoned = true;
                    Rules.Add(item);
                }
            });
        }
        catch (Exception ex)
        {
            StatusMessage = $"加载失败：{ex.Message}";
        }
    }

    /// <summary>创建新规则（仅在用户点保存后才落库）。</summary>
    public ExpertItem CreateNew()
    {
        return new ExpertItem
        {
            Id = Guid.NewGuid(),
            Name = string.Empty,
            Description = string.Empty,
            Category = "规则",
            Engine = "Rule",
            NodeType = NoteNodeType.Rule,
            SystemPrompt = NoteNodeTypeRegistry.Get(NoteNodeType.Rule).DefaultContent,
            Tools = new List<string>(),
            Memory = AgentMemoryMode.Off, // 规则是确定性大脑，无会话内记忆必要
            MaxHistory = 20,
        };
    }

    /// <summary>保存规则（新增或更新）。</summary>
    public async Task<(bool Success, string Error)> SaveAsync(ExpertItem item)
    {
        if (item == null || string.IsNullOrWhiteSpace(item.Name))
            return (false, "名称不能为空");

        try
        {
            var existing = Rules.FirstOrDefault(r => r.Id == item.Id);
            var def = new CustomAgentDefinition
            {
                Id = item.Id,
                Name = item.Name,
                Description = item.Description,
                Category = string.IsNullOrWhiteSpace(item.Category) ? "规则" : item.Category,
                SystemPrompt = item.SystemPrompt,
                Engine = string.IsNullOrWhiteSpace(item.Engine) ? "Rule" : item.Engine,
                NodeType = item.NodeType,
                ModelId = item.ModelId ?? string.Empty,
                Tools = item.Tools,
                ToolApprovals = item.ToolApprovals ?? new Dictionary<string, string>(),
                Skills = item.Skills,
                Memory = item.Memory,
                MaxHistory = item.MaxHistory,
                RequireToolApproval = item.RequireToolApproval,
            };

            await _catalog.SaveAsync(def);
            _runner.Invalidate(item.Id);

            if (existing is not null)
            {
                existing.Name = item.Name;
                existing.Description = item.Description;
                existing.Category = def.Category;
                existing.SystemPrompt = item.SystemPrompt;
                existing.Engine = def.Engine;
                existing.Tools = item.Tools;
                existing.ToolApprovals = item.ToolApprovals;
                existing.Skills = item.Skills;
                existing.Memory = item.Memory;
                existing.MaxHistory = item.MaxHistory;
                existing.RequireToolApproval = item.RequireToolApproval;
            }
            else
            {
                await LoadAsync();
            }

            StatusMessage = existing == null ? $"已创建：{item.Name}" : "已保存";
            SummonChanged?.Invoke();
            RulesChanged?.Invoke();
            return (true, string.Empty);
        }
        catch (Exception ex)
        {
            return (false, $"保存失败：{ex.Message}");
        }
    }

    [RelayCommand]
    private async Task DeleteExpertAsync(ExpertItem item)
    {
        if (item == null) return;
        try
        {
            await _catalog.DeleteAsync(item.Id);
            _runner.Invalidate(item.Id);
            await LoadAsync();
            StatusMessage = $"已删除：{item.Name}";
            SummonChanged?.Invoke();
            RulesChanged?.Invoke();
        }
        catch (Exception ex)
        {
            StatusMessage = $"删除失败：{ex.Message}";
        }
    }

    /// <summary>召唤/取消召唤（多选入池，与 agent/工作流同模型）。召唤只入池，下拉选中才激活。</summary>
    [RelayCommand]
    private void ToggleSummon(ExpertItem item)
    {
        if (item == null) return;
        item.IsSummoned = !item.IsSummoned;
        StatusMessage = item.IsSummoned ? $"已召唤：{item.Name}" : $"已取消召唤：{item.Name}";
        SummonChanged?.Invoke();
    }

    /// <summary>获取已召唤的规则列表。</summary>
    public List<ExpertItem> GetSummonedRules() => Rules.Where(r => r.IsSummoned).ToList();

    /// <summary>创建规则编辑弹窗 ViewModel（独立 RuleEditViewModel；调用方应在打开前 await Mcp.EnsureLoadedAsync()）。</summary>
    public RuleEditViewModel CreateEditViewModel(ExpertItem? existing)
    {
        var tools = _catalog.GetAvailableTools();
        return new RuleEditViewModel(existing, tools, Mcp.Servers.ToList());
    }
}
