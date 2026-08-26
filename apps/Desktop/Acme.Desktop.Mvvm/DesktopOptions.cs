namespace Acme.Desktop.Mvvm;

/// <summary>
/// 桌面运行时路径配置（由桌面组合根 <c>AddDesktopCommon</c> 设置）。非 AI 选项，故不放 AetherAIOptions。
/// options 类在此，<c>services.Configure</c> 注册，消费方 <c>IOptions</c> 惰性取。
/// </summary>
public sealed class DesktopOptions
{
    /// <summary>用户主题 JSON 文件目录（&lt;tenantDir&gt;/themes）。内置 light/dark 不依赖此目录。</summary>
    public string? ThemesDirectory { get; set; }
}
