using System.Collections.ObjectModel;
using Acme.Agents.AI;
using Acme.Notes.AgentSource;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Acme.Shell;

/// <summary>工作流页面 ViewModel：从自定义 Workflow 目录加载，展示为卡片，CRUD + 召唤到聊天。UI 无关。</summary>
public partial class WorkflowsViewModel : ObservableObject
{
    private readonly ICustomWorkflowCatalog _catalog;
    private readonly ICustomAgentCatalog _agentCatalog;
    private readonly ICustomWorkflowRunner _runner;
    private readonly IUiDispatcher _dispatcher;

    public WorkflowsViewModel(ICustomWorkflowCatalog catalog, ICustomAgentCatalog agentCatalog,
        ICustomWorkflowRunner runner, IUiDispatcher dispatcher)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _agentCatalog = agentCatalog ?? throw new ArgumentNullException(nameof(agentCatalog));
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    public ObservableCollection<WorkflowItem> Workflows { get; } = new();

    [ObservableProperty] private string _statusMessage = string.Empty;

    /// <summary>用户点"召唤到聊天"时触发（MainViewModelBase 订阅，创建 workflow 会话）。</summary>
    public event Action<WorkflowItem>? SummonRequested;

    /// <summary>工作流增删改时触发（通知 Notes 等同源列表刷新）。</summary>
    public event Action? WorkflowsChanged;

    public async Task LoadAsync()
    {
        try
        {
            var list = await _catalog.ListAsync();
            await _dispatcher.InvokeAsync(() =>
            {
                Workflows.Clear();
                foreach (var d in list)
                    Workflows.Add(ToItem(d));
            });
        }
        catch (Exception ex) { StatusMessage = $"加载失败：{ex.Message}"; }
    }

    public WorkflowItem CreateNew() => new() { Id = Guid.NewGuid(), Category = "工作流" };

    /// <summary>创建编辑弹窗 ViewModel（从目录加载可用 agent 列表供选择参与者/入口）。</summary>
    public async Task<WorkflowEditViewModel> CreateEditViewModelAsync(WorkflowItem? existing)
    {
        var agents = await _agentCatalog.ListAsync();
        var options = agents.Select(a => new WorkflowAgentOption(a.Id, a.Name)).ToList();
        return new WorkflowEditViewModel(existing, options);
    }

    public async Task<(bool Success, string Error)> SaveAsync(WorkflowItem item)
    {
        if (item == null || string.IsNullOrWhiteSpace(item.Name))
            return (false, "名称不能为空");
        if (item.EntryAgentId == Guid.Empty || !item.Participants.Contains(item.EntryAgentId))
            return (false, "入口 agent 必须在参与者内");
        try
        {
            await _catalog.SaveAsync(ToDefinition(item));
            _runner.Invalidate(item.Id);
            await LoadAsync();
            StatusMessage = "已保存";
            WorkflowsChanged?.Invoke();
            return (true, string.Empty);
        }
        catch (Exception ex) { return (false, $"保存失败：{ex.Message}"); }
    }

    [RelayCommand]
    private async Task DeleteAsync(WorkflowItem item)
    {
        if (item == null) return;
        try
        {
            await _catalog.DeleteAsync(item.Id);
            _runner.Invalidate(item.Id);
            await LoadAsync();
            StatusMessage = $"已删除：{item.Name}";
            WorkflowsChanged?.Invoke();
        }
        catch (Exception ex) { StatusMessage = $"删除失败：{ex.Message}"; }
    }

    [RelayCommand]
    private void Summon(WorkflowItem item)
    {
        if (item == null) return;
        item.IsSummoned = true;
        SummonRequested?.Invoke(item);
    }

    private static WorkflowItem ToItem(CustomWorkflowDefinition d) => new()
    {
        Id = d.Id,
        Name = d.Name,
        Description = d.Description,
        Category = string.IsNullOrWhiteSpace(d.Category) ? "工作流" : d.Category,
        EntryAgentId = d.EntryAgentId,
        Participants = d.Participants.ToList(),
        Handoffs = d.Handoffs.ToList(),
        HandoffInstructions = d.HandoffInstructions,
    };

    private static CustomWorkflowDefinition ToDefinition(WorkflowItem item) => new()
    {
        Id = item.Id,
        Name = item.Name.Trim(),
        Description = item.Description?.Trim() ?? string.Empty,
        Category = string.IsNullOrWhiteSpace(item.Category) ? "工作流" : item.Category.Trim(),
        EntryAgentId = item.EntryAgentId,
        Participants = item.Participants.ToList(),
        Handoffs = item.Handoffs.ToList(),
        HandoffInstructions = item.HandoffInstructions,
    };
}

/// <summary>可用 agent 选项（workflow 编辑时选参与者/入口）。</summary>
public sealed record WorkflowAgentOption(Guid Id, string Name);
