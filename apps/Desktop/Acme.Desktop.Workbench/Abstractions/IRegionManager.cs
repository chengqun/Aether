using System.Collections.Generic;
using Avalonia.Controls;

namespace Acme.Desktop.Workbench.Abstractions;

/// <summary>
/// 插件面板注入：命名 region 的运行时注册表。
/// <see cref="WorkspaceView"/> 遍历 <see cref="LayoutRegion"/> 叶子时向此登记命名 region（name → ContentControl 宿主）。
/// 插件不重定义整个 workspace，只往已存在的命名 region 里塞/删 panel。
/// </summary>
/// <remarks>
/// 内容变更契约（防止 chat 空白 bug 复发）：<see cref="RegisterRegion"/> 仅登记宿主、**绝不触碰 host.Content**；
/// region 的内容（初始 panel 或插件注入）一律经 <see cref="AddToRegion"/> 设置，经 <see cref="RemoveFromRegion"/> 清空。
/// 故 <c>BuildRegion</c> 先 <c>RegisterRegion</c> 再 <c>AddToRegion</c>——内容设置集中在 AddToRegion，RegisterRegion 无副作用，
/// 调换顺序或误加 <c>host.Content=null</c> 都不会抹掉已设内容。
/// </remarks>
public interface IRegionManager
{
    /// <summary>登记一个命名 region 的视觉宿主（ContentControl）。仅记录宿主，**不触碰 host.Content**。
    /// WorkspaceView 建叶子时调；随后用 <see cref="AddToRegion"/> 设初始内容。</summary>
    void RegisterRegion(string name, ContentControl host);

    /// <summary>取消登记并清空宿主内容（region 拆除时调）。当前无消费者（MVP 不拆 region），留作插件生命周期。</summary>
    void UnregisterRegion(string name);

    /// <summary>往命名 region 塞一个 panel（按 panel.Key 解析视图并设为宿主 Content）。
    /// 这是设置 region 内容的唯一路径——初始 panel 与插件注入都走此。返回是否成功（region 未登记或视图解析失败则 false）。</summary>
    bool AddToRegion(string name, IPanel panel);

    /// <summary>清空命名 region 的内容（MVP 单 panel region：last-wins，清空即移除当前 panel）。</summary>
    void RemoveFromRegion(string name);

    /// <summary>当前所有已登记的 region 名。</summary>
    IReadOnlyList<string> GetRegionNames();
}
