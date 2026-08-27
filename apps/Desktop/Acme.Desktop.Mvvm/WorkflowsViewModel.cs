using System.Collections.ObjectModel;
using Aether.AI.Core.Agents;
using Acme.Notes.Domain.Agents;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Acme.Desktop.Mvvm;

/// <summary>工作流列表 VM：从自定义 Workflow 目录加载。labs 知识对象页承接创建+编辑；本 VM 供聊天下拉 + labs 编辑保存用。UI 无关。</summary>
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

    /// <summary>可选目标集合变更（新建/删除/catalog reload）时触发。ChatWorkspaceViewModel 订阅刷新统一下拉池。</summary>
    public event Action? TargetsChanged;

    /// <summary>外部触发目标集合变更通知（catalog Changed reload 后调用，让聊天下拉框刷新新增项）。</summary>
    internal void NotifyTargetsChanged() => TargetsChanged?.Invoke();

    /// <summary>工作流增删改时触发（通知同源列表刷新）。</summary>
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
                {
                    Workflows.Add(ToItem(d));
                }
            });
        }
        catch (Exception ex) { StatusMessage = $"加载失败：{ex.Message}"; }
    }

    /// <summary>创建编辑弹窗 ViewModel（从目录加载可用 agent 列表供选择参与者/入口）。labs 知识对象页工作流表单编辑器经此构建。</summary>
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
            TargetsChanged?.Invoke();
            return (true, string.Empty);
        }
        catch (Exception ex) { return (false, $"保存失败：{ex.Message}"); }
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
