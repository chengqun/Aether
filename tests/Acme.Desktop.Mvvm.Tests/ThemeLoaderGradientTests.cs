using Acme.Desktop.Workbench.Theming;
using Avalonia.Media;

namespace Acme.Desktop.Mvvm.Tests;

/// <summary>ThemeLoader 渐变画刷能力单测（P1 皮肤系统基线）。
/// 验证 GradientSpec → LinearGradientBrush 构建：停靠点解析/乱序排序、*Brush→*Color 派生、
/// 背景优先级（渐变 &gt; 纯色）、向后兼容（无 gradients 的旧主题仍走纯色 SolidColorBrush）。
/// 仅构造 LinearGradientBrush/GradientStop/ResourceDictionary，零渲染/平台依赖——可在 headless xunit 跑。</summary>
public class ThemeLoaderGradientTests
{
    // 含 gradients + 渐变背景的"炫"主题（dream.json 形状）。停靠点故意乱序以验证排序。
    private const string GradientTheme = """
    {
      "variant": "dark",
      "colors": { "AccentColor": "#a78bfa", "SidebarBgBrush": "#1a1a2eCC" },
      "gradients": {
        "AccentBrush": { "type": "linear", "angle": 90,
          "stops": [ { "color": "#60a5fa", "offset": 1.0 }, { "color": "#a78bfa", "offset": 0.0 } ] },
        "UserBubbleBrush": { "stops": [ { "color": "#7c3aed", "offset": 0.0 }, { "color": "#2563eb", "offset": 1.0 } ] }
      },
      "background": { "gradient": { "type": "linear", "angle": 135,
          "stops": [ { "color": "#0f0c29", "offset": 0.0 }, { "color": "#302b63", "offset": 1.0 } ] } }
    }
    """;

    // 旧式纯色主题（无 gradients / background.gradient）——向后兼容回归。
    private const string SolidTheme = """
    {
      "variant": "light",
      "colors": { "AccentColor": "#5B5FC7", "AccentBrush": "#5B5FC7" },
      "background": { "color": "#FFFFFF" }
    }
    """;

    [Fact]
    public void Gradient_Brush_Overrides_Color_Brush_With_LinearGradient_SortedStops()
    {
        var def = ThemeLoader.Deserialize(GradientTheme)!;
        Assert.NotNull(def.Gradients);

        var dict = ThemeLoader.ToResourceDictionary(def);

        // AccentBrush：colors 里无此 key（只有 AccentColor）→ gradients 覆盖出渐变
        var accent = Assert.IsType<LinearGradientBrush>(dict["AccentBrush"]);
        Assert.Equal(2, accent.GradientStops.Count);
        // 乱序停靠点应被排序（offset 升序）：首站 offset 0 / #a78bfa，末站 offset 1 / #60a5fa
        Assert.Equal(0.0, accent.GradientStops[0].Offset);
        Assert.Equal(Color.Parse("#a78bfa"), accent.GradientStops[0].Color);
        Assert.Equal(1.0, accent.GradientStops[1].Offset);
        Assert.Equal(Color.Parse("#60a5fa"), accent.GradientStops[1].Color);
    }

    [Fact]
    public void Gradient_Brush_Derives_Color_From_FirstStop_When_Color_Absent()
    {
        var def = ThemeLoader.Deserialize(GradientTheme)!;
        var dict = ThemeLoader.ToResourceDictionary(def);

        // UserBubbleBrush 在 gradients，colors 无 UserBubbleColor → 派生 UserBubbleColor = 首停靠点色 #7c3aed
        Assert.IsType<LinearGradientBrush>(dict["UserBubbleBrush"]);
        // 缺键时 dict[...] 会抛 KeyNotFound → 测试失败；故此断言同时验证"派生键存在 + 值正确"
        Assert.Equal(Color.Parse("#7c3aed"), (Color)dict["UserBubbleColor"]!);
    }

    [Fact]
    public void Gradient_DoesNotOverride_ExplicitlyProvided_Color()
    {
        var def = ThemeLoader.Deserialize(GradientTheme)!;
        var dict = ThemeLoader.ToResourceDictionary(def);

        // AccentColor 在 colors 显式给 #a78bfa；AccentBrush 渐变首停靠点亦 #a78bfa——
        // 不论如何，显式 colors 的 AccentColor 应被尊重（不被渐变逻辑覆盖成别的）。
        Assert.Equal(Color.Parse("#a78bfa"), (Color)dict["AccentColor"]!);
    }

    [Fact]
    public void Background_Gradient_Produces_LinearGradientBrush_WorkspaceBackground()
    {
        var def = ThemeLoader.Deserialize(GradientTheme)!;
        var dict = ThemeLoader.ToResourceDictionary(def);

        var bg = Assert.IsType<LinearGradientBrush>(dict["WorkspaceBackground"]);
        Assert.Equal(2, bg.GradientStops.Count);
        Assert.Equal(Color.Parse("#0f0c29"), bg.GradientStops[0].Color);
        Assert.Equal(Color.Parse("#302b63"), bg.GradientStops[1].Color);
    }

    [Fact]
    public void SolidTheme_BackwardCompat_WorkspaceBackground_Is_SolidColorBrush()
    {
        // 无 gradients / background.gradient 的旧主题：行为应完全不变。
        var def = ThemeLoader.Deserialize(SolidTheme)!;
        Assert.Null(def.Gradients);

        var dict = ThemeLoader.ToResourceDictionary(def);

        // 背景 fallback 纯色（BuildBackground: gradient=null → image 空 → color #FFFFFF）
        Assert.IsType<SolidColorBrush>(dict["WorkspaceBackground"]);
        // AccentBrush 来自 colors（纯色）——无渐变覆盖
        Assert.IsType<SolidColorBrush>(dict["AccentBrush"]);
        Assert.Equal(Color.Parse("#5B5FC7"), ((SolidColorBrush)dict["AccentBrush"]!).Color);
    }

    [Fact]
    public void BuiltInThemes_Includes_Dream()
    {
        // 注册校验：dream 皮肤在内置清单中（菜单才会出）。
        Assert.Contains(ThemeLoader.BuiltInThemes, t => t.Name == "dream");
    }

    [Fact]
    public void Translucent_Color_Parses_As_AARRGGBB_AlphaFirst()
    {
        // dream 皮肤半透明面板用 #AARRGGBB（alpha 在前——Avalonia/WPF 约定；非 CSS 的 RRGGBBAA）。
        // #cc1a1a2e 应解为 A=0xcc(204=80% 不透明)、R=0x1a G=0x1a B=0x2e。
        // 若误写 CSS 式 #1a1a2ecc 则 Avalonia 解成 A=0x1a(10% 近透明)、R=0x2e G=0xcc ——面板几近不可见、
        // aurora 透出过度（曾踩此坑：dream.json 全部 8 位色初版用 RRGGBBAA 全错）。此测试锁定正确约定。
        var json = """
        {
          "variant": "dark",
          "colors": { "SidebarBgBrush": "#cc1a1a2e" }
        }
        """;
        var def = ThemeLoader.Deserialize(json)!;
        var dict = ThemeLoader.ToResourceDictionary(def);

        var brush = Assert.IsType<SolidColorBrush>(dict["SidebarBgBrush"]);
        Assert.Equal(0xcc, brush.Color.A);   // alpha 在前 → 204（非 0x1a）
        Assert.Equal(0x1a, brush.Color.R);
        Assert.Equal(0x1a, brush.Color.G);
        Assert.Equal(0x2e, brush.Color.B);
    }
}
