using Acme.Mobile.Mvvm.Models;

namespace Acme.Mobile.Shared.Views;

/// <summary>
/// 聊天消息模板选择器：按 <see cref="ChatMessageItem.ItemType"/> / <see cref="ChatMessageItem.Role"/>
/// 选择气泡 DataTemplate（用户文本 / 助手文本 Markdown / 工具调用 / 工具结果 / 思考）。
/// DataTemplate 实例由页面 ResourceDictionary 注入到本选择器属性。
/// </summary>
public class ChatMessageTemplateSelector : DataTemplateSelector
{
    public DataTemplate? UserTextTemplate { get; set; }
    public DataTemplate? AssistantTextTemplate { get; set; }
    public DataTemplate? ToolCallTemplate { get; set; }
    public DataTemplate? ToolResultTemplate { get; set; }
    public DataTemplate? ThinkingTemplate { get; set; }

    protected override DataTemplate OnSelectTemplate(object item, BindableObject container)
    {
        if (item is not ChatMessageItem m)
            return AssistantTextTemplate!;

        return m.ItemType switch
        {
            "thinking" => ThinkingTemplate ?? AssistantTextTemplate!,
            "tool_call" => ToolCallTemplate ?? AssistantTextTemplate!,
            "tool_result" => ToolResultTemplate ?? AssistantTextTemplate!,
            _ => m.Role == "user" ? UserTextTemplate! : AssistantTextTemplate!,
        };
    }
}
