using Acme.Desktop.Mvvm;

namespace Acme.Desktop.Mvvm.Palette;

/// <summary>调色板弹窗服务（接口在 Mvvm UI-agnostic；impl 在 Shared/Shell 用 ThemedWindow+ColorView）。
/// 照 <see cref="IReportDialogService"/> 范式：展示调色板对话框，用户改色实时预览、保存/取消。</summary>
public interface IPaletteDialogService
{
    /// <summary>展示调色板对话框，从 <paramref name="host"/> 的当前主题派生可调色。
    /// 实时预览/保存/取消由内部 PaletteViewModel 经 host 驱动，本方法无返回值。</summary>
    Task ShowAsync(IThemeHost host, CancellationToken cancellationToken = default);
}
