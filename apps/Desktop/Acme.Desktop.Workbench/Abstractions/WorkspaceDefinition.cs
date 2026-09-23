using System.Collections.Generic;

namespace Acme.Desktop.Workbench.Abstractions;

/// <summary>panel 声明：region id + 视图/VM 注册键 + 标题 + 图标键。</summary>
/// <param name="RegionId">在 <see cref="LayoutNode"/> 中引用的 region 标识（LayoutRegion/LayoutTabbed 用它找 panel）。</param>
/// <param name="Key"><see cref="IViewRegistry"/> 注册键（如 "chat"/"notes"/"experts"）。</param>
/// <param name="Title">tab/panel 标题（nav rail 常显标签——保持 2-4 字紧凑）。</param>
/// <param name="Icon">图标键（Asset Loader manifest 键或内置 path 键）。</param>
/// <param name="Group">可选分组名（仅 nav rail 呈现用：连续项 Group 变化时上方加间距分隔；null = 不分组）。
/// 不进 <see cref="LayoutNode"/>、不影响布局持久化（CoversSameRegions 只比 region id 序列）。</param>
public sealed record PanelDescriptor(string RegionId, string Key, string Title, string Icon, string? Group = null);

/// <summary>工作台定义：纯数据（可 JSON 序列化）。后续插件/AI 生成只需产出此对象 → <see cref="IWorkspaceManager.ActivateAsync"/>。</summary>
/// <param name="Id">工作台标识。</param>
/// <param name="Title">工作台标题。</param>
/// <param name="Panels">所有 panel 声明。</param>
/// <param name="Layout">布局树根。</param>
public sealed record WorkspaceDefinition(
    string Id,
    string Title,
    IReadOnlyList<PanelDescriptor> Panels,
    LayoutNode Layout);
