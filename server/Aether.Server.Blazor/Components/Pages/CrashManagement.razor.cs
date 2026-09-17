using Aether.Server.Application.Contracts.DTOs.Crash;
using Aether.Server.Application.Contracts.Services.Crash;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Aether.Server.Blazor.Components.Pages;

public partial class CrashManagement : AbpComponentBase
{
    protected List<CrashSummaryDto> summaryData = new();
    protected List<CrashDetailDto> detailData = new();
    protected List<string> appNames = new();
    protected List<string> sourceNames = new();
    protected bool isLoading = false;
    protected CrashDetailDto? selectedCrash;

    // 筛选条件
    protected string filterApp = string.Empty;
    protected string filterSource = string.Empty;
    protected string filterSeverity = string.Empty;
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
            detailData = await CrashAppService.GetCrashDetailsAsync(
                string.IsNullOrWhiteSpace(filterApp) ? null : filterApp,
                string.IsNullOrWhiteSpace(filterSource) ? null : filterSource,
                string.IsNullOrWhiteSpace(filterSeverity) ? null : int.Parse(filterSeverity),
                filterStartDate,
                filterEndDate);

            // 从明细数据生成汇总（同 AgentUsage 的客户端 group-by 手法）
            summaryData = detailData
                .GroupBy(r => new { r.ExceptionType, r.Source })
                .Select(g => new CrashSummaryDto
                {
                    ExceptionType = g.Key.ExceptionType,
                    Source = g.Key.Source,
                    TotalCount = g.Sum(r => (long)r.Count),
                    RecordCount = g.Count(),
                    SessionCount = g.Select(r => r.SessionId).Distinct().Count(),
                    Apps = string.Join(", ", g.Select(r => r.App).Distinct()),
                    LastOccurredAt = g.Max(r => r.OccurredAt)
                })
                .OrderByDescending(s => s.LastOccurredAt)
                .ToList();

            // 提取不重复的应用/来源列表（供筛选下拉）
            appNames = detailData
                .Select(r => r.App)
                .Distinct()
                .OrderBy(n => n)
                .ToList();
            sourceNames = detailData
                .Select(r => r.Source)
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
        filterApp = string.Empty;
        filterSource = string.Empty;
        filterSeverity = string.Empty;
        filterStartDate = null;
        filterEndDate = null;
        await LoadData();
    }

    protected void ShowStack(CrashDetailDto item)
    {
        selectedCrash = item;
    }

    protected void CloseStack()
    {
        selectedCrash = null;
    }
}
