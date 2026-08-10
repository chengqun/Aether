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
        // 不清空 host.Content——BuildRegion 先登记 region 再设 panel 视图，若此处清空会抹掉已设的视图
        // （chat 面板被清空即此 bug）。region 起始内容由调用方决定；AddToRegion/RemoveFromRegion 才改 Content。
        _regions[name] = host;
    }

    public void UnregisterRegion(string name)
    {
        if (_regions.TryGetValue(name, out var host))
        {
            host.Content = null;
            _regions.Remove(name);
        }
    }

    public bool AddToRegion(string name, IPanel panel)
    {
        if (!_regions.TryGetValue(name, out var host)) return false;
        var view = _viewRegistry.CreateView(panel.Key);
        if (view is not null) view.DataContext = panel.ViewModel;
        host.Content = view;
        return true;
    }

    public void RemoveFromRegion(string name, string panelId)
    {
        if (_regions.TryGetValue(name, out var host))
            host.Content = null;
    }

    public IReadOnlyList<string> GetRegionNames() => _regions.Keys.ToList();
}
