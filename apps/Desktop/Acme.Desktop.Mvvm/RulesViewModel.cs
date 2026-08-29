using System.Collections.ObjectModel;
using Aether.AI.Core.Agents;
using Acme.Notes.Domain;
using Acme.Notes.Domain.Agents;
// Core 与 Notes.Domain 各有一个平行 AgentMemoryMode 枚举（适配器在 ToAgentDefinition 处映射）；
// 此 VM 构建 CustomAgentDefinition（Notes 域），故别名钉到 Notes 域枚举，消解歧义。
using AgentMemoryMode = Acme.Notes.Domain.Agents.AgentMemoryMode;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Acme.Desktop.Mvvm;

/// <summary>
/// 规则 agent 列表 VM：规则 agent 是 agent 的一种（<see cref="NoteNodeType.Rule"/> + <c>Engine=Rule</c>），
/// 复用 <see cref="ICustomAgentCatalog"/>/<see cref="ICustomAgentRunner"/>/<see cref="ExpertItem"/>，
/// 按 <see cref="CustomAgentDefinition.NodeType"/>==<see cref="NoteNodeType.Rule"/>/<see cref="NoteNodeType.CodeRule"/> 过滤。
/// labs 知识对象页承接创建+编辑；本 VM 供聊天下拉 + labs 编辑保存用。
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

    /// <summary>可选目标集合变更（新建/删除/catalog reload）时触发。ChatWorkspaceViewModel 订阅刷新统一下拉池。</summary>
    public event Action? TargetsChanged;

    /// <summary>外部触发目标集合变更通知（catalog Changed reload 后调用，让聊天下拉框刷新新增项）。</summary>
    internal void NotifyTargetsChanged() => TargetsChanged?.Invoke();

    /// <summary>规则列表增删改后触发（labs 知识对象页订阅，刷新树）。</summary>
    public event Action? RulesChanged;

    /// <summary>从目录加载所有规则 agent（NodeType==Rule/CodeRule）。</summary>
    public async Task LoadAsync()
    {
        try
        {
            var all = await _catalog.ListAsync();
            await _dispatcher.InvokeAsync(() =>
            {
                Rules.Clear();
                foreach (var def in all)
                {
                    if (def.NodeType != NoteNodeType.Rule && def.NodeType != NoteNodeType.CodeRule) continue;
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
                        Memory = def.Memory,
                        MaxHistory = def.MaxHistory,
                        RequireToolApproval = def.RequireToolApproval,
                        Source = def.Source,
                        MarketplaceId = def.MarketplaceId,
                    };
                    Rules.Add(item);
                }
            });
        }
        catch (Exception ex)
        {
            StatusMessage = $"加载失败：{ex.Message}";
        }
    }

    /// <summary>保存规则（新增或更新）。labs 知识对象页规则/代码规则表单编辑器保存经此落库。</summary>
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
                Category = string.IsNullOrWhiteSpace(item.Category)
                    ? (item.NodeType == NoteNodeType.CodeRule ? "代码规则" : "规则")
                    : item.Category,
                SystemPrompt = item.SystemPrompt,
                Engine = string.IsNullOrWhiteSpace(item.Engine) ? "Rule" : item.Engine,
                NodeType = item.NodeType,
                ModelId = item.ModelId ?? string.Empty,
                Tools = item.Tools,
                ToolApprovals = item.ToolApprovals ?? new Dictionary<string, string>(),
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
                existing.Memory = item.Memory;
                existing.MaxHistory = item.MaxHistory;
                existing.RequireToolApproval = item.RequireToolApproval;
            }
            else
            {
                await LoadAsync();
            }

            StatusMessage = existing == null ? $"已创建：{item.Name}" : "已保存";
            TargetsChanged?.Invoke();
            RulesChanged?.Invoke();
            return (true, string.Empty);
        }
        catch (Exception ex)
        {
            return (false, $"保存失败：{ex.Message}");
        }
    }

    /// <summary>创建规则编辑 ViewModel（labs 知识对象页规则表单用；调用方应在打开前 await Mcp.EnsureLoadedAsync()）。</summary>
    public RuleEditViewModel CreateEditViewModel(ExpertItem? existing)
    {
        var tools = _catalog.GetAvailableTools();
        return new RuleEditViewModel(existing, tools, Mcp.Servers.ToList(), currentHost: _catalog.CurrentHost);
    }

    /// <summary>创建代码规则编辑 ViewModel（labs 知识对象页两栏用；NodeType=CodeRule，默认类别「代码规则」、默认 C# 模板）。</summary>
    public RuleEditViewModel CreateCodeRuleEditViewModel(ExpertItem? existing)
    {
        var tools = _catalog.GetAvailableTools();
        return new RuleEditViewModel(existing, tools, Mcp.Servers.ToList(), NoteNodeType.CodeRule, _catalog.CurrentHost);
    }
}
