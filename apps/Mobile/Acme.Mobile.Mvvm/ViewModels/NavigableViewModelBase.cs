using System;
using System.Threading.Tasks;
using Acme.Mobile.Mvvm.Abstractions;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Acme.Mobile.Mvvm.ViewModels;

/// <summary>
/// 可导航的 ViewModel 基类，替代 MAUI 的 IPageLifecycleAware
/// NavigationService 在切换 ViewModel 时自动调用 OnActivatedAsync/OnDeactivatedAsync
/// </summary>
public abstract class NavigableViewModelBase : ViewModelBase, IPageLifecycle
{
    private bool _isActive;

    /// <summary>
    /// UI 线程调度器，由子类通过构造函数注入
    /// </summary>
    protected IUIThreadDispatcher? _uiThreadDispatcher;

    /// <summary>
    /// 当前是否为活动内容
    /// </summary>
    public bool IsActive
    {
        get => _isActive;
        set => SetProperty(ref _isActive, value);
    }

    /// <summary>
    /// 当此 ViewModel 成为活动内容时调用（≈ MAUI OnAppearingAsync）
    /// </summary>
    public virtual Task OnActivatedAsync() => Task.CompletedTask;

    /// <summary>
    /// 当此 ViewModel 被替换时调用（≈ MAUI OnDisappearingAsync）
    /// </summary>
    public virtual Task OnDeactivatedAsync()
    {
        IsActive = false;
        return Task.CompletedTask;
    }
}
