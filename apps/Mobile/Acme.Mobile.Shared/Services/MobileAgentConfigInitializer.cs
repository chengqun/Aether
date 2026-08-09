using Acme.Mobile.Mvvm.Services;
using Acme.Mobile.Shared.Hosting;

namespace Acme.Mobile.Shared.Services;

/// <summary>
/// LLM 配置变更后失效运行时缓存（<see cref="MobileChatClientFactory"/> 已建客户端 +
/// <see cref="MobileLlmConfigModelSource"/> 模型列表），下轮 agent run / 下拉用新配置。
/// 镜像桌面 <c>MainViewModelBase.RefreshLlmConfigsAsync</c>。
/// </summary>
public sealed class MobileAgentConfigInitializer : IAgentConfigInitializer
{
    private readonly MobileChatClientFactory _chatClientFactory;
    private readonly MobileLlmConfigModelSource _modelSource;

    public MobileAgentConfigInitializer(MobileChatClientFactory chatClientFactory, MobileLlmConfigModelSource modelSource)
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
