using CommunityToolkit.Mvvm.ComponentModel;

namespace Acme.Stockapp.Desktop.ViewModels;

/// <summary>
/// CTP 连接标签（chip）项：登录窗顶部的 profile 缩影。数据主体在
/// <see cref="CtpConnectorViewModel"/>._profileConfigs（Id → 完整 <c>CtpConnectionConfig</c>），
/// 本项只承载 chip 显示（Name）与高亮状态（IsCurrent = 当前表单对应的 profile）。
/// </summary>
public sealed partial class CtpProfileItem : ObservableObject
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    /// <summary>chip 高亮：当前表单/连接对应的 profile。</summary>
    [ObservableProperty] private bool _isCurrent;
}
