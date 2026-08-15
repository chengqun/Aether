using System;
using System.Collections.Generic;
using System.Text.Json;
using Acme.Desktop.Shared.Shell;
using Avalonia.Platform;
using IconPacks.Avalonia.Codicons;

namespace Acme.Desktop.Workbench;

/// <summary>
/// 图标加载器：侧边栏 nav 图标优先解析为 **VSCode codicon**（填充型几何 glyph，对齐 VSCode 活动栏外观），
/// 经 <see cref="Codicons.Get"/>（Shared 层集中访问点）取 codicon path data 喂 <see cref="WorkbenchWindow"/> 的
/// <c>&lt;Path&gt;</c>。未命中 codicon map 的 key 回退到内嵌 icons.json manifest（key → Lucide SVG path data，描边型）。
/// codicon 是填充 glyph → nav-rail <c>&lt;Path&gt;</c> 用 Fill 渲染（非 Lucide 的 Stroke）；见 WorkbenchWindow.axaml。
/// </summary>
public static class IconLoader
{
    private static Dictionary<string, string>? _paths;

    /// <summary>nav panel key → VSCode codicon kind（填充型 glyph）。侧边栏图标的权威来源。</summary>
    private static readonly Dictionary<string, PackIconCodiconsKind> CodiconMap = new(StringComparer.Ordinal)
    {
        ["chat"] = PackIconCodiconsKind.CommentDiscussion,
        ["experts"] = PackIconCodiconsKind.Hubot,
        ["notes"] = PackIconCodiconsKind.Book,
        ["documents"] = PackIconCodiconsKind.Files,
        ["sourcecontrol"] = PackIconCodiconsKind.SourceControl,
        ["automation"] = PackIconCodiconsKind.Calendar,
        ["futures"] = PackIconCodiconsKind.GraphLine,
        ["backtest"] = PackIconCodiconsKind.Beaker,
        ["activity"] = PackIconCodiconsKind.Pulse,
        ["settings"] = PackIconCodiconsKind.SettingsGear,
        ["workflow"] = PackIconCodiconsKind.Rocket,
    };

    /// <summary>菜单/收起按钮的 codicon 三横线 path data（委托 Shared.Codicons，XAML <c>{x:Static}</c> 引用）。</summary>
    public static readonly string CodiconMenu = Codicons.Menu;

    /// <summary>按 key 取图标 path data：codicon map 优先（填充 glyph），未命中回退 icons.json（Lucide 描边）。</summary>
    public static string? GetPathData(string key)
    {
        if (CodiconMap.TryGetValue(key, out var kind))
        {
            var p = Codicons.Get(kind);
            if (!string.IsNullOrEmpty(p)) return p;
        }
        _paths ??= Load();
        return _paths.TryGetValue(key, out var lp) ? lp : null;
    }

    private static Dictionary<string, string> Load()
    {
        try
        {
            var uri = new Uri("avares://Acme.Desktop.Workbench/Assets/Icons/icons.json");
            if (!AssetLoader.Exists(uri)) return new();
            using var s = AssetLoader.Open(uri);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(s) ?? new();
        }
        catch
        {
            return new();
        }
    }
}
