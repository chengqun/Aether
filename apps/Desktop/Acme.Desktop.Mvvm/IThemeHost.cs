namespace Acme.Desktop.Mvvm;

/// <summary>
/// 主题宿主接口：让设置层（<c>Acme.Desktop.Shared</c> 的 SettingsVM / SettingsWindow / TraceWindow，不引用 Workbench）
/// 跨层访问 <see cref="WorkbenchShellViewModel"/> 的主题状态而不引入项目引用。
/// 实现在 Workbench 层；消费方经 <c>GetService&lt;MainViewModelBase&gt;() as IThemeHost</c> 取。
/// <see cref="CustomThemeJson"/> + <see cref="ThemeChanged"/> 让子窗口（设置/Trace）也能叠加 overlay 并随切换即时重染。
/// </summary>
public interface IThemeHost
{
    /// <summary>可选主题列表（内置 light/dark + 用户主题文件）。</summary>
    IReadOnlyList<ThemeOption> Themes { get; }

    /// <summary>当前主题名（高亮用，与选中项的 <see cref="ThemeOption.Name"/> 对应）。</summary>
    string Theme { get; }

    /// <summary>当前主题 overlay JSON（null=无 overlay=基线 light 兜底）。供 <c>ThemeLoader.Apply</c> 叠加到窗口 Resources。</summary>
    string? CustomThemeJson { get; }

    /// <summary>当前主题是否深色倾向（驱动 Avalonia 原生控件 ThemeVariant）。</summary>
    bool IsDark { get; }

    /// <summary>主题变化（overlay JSON 或明暗倾向改变）时触发。子窗口订阅以即时重染。</summary>
    event EventHandler? ThemeChanged;

    /// <summary>选中主题并持久化。name 命中内置/用户文件；用户文件缺失则回退 light。</summary>
    Task SelectThemeAsync(string name, CancellationToken cancellationToken = default);
}
