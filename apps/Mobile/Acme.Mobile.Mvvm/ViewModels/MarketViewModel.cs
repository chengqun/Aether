using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Aether.AI.Abstractions;
using Acme.Notes.Domain;
using Acme.Notes.Domain.Agents;
using Acme.Mobile.Mvvm.Events;
using Acme.Mobile.Mvvm.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Acme.Mobile.Mvvm.ViewModels;

/// <summary>
/// 市场 VM：浏览市场 Agent + 召唤到本地（<see cref="ICustomAgentCatalog"/> 持久化为 Agent 文件；
/// Workflow 类经 <see cref="ICustomWorkflowCatalog"/> 落 workflows/ 目录）。
/// 召唤 = 把市场 Agent 详情写成定义（Source=Marketplace, MarketplaceId 标记）→ SaveAsync。
/// 去掉发布本地——MAUI 无本地新建 Agent 入口，Agent 全部来自市场召唤。
/// </summary>
public partial class MarketViewModel : ObservableObject
{
    private readonly IAgentMarketplaceService _marketplace;
    private readonly ICustomAgentCatalog _agentCatalog;
    private readonly ICustomWorkflowCatalog? _workflowCatalog;
    private readonly IEventBusService? _eventBusService;

    public ObservableCollection<MarketplaceAgentDto> Agents { get; } = new();

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _status = "加载中...";

    public MarketViewModel(IAgentMarketplaceService marketplace, ICustomAgentCatalog agentCatalog,
        ICustomWorkflowCatalog? workflowCatalog = null,
        IEventBusService? eventBusService = null)
    {
        _marketplace = marketplace;
        _agentCatalog = agentCatalog;
        _workflowCatalog = workflowCatalog;
        _eventBusService = eventBusService;
        _ = LoadAsync();
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        IsLoading = true;
        Status = "加载市场...";
        try
        {
            var list = await Task.Run(() => _marketplace.GetMarketplaceAsync());
            // 标记已召唤：本地目录中 MarketplaceId 命中的即为已召唤（agent + workflow 合并）
            var localDefs = await _agentCatalog.ListAsync();
            var localMarketplaceIds = localDefs
                .Where(d => d.MarketplaceId.HasValue)
                .Select(d => d.MarketplaceId!.Value)
                .ToHashSet();
            Agents.Clear();
            if (list != null)
            {
                foreach (var a in list) { a.IsSummoned = localMarketplaceIds.Contains(a.Id); Agents.Add(a); }
                Status = $"共 {Agents.Count} 个市场 Agent";
            }
            else Status = "加载失败";
        }
        catch (Exception ex) { Status = $"加载失败: {ex.Message}"; }
        finally { IsLoading = false; }
    }

    [RelayCommand]
    private async Task SummonAsync(MarketplaceAgentDto dto)
    {
        try
        {
            Status = $"召唤 {dto.AgentName}...";
            var detail = await Task.Run(() => _marketplace.GetAgentDetailAsync(dto.Id));
            if (detail == null) { Status = "召唤失败:获取详情为空"; return; }
            if (string.IsNullOrWhiteSpace(detail.Content)) { Status = "召唤失败:源文本为空"; return; }

            // 召唤 = 把云端详情落本地（MarketplaceId 标记来源），按 Kind 选 Agent/Workflow
            var isWorkflow = string.Equals(detail.Kind, "Workflow", StringComparison.OrdinalIgnoreCase);
            if (isWorkflow && _workflowCatalog is not null)
            {
                // Workflow 类市场项走 workflow catalog → 落 labs/workflows/*.workflow.json（不错类为 Agent）
                var wf = new CustomWorkflowDefinition
                {
                    Id = Guid.NewGuid(),
                    Name = detail.AgentName,
                    Description = detail.Description,
                    Category = string.IsNullOrEmpty(detail.Category) ? "工作流" : detail.Category,
                };
                await _workflowCatalog.SaveAsync(wf);
            }
            else
            {
                var def = new CustomAgentDefinition
                {
                    Id = Guid.NewGuid(),
                    Name = detail.AgentName,
                    DisplayName = detail.AgentName,
                    Description = detail.Description,
                    Category = string.IsNullOrEmpty(detail.Category) ? "普通对话" : detail.Category,
                    SystemPrompt = detail.Content,
                    Source = "Marketplace",
                    MarketplaceId = dto.Id,
                    MarketplaceSyncedAt = detail.LastModifiedAt,
                    NodeType = isWorkflow ? NoteNodeType.Workflow : NoteNodeType.Agent,
                };
                await _agentCatalog.SaveAsync(def);
            }

            dto.IsSummoned = true;
            Status = $"已召唤 {dto.AgentName},可在通讯录使用";
            // 通知通讯录刷新 AI 助手分组，切回通讯录即可看到新召唤的 Agent
            _eventBusService?.Publish(new AgentsChangedEvent("summoned"));
        }
        catch (Exception ex) { Status = $"召唤失败: {ex.Message}"; }
    }
}
