using System.Threading;
using System.Threading.Tasks;

namespace Acme.Desktop.Mvvm;

/// <summary>提交消息生成服务：取当前项目目录的 git 变更（staged 优先，无则全部）的 diff 摘要 → LLM 生成提交消息。
/// 接口 UI-agnostic（Mvvm）；impl 在 Shared/Shell，照 <see cref="Aether.AI.Hosting.IConversationSummaryService"/> 范式。
/// 不提交、不暂存——只返回消息文本，调用方（SCM 页 ✨ 按钮）填入输入框供用户编辑后提交。
/// 失败 / 无变更 / 无 LLM 返回 null（调用方退化提示）。</summary>
public interface ICommitMessageService
{
    /// <summary>生成提交消息文本。modelId=null 用当前选中 LLM（镜像 TitleService 范式——提交消息非某 agent 产物，不绑 agent 模型）。
    /// 返回首行简明描述（可选附 body）；失败 / 无变更 / 无 LLM 返回 null。</summary>
    Task<string?> GenerateAsync(string? modelId = null, CancellationToken cancellationToken = default);
}
