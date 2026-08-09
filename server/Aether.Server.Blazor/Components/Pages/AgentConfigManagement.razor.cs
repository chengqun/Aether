using Aether.Server.Application.Contracts.DTOs.AgentConfig;
using Aether.Server.Application.Contracts.Services.AgentConfig;
using Microsoft.JSInterop;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Volo.Abp.Application.Dtos;

namespace Aether.Server.Blazor.Components.Pages;

/// <summary>
/// NoteNode 管理页（替代旧 AgentConfig 管理）：管理市场发布的 Agent/Workflow 源文本。
/// </summary>
public partial class AgentConfigManagement : AbpComponentBase
{
    protected List<NoteNodeDto> noteNodes = new();
    protected long totalCount = 0;
    protected int currentPage = 1;
    protected int pageSize = 10;
    protected int totalPages => totalCount > 0 ? (int)Math.Ceiling((double)totalCount / pageSize) : 1;
    protected string jumpPage = string.Empty;

    protected bool isLoading = false;
    protected bool show_modal = false;
    protected bool isEditing = false;
    protected NoteNodeCreateDto editing = new();
    protected Guid? editingId = null;

    protected bool isSaving = false;

    // 过滤
    protected string filterNodeType = string.Empty; // "" = 全部
    protected string filterPublished = string.Empty; // "" = 全部

    protected override async Task OnInitializedAsync()
    {
        await LoadAsync();
    }

    protected async Task LoadAsync()
    {
        isLoading = true;
        try
        {
            var result = await NoteNodeAppService.GetListAsync(
                new PagedAndSortedResultRequestDto
                {
                    MaxResultCount = pageSize,
                    SkipCount = (currentPage - 1) * pageSize,
                    Sorting = "Title"
                }
            );
            var all = result.Items.ToList();
            // 客户端过滤（NodeType / IsPublished）
            if (!string.IsNullOrEmpty(filterNodeType))
                all = all.Where(n => n.NodeType == filterNodeType).ToList();
            if (filterPublished == "yes") all = all.Where(n => n.IsPublished).ToList();
            else if (filterPublished == "no") all = all.Where(n => !n.IsPublished).ToList();
            noteNodes = all;
            totalCount = result.TotalCount;
        }
        catch (Exception ex)
        {
            await Notify.Error($"加载失败: {ex.Message}");
        }
        finally
        {
            isLoading = false;
        }
    }

    protected void ShowCreateModal()
    {
        isEditing = false;
        editingId = null;
        editing = new NoteNodeCreateDto { NodeType = "Agent", Category = "普通对话" };
        show_modal = true;
    }

    protected void ShowEditModal(NoteNodeDto node)
    {
        isEditing = true;
        editingId = node.Id;
        editing = new NoteNodeCreateDto
        {
            NodeType = node.NodeType,
            Title = node.Title,
            Content = node.Content,
            IsPublished = node.IsPublished,
            Description = node.Description,
            Category = node.Category,
            MinAppVersion = node.MinAppVersion,
        };
        show_modal = true;
    }

    protected void CloseModal() => show_modal = false;

    protected async Task SaveAsync()
    {
        isSaving = true;
        try
        {
            if (isEditing && editingId.HasValue)
            {
                await NoteNodeAppService.UpdateAsync(editingId.Value, new NoteNodeUpdateDto
                {
                    NodeType = editing.NodeType,
                    Title = editing.Title,
                    Content = editing.Content,
                    IsPublished = editing.IsPublished,
                    Description = editing.Description,
                    Category = editing.Category,
                    MinAppVersion = editing.MinAppVersion,
                });
                await Notify.Success("更新成功");
            }
            else
            {
                await NoteNodeAppService.CreateAsync(new NoteNodeCreateDto
                {
                    NodeType = editing.NodeType,
                    Title = editing.Title,
                    Content = editing.Content,
                    IsPublished = editing.IsPublished,
                    Description = editing.Description,
                    Category = editing.Category,
                    MinAppVersion = editing.MinAppVersion,
                });
                await Notify.Success("创建成功");
            }

            CloseModal();
            await LoadAsync();
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

    protected async Task DeleteAsync(NoteNodeDto node)
    {
        if (!await JS.InvokeAsync<bool>("confirm", $"确定要删除 \"{node.Title}\" 吗？"))
            return;
        try
        {
            await NoteNodeAppService.DeleteAsync(node.Id);
            await Notify.Success("删除成功");
            await LoadAsync();
        }
        catch (Exception ex)
        {
            await Notify.Error($"删除失败: {ex.Message}");
        }
    }

    /// <summary>上架到市场</summary>
    protected async Task PublishAsync(NoteNodeDto node)
    {
        try
        {
            await AgentMarketplaceAppService.PublishAsync(node.Id);
            await Notify.Success($"已发布到市场：{node.Title}");
            await LoadAsync();
        }
        catch (Exception ex) { await Notify.Error(ex.Message); }
    }

    /// <summary>从市场下架</summary>
    protected async Task UnpublishAsync(NoteNodeDto node)
    {
        try
        {
            await AgentMarketplaceAppService.UnpublishAsync(node.Id);
            await Notify.Success($"已从市场下架：{node.Title}");
            await LoadAsync();
        }
        catch (Exception ex) { await Notify.Error(ex.Message); }
    }

    protected async Task OnFilterChanged() { currentPage = 1; await LoadAsync(); }

    // 分页
    protected async Task PrevPage() { if (currentPage > 1) { currentPage--; await LoadAsync(); } }
    protected async Task NextPage() { if (currentPage < totalPages) { currentPage++; await LoadAsync(); } }
    protected async Task GoToPage(int page) { if (page >= 1 && page <= totalPages && page != currentPage) { currentPage = page; await LoadAsync(); } }
    protected async Task GoToPage(string pageStr) { if (int.TryParse(pageStr, out var page) && page >= 1 && page <= totalPages) await GoToPage(page); }

    protected IEnumerable<int> GetPageNumbers()
    {
        if (totalPages <= 7) return Enumerable.Range(1, totalPages);
        var pages = new List<int> { 1 };
        int start = Math.Max(2, currentPage - 2);
        int end = Math.Min(totalPages - 1, currentPage + 2);
        if (start > 2) pages.Add(-1);
        for (int i = start; i <= end; i++) pages.Add(i);
        if (end < totalPages - 1) pages.Add(-2);
        pages.Add(totalPages);
        return pages;
    }

    protected string TypeBadge(string nodeType) => nodeType == "Workflow" ? "专家团" : (nodeType == "Note" ? "笔记" : "Agent");
}
