using System.Collections.Generic;
using Avalonia.Controls;

namespace Acme.Desktop.Workbench.Abstractions;

/// <summary>
/// 插件面板注入：命名 region 的运行时注册表。
/// <see cref="WorkspaceView"/> 遍历 <see cref="LayoutRegion"/> 叶子时向此登记命名 region（name → ContentControl 宿主）。
/// 插件不重定义整个 workspace，只往已存在的命名 region 里塞/删 panel。
/// </summary>
public interface IRegionManager
{
    /// <summary>登记一个命名 region 的视觉宿主（ContentControl）。WorkspaceView 建叶子时调。</summary>
    void RegisterRegion(string name, ContentControl host);

    /// <summary>取消登记。</summary>
    void UnregisterRegion(string name);

    /// <summary>往命名 region 塞一个 panel（解析其视图并设为宿主 Content）。返回是否成功。</summary>
    bool AddToRegion(string name, IPanel panel);

    /// <summary>从命名 region 移除指定 panel（若为当前内容则清空）。</summary>
    void RemoveFromRegion(string name, string panelId);

    /// <summary>当前所有已登记的 region 名。</summary>
    IReadOnlyList<string> GetRegionNames();
}
