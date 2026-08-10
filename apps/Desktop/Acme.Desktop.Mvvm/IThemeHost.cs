namespace Acme.Desktop.Mvvm;

/// <summary>
/// 主题宿主接口：让设置层（<c>Acme.Desktop.Shared</c> 的 SettingsVM，不引用 Workbench）
/// 跨层访问 <see cref="WorkbenchShellViewModel"/> 的主题状态而不引入项目引用。
/// 实现在 Workbench 层；SettingsVM 经 <c>GetService&lt;MainViewModelBase&gt;() as IThemeHost</c> 取。
/// </summary>
public interface IThemeHost
{
    /// <summary>可选主题列表（内置 light/dark + 用户主题文件）。</summary>
    IReadOnlyList<ThemeOption> Themes { get; }

    /// <summary>当前主题名（高亮用，与选中项的 <see cref="ThemeOption.Name"/> 对应）。</summary>
    string Theme { get; }

    /// <summary>选中主题并持久化。name 命中内置/用户文件；用户文件缺失则回退 light。</summary>
    Task SelectThemeAsync(string name, CancellationToken cancellationToken = default);
}
