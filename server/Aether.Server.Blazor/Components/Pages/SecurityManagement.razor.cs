using Aether.Server.Application.Contracts.DTOs.Security;
using Aether.Server.Application.Contracts.DTOs.Moment;
using Aether.Server.Application.Contracts.Services.Security;
using Aether.Server.Application.Contracts.Services.Moment;
using Microsoft.JSInterop;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Volo.Abp.Application.Dtos;

namespace Aether.Server.Blazor.Components.Pages;

public partial class SecurityManagement : AbpComponentBase
{
    protected List<SecurityDto> securities = new();
    protected int totalCount = 0;
    protected int currentPage = 1;
    protected int pageSize = 10;
    protected int totalPages => (int)Math.Ceiling((double)totalCount / pageSize);
    protected string jumpPage = string.Empty;

    protected bool isLoading = false;
    protected bool isSaving = false;
    protected bool isSyncing = false;
    protected bool isSyncingPicking = false;
    protected bool isEditing = false;
    protected bool showModal = false;
    protected bool showMomentModal = false;

    // 编辑/新增表单
    protected SecurityDto editingSecurity = new();
    protected int? editingId = null;

    // 发动态表单
    protected int momentStockId = 0;
    protected string momentStockName = string.Empty;
    protected string momentContent = string.Empty;
    protected string momentVisibility = "Friends";

    protected async Task SyncFromExternal()
    {
        isSyncing = true;
        try
        {
            var result = await SecuritySyncService.SyncFromExternalAsync();
            await Notify.Success($"同步完成：共{result.Total}条，新增{result.Added}条，更新{result.Updated}条");
            await LoadSecuritiesAsync();
        }
        catch (Exception ex)
        {
            await Notify.Error($"同步失败: {ex.Message}");
        }
        finally
        {
            isSyncing = false;
        }
    }

    protected async Task SyncStockPickingMoments()
    {
        isSyncingPicking = true;
        try
        {
            var result = await SecuritySyncService.SyncStockPickingMomentsAsync();
            await Notify.Success($"同步完成：{result.TotalStrategies}个策略，发布{result.PublishedMoments}条朋友圈，跳过{result.SkippedStocks}只股票");
        }
        catch (Exception ex)
        {
            await Notify.Error($"同步选股策略失败: {ex.Message}");
        }
        finally
        {
            isSyncingPicking = false;
        }
    }

    protected override async Task OnInitializedAsync()
    {
        await LoadSecuritiesAsync();
    }

    protected async Task LoadSecuritiesAsync()
    {
        isLoading = true;
        try
        {
            var result = await SecurityAppService.GetListAsync(new PagedAndSortedResultRequestDto
            {
                MaxResultCount = pageSize,
                SkipCount = (currentPage - 1) * pageSize,
                Sorting = "Id"
            });
            securities = result.Items.ToList();
            totalCount = (int)result.TotalCount;
        }
        catch (Exception ex)
        {
            await Notify.Error($"加载股票列表失败: {ex.Message}");
        }
        finally
        {
            isLoading = false;
        }
    }

    protected async Task PrevPage()
    {
        if (currentPage > 1)
        {
            currentPage--;
            await LoadSecuritiesAsync();
        }
    }

    protected async Task NextPage()
    {
        if (currentPage < totalPages)
        {
            currentPage++;
            await LoadSecuritiesAsync();
        }
    }

    protected async Task GoToPage(int page)
    {
        if (page >= 1 && page <= totalPages && page != currentPage)
        {
            currentPage = page;
            await LoadSecuritiesAsync();
        }
    }

    protected async Task GoToPage(string pageStr)
    {
        if (int.TryParse(pageStr, out var page) && page >= 1 && page <= totalPages)
        {
            await GoToPage(page);
        }
    }

    protected IEnumerable<int> GetPageNumbers()
    {
        if (totalPages <= 7)
            return Enumerable.Range(1, totalPages);

        var pages = new List<int> { 1 };

        int start = Math.Max(2, currentPage - 2);
        int end = Math.Min(totalPages - 1, currentPage + 2);

        if (start > 2) pages.Add(-1); // 省略号
        for (int i = start; i <= end; i++) pages.Add(i);
        if (end < totalPages - 1) pages.Add(-2); // 省略号

        pages.Add(totalPages);
        return pages;
    }

    protected void ShowCreateModal()
    {
        isEditing = false;
        editingId = null;
        editingSecurity = new SecurityDto();
        showModal = true;
    }

    protected void ShowEditModal(SecurityDto security)
    {
        isEditing = true;
        editingId = security.Id;
        editingSecurity = new SecurityDto
        {
            Id = security.Id,
            Code = security.Code,
            Name = security.Name,
            Type = security.Type,
            Exchange = security.Exchange,
            BaseCode = security.BaseCode
        };
        showModal = true;
    }

    protected void CloseModal()
    {
        showModal = false;
    }

    protected async Task SaveSecurity()
    {
        isSaving = true;
        try
        {
            if (isEditing && editingId.HasValue)
            {
                await SecurityAppService.UpdateAsync(editingId.Value, new SecurityUpdateDto
                {
                    Name = editingSecurity.Name,
                    Type = editingSecurity.Type,
                    Exchange = editingSecurity.Exchange,
                    BaseCode = editingSecurity.BaseCode
                });
                await Notify.Success("更新成功");
            }
            else
            {
                await SecurityAppService.CreateAsync(new SecurityCreateDto
                {
                    Code = editingSecurity.Code,
                    Name = editingSecurity.Name,
                    Type = editingSecurity.Type,
                    Exchange = editingSecurity.Exchange,
                    BaseCode = editingSecurity.BaseCode
                });
                await Notify.Success("创建成功");
            }

            CloseModal();
            await LoadSecuritiesAsync();
        }
        catch (Exception ex)
        {
            await Notify.Error($"保存失败: {ex.Message}");
        }
        finally
        {
            isSaving = false;
        }
    }

    protected async Task DeleteSecurity(SecurityDto security)
    {
        if (!await JS.InvokeAsync<bool>("confirm", $"确定要删除 {security.Name}({security.Code}) 吗？"))
        {
            return;
        }

        try
        {
            await SecurityAppService.DeleteAsync(security.Id);
            await Notify.Success("删除成功");
            await LoadSecuritiesAsync();
        }
        catch (Exception ex)
        {
            await Notify.Error($"删除失败: {ex.Message}");
        }
    }

    // 发动态相关
    protected void ShowCreateMomentModal(SecurityDto security)
    {
        momentStockId = security.Id;
        momentStockName = $"{security.Name}({security.Code})";
        momentContent = string.Empty;
        momentVisibility = "Friends";
        showMomentModal = true;
    }

    protected void CloseMomentModal()
    {
        showMomentModal = false;
    }

    protected async Task CreateMoment()
    {
        isSaving = true;
        try
        {
            await MomentAppService.CreateMomentAsync(new CreateMomentDto
            {
                Content = momentContent,
                Type = "Stock",
                Visibility = momentVisibility,
                StockId = momentStockId
            });
            await Notify.Success("动态发布成功");
            CloseMomentModal();
        }
        catch (Exception ex)
        {
            await Notify.Error($"发布失败: {ex.Message}");
        }
        finally
        {
            isSaving = false;
        }
    }
}