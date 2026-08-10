using System.Collections.Generic;

namespace Acme.Desktop.Workbench.Abstractions;

/// <summary>panel 声明：region id + 视图/VM 注册键 + 标题 + 图标键。</summary>
/// <param name="RegionId">在 <see cref="LayoutNode"/> 中引用的 region 标识（LayoutRegion/LayoutTabbed 用它找 panel）。</param>
/// <param name="Key"><see cref="IViewRegistry"/> 注册键（如 "chat"/"notes"/"experts"）。</param>
/// <param name="Title">tab/panel 标题。</param>
/// <param name="Icon">图标键（Asset Loader manifest 键或内置 path 键）。</param>
public sealed record PanelDescriptor(string RegionId, string Key, string Title, string Icon);

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
