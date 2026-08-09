using System;
using Acme.Mobile.Mvvm.ViewModels;

namespace Acme.Mobile.Mvvm.Navigation;

/// <summary>
/// 持有当前显示的 ViewModel，变更时触发事件通知 MainWindow 更新 ContentControl
/// </summary>
public class NavigationStore
{
    private ViewModelBase _currentViewModel = null!;

    public ViewModelBase CurrentViewModel
    {
        get => _currentViewModel;
        set
        {
            if (_currentViewModel != value)
            {
                _currentViewModel = value;
                OnCurrentViewModelChanged();
            }
        }
    }

    public event Action? CurrentViewModelChanged;

    private void OnCurrentViewModelChanged()
    {
        CurrentViewModelChanged?.Invoke();
    }
}