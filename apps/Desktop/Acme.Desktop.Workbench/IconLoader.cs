using System;
using System.Collections.Generic;
using System.Text.Json;
using Avalonia.Platform;

namespace Acme.Desktop.Workbench;

/// <summary>
/// 图标加载器（Asset Loader 技术）：经 Avalonia <see cref="IAssetLoader"/> 加载内嵌 icons.json manifest
/// （key → Lucide SVG path data）。panel/工作区图标按 key 解析为 Path Data 字符串。
/// 复用现有 NavIcons.cs/NoteIcons.cs（const path data）；本加载器供 manifest 驱动的动态图标。
/// </summary>
public static class IconLoader
{
    private static Dictionary<string, string>? _paths;

    /// <summary>按 key 取 SVG path data（未命中返回 null）。</summary>
    public static string? GetPathData(string key)
    {
        _paths ??= Load();
        return _paths.TryGetValue(key, out var p) ? p : null;
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
