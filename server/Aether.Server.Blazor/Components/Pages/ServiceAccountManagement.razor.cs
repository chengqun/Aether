using Aether.Server.Application.Contracts.DTOs.ServiceAccounts;
using Aether.Server.Application.Contracts.Services.ServiceAccounts;
using Microsoft.JSInterop;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Aether.Server.Blazor.Components.Pages;

public partial class ServiceAccountManagement : AbpComponentBase
{
    protected List<ServiceAccountDto> serviceAccounts = new();
    protected List<ServiceAccountPostListItemDto> posts = new();
    protected ServiceAccountDto? selectedServiceAccount = null;

    protected bool isLoading = false;
    protected bool isLoadingPosts = false;
    protected bool isSaving = false;
    protected bool isEditing = false;
    protected bool showModal = false;
    protected bool showPostModal = false;

    // 编辑/新增表单
    protected ServiceAccountDto editingItem = new();
    protected Guid? editingId = null;

    // 发内容表单
    protected Guid postServiceAccountId;
    protected string postServiceAccountName = string.Empty;
    protected string postTitle = string.Empty;
    protected string postContentType = "Article";
    protected string postSummary = string.Empty;
    protected string postContent = string.Empty;
    protected string postCoverImageUrl = string.Empty;

    protected override async Task OnInitializedAsync()
    {
        await LoadDataAsync();
    }

    protected async Task LoadDataAsync()
    {
        isLoading = true;
        try
        {
            serviceAccounts = await ServiceAccountAppService.GetAllAsync();
        }
        catch (Exception ex)
        {
            await Notify.Error($"加载服务号列表失败: {ex.Message}");
        }
        finally
        {
            isLoading = false;
        }
    }

    // 内容列表
    protected async Task LoadPostsAsync(ServiceAccountDto sa)
    {
        selectedServiceAccount = sa;
        isLoadingPosts = true;
        try
        {
            posts = await ServiceAccountAppService.GetPostsAsync(sa.Id, 100, 0);
        }
        catch (Exception ex)
        {
            await Notify.Error($"加载内容列表失败: {ex.Message}");
        }
        finally
        {
            isLoadingPosts = false;
        }
    }

    protected void ClosePostsView()
    {
        selectedServiceAccount = null;
        posts = new();
    }

    protected async Task PinPost(ServiceAccountPostListItemDto post)
    {
        try
        {
            await ServiceAccountAppService.PinPostAsync(post.Id);
            await Notify.Success("已置顶");
            if (selectedServiceAccount != null)
                await LoadPostsAsync(selectedServiceAccount);
        }
        catch (Exception ex)
        {
            await Notify.Error($"置顶失败: {ex.Message}");
        }
    }

    protected async Task UnpinPost(ServiceAccountPostListItemDto post)
    {
        try
        {
            await ServiceAccountAppService.UnpinPostAsync(post.Id);
            await Notify.Success("已取消置顶");
            if (selectedServiceAccount != null)
                await LoadPostsAsync(selectedServiceAccount);
        }
        catch (Exception ex)
        {
            await Notify.Error($"取消置顶失败: {ex.Message}");
        }
    }

    protected async Task DeletePost(ServiceAccountPostListItemDto post)
    {
        if (!await JS.InvokeAsync<bool>("confirm", $"确定要删除内容「{post.Title}」吗？"))
        {
            return;
        }

        try
        {
            await ServiceAccountAppService.DeletePostAsync(post.Id);
            await Notify.Success("删除成功");
            if (selectedServiceAccount != null)
                await LoadPostsAsync(selectedServiceAccount);
        }
        catch (Exception ex)
        {
            await Notify.Error($"删除失败: {ex.Message}");
        }
    }

    // 服务号 CRUD
    protected void ShowCreateModal()
    {
        isEditing = false;
        editingId = null;
        editingItem = new ServiceAccountDto();
        showModal = true;
    }

    protected void ShowEditModal(ServiceAccountDto item)
    {
        isEditing = true;
        editingId = item.Id;
        editingItem = new ServiceAccountDto
        {
            Id = item.Id,
            Name = item.Name,
            AvatarUrl = item.AvatarUrl,
            Description = item.Description,
            Category = item.Category,
            IsVerified = item.IsVerified,
            WelcomeMessage = item.WelcomeMessage
        };
        showModal = true;
    }

    protected void CloseModal()
    {
        showModal = false;
    }

    protected async Task SaveServiceAccount()
    {
        isSaving = true;
        try
        {
            if (isEditing && editingId.HasValue)
            {
                await ServiceAccountAppService.UpdateAsync(editingId.Value, new UpdateServiceAccountDto
                {
                    Name = editingItem.Name,
                    AvatarUrl = editingItem.AvatarUrl,
                    Description = editingItem.Description,
                    Category = editingItem.Category,
                    IsVerified = editingItem.IsVerified,
                    WelcomeMessage = editingItem.WelcomeMessage
                });
                await Notify.Success("更新成功");
            }
            else
            {
                await ServiceAccountAppService.CreateAsync(new CreateServiceAccountDto
                {
                    Name = editingItem.Name,
                    AvatarUrl = editingItem.AvatarUrl,
                    Description = editingItem.Description,
                    Category = editingItem.Category
                });
                await Notify.Success("创建成功");
            }

            CloseModal();
            await LoadDataAsync();
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

    protected async Task DeleteServiceAccount(ServiceAccountDto item)
    {
        if (!await JS.InvokeAsync<bool>("confirm", $"确定要删除服务号「{item.Name}」吗？"))
        {
            return;
        }

        try
        {
            await ServiceAccountAppService.DeleteAsync(item.Id);
            await Notify.Success("删除成功");
            if (selectedServiceAccount?.Id == item.Id)
                ClosePostsView();
            await LoadDataAsync();
        }
        catch (Exception ex)
        {
            await Notify.Error($"删除失败: {ex.Message}");
        }
    }

    // 发内容相关
    protected void ShowPublishModal(ServiceAccountDto item)
    {
        postServiceAccountId = item.Id;
        postServiceAccountName = item.Name;
        postTitle = string.Empty;
        postContentType = "Article";
        postSummary = string.Empty;
        postContent = string.Empty;
        postCoverImageUrl = string.Empty;
        showPostModal = true;
    }

    protected void ClosePostModal()
    {
        showPostModal = false;
    }

    protected async Task PublishPost()
    {
        isSaving = true;
        try
        {
            await ServiceAccountAppService.PublishPostAsync(new CreateServiceAccountPostDto
            {
                ServiceAccountId = postServiceAccountId,
                Title = postTitle,
                ContentType = postContentType,
                Summary = postSummary,
                Content = postContent,
                CoverImageUrl = string.IsNullOrEmpty(postCoverImageUrl) ? null : postCoverImageUrl
            });
            await Notify.Success("内容发布成功");
            ClosePostModal();
            // 如果正在查看该服务号的内容，自动刷新
            if (selectedServiceAccount?.Id == postServiceAccountId)
                await LoadPostsAsync(selectedServiceAccount);
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
