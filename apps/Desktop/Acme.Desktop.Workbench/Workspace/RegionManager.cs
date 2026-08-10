using System;
using System.Collections.Generic;
using System.Linq;
using Acme.Desktop.Workbench.Abstractions;
using Avalonia.Controls;

namespace Acme.Desktop.Workbench.Workspace;

/// <summary>
/// IRegionManager 默认实现：命名 region → ContentControl 宿主字典。
/// MVP 单 panel/region：AddToRegion 设宿主 Content（last wins）；RemoveFromRegion 清空。
/// 多 panel/region（ItemsControl 宿主）留后续。
/// 内容变更契约见 <see cref="IRegionManager"/>：RegisterRegion 不碰 Content，AddToRegion/RemoveFromRegion 才改。
/// </summary>
public sealed class RegionManager : IRegionManager
{
    private readonly IViewRegistry _viewRegistry;
    private readonly Dictionary<string, ContentControl> _regions = new();

    public RegionManager(IViewRegistry viewRegistry)
    {
        _viewRegistry = viewRegistry;
    }

    public void RegisterRegion(string name, ContentControl host)
    {
        // 仅登记宿主——绝不触碰 host.Content（曾因清空抹掉 BuildRegion 已设的 ChatView 致 chat 空白）。
        // 内容由 AddToRegion 设置。重复登记同一 name 覆盖宿主引用（last wins）。
        _regions[name] = host;
    }

    public void UnregisterRegion(string name)
    {
        if (_regions.TryGetValue(name, out var host))
        {
            host.Content = null; // 拆除 region 时清空（与 RemoveFromRegion 的"清内容但保留 region"区分）
            _regions.Remove(name);
        }
    }

    public bool AddToRegion(string name, IPanel panel)
    {
        if (!_regions.TryGetValue(name, out var host)) return false;
        var view = _viewRegistry.CreateView(panel.Key);
        if (view is null) return false; // key 未注册 → 不改 Content
        view.DataContext = panel.ViewModel;
        host.Content = view;
        return true;
    }

    public void RemoveFromRegion(string name)
    {
        if (_regions.TryGetValue(name, out var host))
            host.Content = null;
    }

    public IReadOnlyList<string> GetRegionNames() => _regions.Keys.ToList();
}
