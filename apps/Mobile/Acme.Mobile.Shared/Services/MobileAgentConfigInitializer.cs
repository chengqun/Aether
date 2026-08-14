using Acme.Mobile.Mvvm.Services;
using Acme.Shared.HostGlue;

namespace Acme.Mobile.Shared.Services;

/// <summary>
/// LLM 配置变更后失效运行时缓存（<see cref="LlmConfigChatClientFactory"/> 已建客户端 +
/// <see cref="LlmConfigModelSource"/> 模型列表），下轮 agent run / 下拉用新配置。
/// 镜像桌面 <c>MainViewModelBase.RefreshLlmConfigsAsync</c>。
/// </summary>
public sealed class MobileAgentConfigInitializer : IAgentConfigInitializer
{
    private readonly LlmConfigChatClientFactory _chatClientFactory;
    private readonly LlmConfigModelSource _modelSource;

    public MobileAgentConfigInitializer(LlmConfigChatClientFactory chatClientFactory, LlmConfigModelSource modelSource)
    {
        _chatClientFactory = chatClientFactory;
        _modelSource = modelSource;
    }

    public Task RefreshLlmConfigAsync()
    {
        _chatClientFactory.ClearCache();
        _modelSource.Invalidate();
        return Task.CompletedTask;
    }
}
