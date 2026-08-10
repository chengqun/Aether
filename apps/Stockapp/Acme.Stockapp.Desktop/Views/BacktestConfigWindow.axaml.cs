using System.ComponentModel;
using Acme.Desktop.Shared.Shell;
using Acme.Stockapp.Desktop.ViewModels;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Acme.Stockapp.Desktop.Views;

/// <summary>回测配置弹窗。DataContext = TradingViewModel（复用配置属性 + RunCommand）。
/// 点"开始回测"后 RunCommand 执行，IsRunning 变 true 时自动关闭弹窗。</summary>
public partial class BacktestConfigWindow : ThemedWindow
{
    public BacktestConfigWindow() { InitializeComponent(); }

    public BacktestConfigWindow(TradingViewModel vm) : this()
    {
        DataContext = vm;
        vm.PropertyChanged += OnVmPropertyChanged;
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // IsRunning 从 true → false（回测完成）时关闭弹窗
        if (e.PropertyName == nameof(TradingViewModel.IsRunning) && DataContext is TradingViewModel vm && !vm.IsRunning)
        {
            Close();
        }
    }
}
