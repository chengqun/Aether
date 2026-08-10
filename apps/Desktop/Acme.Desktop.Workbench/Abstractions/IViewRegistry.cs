using Avalonia.Controls;

namespace Acme.Desktop.Workbench.Abstractions;

/// <summary>
/// 视图注册表：view-key → 视图/VM 工厂。替代 MainWindow 硬编码视图 + Application.DataTemplates。
/// <see cref="WorkspaceView"/> 遍历布局树遇到叶子时，按 panel Key 经此解析视图（非硬编码类型映射）。
/// 与 <see cref="IRegionManager"/> 区分：ViewRegistry 解析"一个 panel 的视图"，RegionManager 管"往哪个命名槽里动态增删 panel"。
/// </summary>
public interface IViewRegistry
{
    /// <summary>注册一个 view-key 的视图工厂 + 可选 VM 工厂。</summary>
    void Register(string key, System.Func<Control> viewFactory, System.Func<object?>? vmFactory = null);

    /// <summary>按 key 创建视图（未注册返回 null）。</summary>
    Control? CreateView(string key);

    /// <summary>按 key 创建 VM（未注册返回 null）。</summary>
    object? CreateViewModel(string key);
}
