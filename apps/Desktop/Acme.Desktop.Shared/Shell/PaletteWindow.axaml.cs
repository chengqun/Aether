using System;
using Acme.Desktop.Mvvm.Palette;
using Avalonia.Controls;
using Avalonia.Media;

namespace Acme.Desktop.Shared.Shell;

/// <summary>调色板窗口（ThemedWindow + ColorPicker）。绑 PaletteViewModel。
/// ColorView.Color↔PaletteColorRow.Hex 经 HexToColorConverter 双向绑定；另在 code-behind 订
/// ColorView.ColorChanged 事件把新色确定性推回 VM 的 Hex——不依赖 TwoWay 绑定 pushback
/// （Color 是 read-write StyledProperty，TwoWay 多半也行，但事件是铁律兜底）。
/// 改色经 VM.RebuildPreview→IThemeHost.SetCustomThemeJson 整窗实时预览。</summary>
public partial class PaletteWindow : ThemedWindow
{
    public PaletteWindow() { InitializeComponent(); }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);   // 先让 ThemedWindow 套 overlay 主题
        MasterColorView.ColorChanged += OnMasterColorChanged;
        BgColorView.ColorChanged += OnBgColorChanged;
    }

    protected override void OnClosed(EventArgs e)
    {
        MasterColorView.ColorChanged -= OnMasterColorChanged;
        BgColorView.ColorChanged -= OnBgColorChanged;
        base.OnClosed(e);
    }

    /// <summary>颜色 tab 主色板：拖动/输入新色 → 推回当前选中色项 Hex（派生项 IsEditable=false 跳过）。</summary>
    private void OnMasterColorChanged(object? sender, ColorChangedEventArgs e)
    {
        if (DataContext is PaletteViewModel vm && vm.SelectedColorRow is { IsEditable: true } row)
            row.Hex = ToHex(e.NewColor);
    }

    /// <summary>明暗基底 tab 背景色板：新色 → 推回 BackgroundColor.Hex。</summary>
    private void OnBgColorChanged(object? sender, ColorChangedEventArgs e)
    {
        if (DataContext is PaletteViewModel vm)
            vm.BackgroundColor.Hex = ToHex(e.NewColor);
    }

    /// <summary>Color → #RRGGBB（丢 alpha，与 HexToColorConverter.ConvertBack 对称）。</summary>
    private static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
}
