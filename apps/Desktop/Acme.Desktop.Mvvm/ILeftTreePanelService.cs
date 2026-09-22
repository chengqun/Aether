using System;

namespace Acme.Desktop.Mvvm;

/// <summary>
/// 主侧栏（shell 级左栏：文件树 / 项目 SCM 面板 / 知识对象树 / 知识库 SCM 面板）显隐程序化 API
/// （接口在 Mvvm，UI 无关，镜像 <see cref="IChatPanelService"/> 的显隐三件套；impl 在 Shared）。
/// 标题栏「主侧栏」toggle 按钮经此服务开关左栏（VSCode 式可折叠侧栏，与终端/AI 右栏 toggle 一致）。
/// </summary>
/// <remarks>
/// 左栏由 tab **内容驱动**（切到 documents/sourcecontrol/knowledge/knowledgescm tab 显示对应树，其余 tab 无树可显、归 0——
/// 这是内容切换而非让空间，区别于已删除的终端/右栏 tab-coupled 面板隐藏）。
/// 本服务叠加一层**手动 toggle**：有效可见 = 手动 toggle 开 && 当前是编辑器类 tab。折叠时仅隐藏 region 空间
/// （列宽 0 + content IsVisible=false），树视图不销毁——Workbench 加载即建全部 panel 视图，跨 nav 切换常驻（仅隐藏），
/// 文件树展开状态不丢。
/// </remarks>
public interface ILeftTreePanelService
{
    /// <summary>主侧栏（左栏）手动 toggle 是否开启。与 tab 耦合：开启且当前是编辑器类 tab 时左栏才真正显示。</summary>
    bool IsLeftTreeVisible { get; }

    /// <summary>主侧栏显隐变更。WorkspaceView 订阅以折叠/展开左栏 region 的 Grid 列宽；shell VM 订阅以同步 toggle 按钮态。</summary>
    event Action<bool>? LeftTreeVisibilityChanged;

    /// <summary>设置主侧栏手动 toggle 可见性并持久化到 preferences（ui.leftTreeVisible）。标题栏 toggle 调此。</summary>
    void SetLeftTreeVisible(bool visible);
}
