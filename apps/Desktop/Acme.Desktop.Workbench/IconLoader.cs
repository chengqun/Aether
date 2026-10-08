using System;
using System.Collections.Generic;
using Acme.Desktop.Shared.Shell;
using IconPacks.Avalonia.Codicons;

namespace Acme.Desktop.Workbench;

/// <summary>
/// 图标加载器：侧边栏 nav 图标解析为 **VSCode codicon**（填充型几何 glyph，对齐 VSCode 活动栏外观），
/// 经 <see cref="Codicons.Get"/>（Shared 层集中访问点）取 codicon path data 喂 <see cref="WorkbenchWindow"/> 的
/// <c>&lt;Path&gt;</c>。codicon 是填充 glyph → nav-rail <c>&lt;Path&gt;</c> 用 Fill 渲染（非 Lucide 的 Stroke）。
/// <para>图标体系单一权威源：codicon（<see cref="Codicons"/>，Shared）/ Lucide（<see cref="NavIcons"/>，Mvvm）。
/// 旧的内嵌 icons.json（Lucide 回退清单）已删——它的 key 全被 <see cref="CodiconMap"/> 覆盖、Lucide path
/// 与 NavIcons 重复，是死资源。未命中 codicon 返回 null，调用方回退 panel.Icon 原串。</para>
/// </summary>
public static class IconLoader
{
    /// <summary>nav panel key → VSCode codicon kind（填充型 glyph）。侧边栏图标的权威来源。</summary>
    private static readonly Dictionary<string, PackIconCodiconsKind> CodiconMap = new(StringComparer.Ordinal)
    {
        ["chat"] = PackIconCodiconsKind.CommentDiscussion,
        ["connector"] = PackIconCodiconsKind.Plug,
        ["notes"] = PackIconCodiconsKind.Book,
        ["documents"] = PackIconCodiconsKind.Files,
        ["sourcecontrol"] = PackIconCodiconsKind.SourceControl,
        ["terminal"] = PackIconCodiconsKind.Terminal,
        ["automation"] = PackIconCodiconsKind.Calendar,
        ["futures"] = PackIconCodiconsKind.GraphLine,
        ["backtest"] = PackIconCodiconsKind.Beaker,
        ["activity"] = PackIconCodiconsKind.Pulse,
        ["history"] = PackIconCodiconsKind.History,
        ["wallet"] = PackIconCodiconsKind.CreditCard,   // codicons 包无 Wallet——CreditCard 表「钱」
        ["knowledge"] = PackIconCodiconsKind.Collection,
        ["settings"] = PackIconCodiconsKind.SettingsGear,
        ["workflow"] = PackIconCodiconsKind.Rocket,
    };

    /// <summary>菜单/收起按钮的 codicon 三横线 path data（委托 Shared.Codicons，XAML <c>{x:Static}</c> 引用）。</summary>
    public static readonly string CodiconMenu = Codicons.Menu;

    /// <summary>按 key 取 codicon path data；未命中返回 null（调用方回退 panel.Icon 原串）。</summary>
    public static string? GetPathData(string key)
    {
        if (CodiconMap.TryGetValue(key, out var kind))
        {
            var p = Codicons.Get(kind);
            if (!string.IsNullOrEmpty(p)) return p;
        }
        return null;
    }
}
