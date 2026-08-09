using System;
using System.Threading.Tasks;
using Acme.Mobile.Mvvm.ViewModels;

namespace Acme.Mobile.Mvvm.Navigation;

/// <summary>
/// 导航服务抽象，替代 MAUI Shell 导航
/// </summary>
public interface INavigationService
{
    /// <summary>
    /// 导航到指定 ViewModel（从 DI 容器解析）
    /// </summary>
    Task NavigateToAsync<TViewModel>() where TViewModel : ViewModelBase;

    /// <summary>
    /// 导航到指定 ViewModel 并配置参数
    /// </summary>
    Task NavigateToAsync<TViewModel>(Action<TViewModel> configure) where TViewModel : ViewModelBase;

    /// <summary>
    /// 直接导航到已创建的 ViewModel 实例
    /// </summary>
    void NavigateTo(ViewModelBase viewModel);

    /// <summary>
    /// 当前显示的 ViewModel
    /// </summary>
    ViewModelBase CurrentViewModel { get; }
}