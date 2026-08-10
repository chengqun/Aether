namespace Acme.Desktop.Mvvm;

/// <summary>
/// 聊天视图宿主契约：ChatView code-behind 经此访问运行态/历史增量/设置入口，
/// 解耦对 <see cref="MainViewModelBase"/> 的硬 cast。继承 <see cref="IChatViewModel"/>（Messages），
/// 供 CollectionChanged 订阅。<see cref="ChatWorkspaceViewModel"/> 实现此接口；
/// Codex 工作台 shell 把 Chat 作为 panel 暴露时，ChatView 直接绑到 Chat。
/// </summary>
public interface IChatViewHost : IChatViewModel
{
    /// <summary>agent 是否正在流式运行（驱动停止按钮/plan-accept 条显隐）。</summary>
    bool IsAgentRunning { get; }

    /// <summary>是否还有更早的历史轮未渲染（供 View 判断是否触发增量加载）。</summary>
    bool HasMoreHistory { get; }

    /// <summary>增量加载更早的历史轮，返回实际加载的轮数（0=无更多）。</summary>
    int LoadMoreHistory();

    /// <summary>设置对话框服务（ChatView 头部「设备连接配置」按钮调用）。</summary>
    ISettingsDialogService? SettingsDialog { get; }
}
