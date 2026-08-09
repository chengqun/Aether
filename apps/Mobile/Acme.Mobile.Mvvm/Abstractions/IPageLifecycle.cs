using System.Threading.Tasks;

namespace Acme.Mobile.Mvvm.Abstractions;

/// <summary>
/// 页面生命周期抽象
/// MAUI: IPageLifecycleAware (OnAppearingAsync/OnDisappearingAsync)
/// Avalonia: NavigableViewModelBase (OnActivatedAsync/OnDeactivatedAsync)
/// </summary>
public interface IPageLifecycle
{
    Task OnActivatedAsync();
    Task OnDeactivatedAsync();
}