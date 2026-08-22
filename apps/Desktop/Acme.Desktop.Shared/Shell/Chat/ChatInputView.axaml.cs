using Avalonia.Controls;

namespace Acme.Desktop.Shared.Shell;

/// <summary>
/// 聊天输入区（共享控件）：plan-accept 条 + 活动计划面板 + 输入卡（chips / TextBox / 发送-停止）+ 免责声明。
/// 被 <see cref="ChatPageView"/> 两态共用——空态（居中 hero）与非空态（底部 dock）各嵌一个实例，输入定义单一、
/// 仅容器布局不同。pill 圆角 16 是全屏页专属；紧凑右栏 <see cref="ChatView"/> 自持其输入不经此控件。
/// 绑 <c>IChatViewHost</c>（DataContext 继承自父视图同款 VM，与 <see cref="ChatMessagesView"/> 同源）。
/// </summary>
public partial class ChatInputView : UserControl
{
    public ChatInputView()
    {
        InitializeComponent();
    }
}
