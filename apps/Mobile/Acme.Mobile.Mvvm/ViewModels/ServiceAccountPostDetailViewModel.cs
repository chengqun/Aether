using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Acme.ServiceAccount.Domain;
using Acme.Mobile.Mvvm.Abstractions;

namespace Acme.Mobile.Mvvm.ViewModels;

/// <summary>
/// 服务号文章详情 ViewModel
/// </summary>
public partial class ServiceAccountPostDetailViewModel : NavigableViewModelBase
{
    private readonly IServiceAccountService? _serviceAccountService;

    [ObservableProperty]
    private Guid _postId;

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _content = string.Empty;

    [ObservableProperty]
    private string _serviceAccountName = string.Empty;

    [ObservableProperty]
    private string _contentType = string.Empty;

    [ObservableProperty]
    private DateTime _publishedAt;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    public ServiceAccountPostDetailViewModel(IServiceAccountService? serviceAccountService = null,
                                             IUIThreadDispatcher? uiThreadDispatcher = null)
    {
        _serviceAccountService = serviceAccountService;
        _uiThreadDispatcher = uiThreadDispatcher;
    }

    public void SetParameters(IDictionary<string, object> query)
    {
        if (query.TryGetValue("postId", out var id) && Guid.TryParse(id?.ToString(), out var guid))
        {
            PostId = guid;
        }
    }

    public override async Task OnActivatedAsync()
    {
        await base.OnActivatedAsync();
        await LoadAsync();
    }

    public async Task LoadAsync()
    {
        if (_serviceAccountService == null || PostId == Guid.Empty) return;

        try
        {
            IsLoading = true;

            var post = await Task.Run(() => _serviceAccountService.GetPostAsync(PostId));
            if (post != null)
            {
                _uiThreadDispatcher?.Post(() =>
                {
                    Title = post.Title;
                    Content = post.Content;
                    ServiceAccountName = post.ServiceAccountName;
                    ContentType = post.ContentType;
                    PublishedAt = post.PublishedAt;
                    StatusMessage = string.Empty;
                });
            }
            else
            {
                StatusMessage = "加载失败";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"加载失败: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }
}
