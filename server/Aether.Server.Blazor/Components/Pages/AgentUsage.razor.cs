using Aether.Server.Application.Contracts.DTOs.AgentUsage;
using Aether.Server.Application.Contracts.Services.AgentUsage;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Aether.Server.Blazor.Components.Pages;

public partial class AgentUsage : AbpComponentBase
{
    protected List<AgentUsageSummaryDto> summaryData = new();
    protected List<AgentUsageDetailDto> detailData = new();
    protected List<string> agentNames = new();
    protected bool isLoading = false;

    // 筛选条件
    protected string filterAgentName = string.Empty;
    protected DateTime? filterStartDate;
    protected DateTime? filterEndDate;

    protected override async Task OnInitializedAsync()
    {
        await LoadData();
    }

    protected async Task LoadData()
    {
        isLoading = true;
        try
        {
            // 加载明细数据
            detailData = await AgentUsageAppService.GetUsageDetailsAsync(
                string.IsNullOrWhiteSpace(filterAgentName) ? null : filterAgentName,
                filterStartDate,
                filterEndDate);

            // 从明细数据生成汇总
            summaryData = detailData
                .GroupBy(r => new { r.AgentName, r.AgentKind })
                .Select(g => new AgentUsageSummaryDto
                {
                    AgentName = g.Key.AgentName,
                    AgentKind = g.Key.AgentKind,
                    TotalInputTokens = g.Sum(r => (long)r.InputTokens),
                    TotalOutputTokens = g.Sum(r => (long)r.OutputTokens),
                    TotalTokens = g.Sum(r => (long)r.TotalTokens),
                    CallCount = g.Count()
                })
                .OrderByDescending(s => s.TotalTokens)
                .ToList();

            // 提取不重复的 Agent 名称列表
            agentNames = detailData
                .Select(r => r.AgentName)
                .Distinct()
                .OrderBy(n => n)
                .ToList();
        }
        catch (Exception ex)
        {
            await Notify.Error($"加载数据失败: {ex.Message}");
        }
        finally
        {
            isLoading = false;
        }
    }

    protected async Task ResetFilters()
    {
        filterAgentName = string.Empty;
        filterStartDate = null;
        filterEndDate = null;
        await LoadData();
    }

    protected static string GetAgentKindName(int agentKind)
    {
        return agentKind switch
        {
            0 => "Agent",
            1 => "Workflow",
            2 => "RuleBased",
            _ => agentKind.ToString()
        };
    }
}