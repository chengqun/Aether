using Acme.Desktop.Mvvm;

namespace Acme.Desktop.Shared.Shell;

/// <summary>聊天模式选择器条目（ComboBox ItemsSource）：枚举值 + 显示标签 + codicon 图标路径。
/// 图标路径来自 <see cref="Codicons"/>（Shared 层，Mvvm 不可见），故本类在 Shared 而非 Mvvm。
/// ComboBox 用 SelectedValuePath="Mode" + SelectedValue 绑 VM 的 <c>SelectedChatMode</c>（枚举）。</summary>
public sealed class ChatModeInfo
{
    public ChatMode Mode { get; init; }
    public string Label { get; init; } = "";
    public string Icon { get; init; } = "";
    public string ToolTip { get; init; } = "";
}

/// <summary>4 档模式静态列表（选择器 ItemsSource）。</summary>
public static class ChatModes
{
    public static IReadOnlyList<ChatModeInfo> All { get; } = new[]
    {
        new ChatModeInfo { Mode = ChatMode.Plan,     Label = "规划", Icon = Codicons.ListFlat, ToolTip = "规划：先出计划不执行，接受后再执行" },
        new ChatModeInfo { Mode = ChatMode.Manual,   Label = "手动", Icon = Codicons.Edit,     ToolTip = "手动：每个非安全工具都弹审批" },
        new ChatModeInfo { Mode = ChatMode.AutoEdit, Label = "自动编辑", Icon = Codicons.Beaker,  ToolTip = "自动编辑：自动批可逆操作，危险操作仍弹审批（默认）" },
        new ChatModeInfo { Mode = ChatMode.Auto,     Label = "自动", Icon = Codicons.Play,     ToolTip = "自动：全部自动批，不弹审批" },
    };
}
