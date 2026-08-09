using Aether.Server.Application.Contracts.DTOs.VersionConfig;
using Aether.Server.Application.Contracts.Services.VersionConfig;
using Aether.Server.Domain.Shared.Enums;
using Microsoft.JSInterop;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Volo.Abp.Application.Dtos;

namespace Aether.Server.Blazor.Components.Pages;

public partial class VersionConfigManagement : AbpComponentBase
{
    protected List<VersionConfigDto> versionConfigs = new();
    protected int totalCount = 0;
    protected int currentPage = 1;
    protected int pageSize = 10;
    protected int totalPages => (int)Math.Ceiling((double)totalCount / pageSize);
    protected string jumpPage = string.Empty;
    protected int filterPlatform = -1;

    protected bool isLoading = false;
    protected bool isSaving = false;
    protected bool isEditing = false;
    protected bool showModal = false;

    protected VersionConfigDto editingConfig = new();
    protected Guid? editingId = null;

    protected int editingPlatformInt
    {
        get => (int)editingConfig.Platform;
        set => editingConfig.Platform = (AppPlatform)value;
    }

    protected override async Task OnInitializedAsync()
    {
        await LoadVersionConfigsAsync();
    }

    protected async Task LoadVersionConfigsAsync()
    {
        isLoading = true;
        try
        {
            var result = await VersionConfigAppService.GetListAsync(new PagedAndSortedResultRequestDto
            {
                MaxResultCount = pageSize,
                SkipCount = (currentPage - 1) * pageSize,
                Sorting = "Platform, VersionCode DESC"
            });
            versionConfigs = result.Items.ToList();
            totalCount = (int)result.TotalCount;

            if (filterPlatform >= 0)
            {
                var platform = (AppPlatform)filterPlatform;
                versionConfigs = versionConfigs.Where(v => v.Platform == platform).ToList();
            }
        }
        catch (Exception ex)
        {
            await Notify.Error($"加载版本配置列表失败: {ex.Message}");
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
            await LoadVersionConfigsAsync();
        }
    }

    protected async Task NextPage()
    {
        if (currentPage < totalPages)
        {
            currentPage++;
            await LoadVersionConfigsAsync();
        }
    }

    protected async Task GoToPage(int page)
    {
        if (page >= 1 && page <= totalPages && page != currentPage)
        {
            currentPage = page;
            await LoadVersionConfigsAsync();
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

        if (start > 2) pages.Add(-1);
        for (int i = start; i <= end; i++) pages.Add(i);
        if (end < totalPages - 1) pages.Add(-2);

        pages.Add(totalPages);
        return pages;
    }

    protected void ShowCreateModal()
    {
        isEditing = false;
        editingId = null;
        editingConfig = new VersionConfigDto();
        showModal = true;
    }

    protected void ShowEditModal(VersionConfigDto config)
    {
        isEditing = true;
        editingId = config.Id;
        editingConfig = new VersionConfigDto
        {
            Id = config.Id,
            Platform = config.Platform,
            VersionCode = config.VersionCode,
            VersionName = config.VersionName,
            UpdateUrl = config.UpdateUrl,
            UpdateNote = config.UpdateNote,
            IsForce = config.IsForce
        };
        showModal = true;
    }

    protected void CloseModal()
    {
        showModal = false;
    }

    protected async Task SaveVersionConfig()
    {
        isSaving = true;
        try
        {
            if (isEditing && editingId.HasValue)
            {
                await VersionConfigAppService.UpdateAsync(editingId.Value, new VersionConfigUpdateDto
                {
                    Platform = editingConfig.Platform,
                    VersionCode = editingConfig.VersionCode,
                    VersionName = editingConfig.VersionName,
                    UpdateUrl = editingConfig.UpdateUrl,
                    UpdateNote = editingConfig.UpdateNote,
                    IsForce = editingConfig.IsForce
                });
                await Notify.Success("更新成功");
            }
            else
            {
                await VersionConfigAppService.CreateAsync(new VersionConfigCreateDto
                {
                    Platform = editingConfig.Platform,
                    VersionCode = editingConfig.VersionCode,
                    VersionName = editingConfig.VersionName,
                    UpdateUrl = editingConfig.UpdateUrl,
                    UpdateNote = editingConfig.UpdateNote,
                    IsForce = editingConfig.IsForce
                });
                await Notify.Success("创建成功");
            }

            CloseModal();
            await LoadVersionConfigsAsync();
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

    protected async Task DeleteVersionConfig(VersionConfigDto config)
    {
        if (!await JS.InvokeAsync<bool>("confirm", $"确定要删除 {GetPlatformName(config.Platform)} {config.VersionName} 吗？"))
        {
            return;
        }

        try
        {
            await VersionConfigAppService.DeleteAsync(config.Id);
            await Notify.Success("删除成功");
            await LoadVersionConfigsAsync();
        }
        catch (Exception ex)
        {
            await Notify.Error($"删除失败: {ex.Message}");
        }
    }

    protected static string GetPlatformName(AppPlatform platform) => platform switch
    {
        AppPlatform.iOS => "iOS",
        AppPlatform.Android => "Android",
        AppPlatform.Windows => "Windows",
        AppPlatform.MacCatalyst => "Mac",
        AppPlatform.Linux => "Linux",
        _ => platform.ToString()
    };
}
