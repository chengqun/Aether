using Acme.Mobile.Mvvm.Models;

namespace Acme.Mobile.Shared.Views;

/// <summary>
/// 聊天消息模板选择器：按 <see cref="ChatMessageItem.Role"/> / <see cref="ChatMessageItem.ItemType"/>
/// / <see cref="ChatMessageItem.IsReasoning"/> 选择气泡 DataTemplate（用户文本 / 助手文本 Markdown /
/// 工具单卡（call+result 合并）/ 思考段 / 审批卡）。对齐桌面分段模型 ItemType 集 {text,tool,plan,approval}。
/// DataTemplate 实例由页面 ResourceDictionary 注入到本选择器属性。
/// </summary>
public class ChatMessageTemplateSelector : DataTemplateSelector
{
    public DataTemplate? UserTextTemplate { get; set; }
    public DataTemplate? AssistantTextTemplate { get; set; }
    public DataTemplate? ToolTemplate { get; set; }
    public DataTemplate? ReasoningTemplate { get; set; }
    public DataTemplate? ApprovalTemplate { get; set; }

    protected override DataTemplate OnSelectTemplate(object item, BindableObject container)
    {
        if (item is not ChatMessageItem m)
            return AssistantTextTemplate!;

        // 用户气泡优先（Role 驱动）
        if (m.IsUser) return UserTextTemplate ?? AssistantTextTemplate!;

        // 助手侧：按 ItemType + IsReasoning 路由
        if (m.IsToolStep) return ToolTemplate ?? AssistantTextTemplate!;
        if (m.IsApprovalCard) return ApprovalTemplate ?? AssistantTextTemplate!;
        if (m.IsReasoning) return ReasoningTemplate ?? AssistantTextTemplate!;
        return AssistantTextTemplate!;
    }
}
