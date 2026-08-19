using Acme.Desktop.Mvvm.Palette;

namespace Acme.Desktop.Mvvm.Tests;

/// <summary>调色板主色派生算法单测（PaletteViewModel.MixHex / DeriveAccentFamily 纯函数，零 Avalonia 依赖）。
/// 验证 RGB 线性混合边界/中点/非法回退 + 主色派生族齐全且语义正确（变暗/变亮/极淡）。</summary>
public class PaletteDerivationTests
{
    private const string Accent = "#5B5FC7";   // 内置 light 的 accent

    [Fact]
    public void DeriveAccentFamily_ReturnsAllSevenTokens_AndMainFamilyEqualsAccent()
    {
        var d = PaletteViewModel.DeriveAccentFamily(Accent);

        Assert.Equal(7, d.Count);
        // accent 同值族（4 个直接等于主色）
        Assert.Equal(Accent, d["AccentColor"]);
        Assert.Equal(Accent, d["AccentBrush"]);
        Assert.Equal(Accent, d["SidebarActiveBrush"]);
        Assert.Equal(Accent, d["UserBubbleBrush"]);
    }

    [Fact]
    public void DeriveAccentFamily_HoverIsDarker_LightIsLighter_TintNearWhite()
    {
        var d = PaletteViewModel.DeriveAccentFamily(Accent);
        var (r, g, b) = ParseRgb(Accent);

        // hover = 混黑 15% → 各通道 ≤ 主色
        var (hr, hg, hb) = ParseRgb(d["AccentHoverBrush"]);
        Assert.True(hr <= r && hg <= g && hb <= b);

        // light = 混白 25% → 各通道 ≥ 主色
        var (lr, lg, lb) = ParseRgb(d["AccentLightBrush"]);
        Assert.True(lr >= r && lg >= g && lb >= b);

        // tint = 混白 88% → 接近白（各通道 ≥ light）
        var (tr, tg, tb) = ParseRgb(d["AccentTintBgBrush"]);
        Assert.True(tr >= lr && tg >= lg && tb >= lb);
    }

    [Fact]
    public void MixHex_AtT0ReturnsA_AtT1ReturnsB()
    {
        Assert.Equal("#000000", PaletteViewModel.MixHex("#000000", "#FFFFFF", 0));
        Assert.Equal("#FFFFFF", PaletteViewModel.MixHex("#000000", "#FFFFFF", 1));
    }

    [Fact]
    public void MixHex_MidpointIsAverage()
    {
        // 0→255 中点 = 127.5 → Math.Round(ToEven) = 128 = 0x80
        Assert.Equal("#808080", PaletteViewModel.MixHex("#000000", "#FFFFFF", 0.5));
    }

    [Fact]
    public void MixHex_DerivedHoverMatchesDirectMix()
    {
        var d = PaletteViewModel.DeriveAccentFamily(Accent);
        Assert.Equal(PaletteViewModel.MixHex(Accent, "#000000", 0.15), d["AccentHoverBrush"]);
        Assert.Equal(PaletteViewModel.MixHex(Accent, "#FFFFFF", 0.25), d["AccentLightBrush"]);
        Assert.Equal(PaletteViewModel.MixHex(Accent, "#FFFFFF", 0.88), d["AccentTintBgBrush"]);
    }

    [Fact]
    public void MixHex_InvalidHexFallsBackToA()
    {
        // b 非法 → 回退 a
        Assert.Equal("#5B5FC7", PaletteViewModel.MixHex("#5B5FC7", "not-a-color", 0.5));
        // a 非法 → 回退 a（原样返回，a 本就是非法串）
        Assert.Equal("bad", PaletteViewModel.MixHex("bad", "#000000", 0.5));
    }

    [Fact]
    public void MixHex_DropsAlphaChannelFromEightDigitHex()
    {
        // #5B5FC7FF（带 alpha）→ 解析时丢 AA，等同 #5B5FC7
        Assert.Equal(Accent, PaletteViewModel.MixHex("#5B5FC7FF", "#000000", 0));
    }

    private static (int r, int g, int b) ParseRgb(string hex)
    {
        var h = hex.TrimStart('#');
        if (h.Length == 8) h = h[2..];
        return (Convert.ToInt32(h[0..2], 16), Convert.ToInt32(h[2..4], 16), Convert.ToInt32(h[4..6], 16));
    }
}
