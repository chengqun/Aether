using Acme.Mobile.Mvvm.Services;

namespace Acme.Mobile.Shared.Services;

/// <summary>
/// 手机端登录后初始化（三段式）。镜像 AlphaAgent <c>PostLoginInitializer</c>，适配 Aether 契约：
/// <para>Tier1 <see cref="InitializeCriticalAsync"/>（阻塞，Splash 自动登录成功后调）：应用缓存 LLM 配置到
/// 运行时（<c>IAgentConfigInitializer.RefreshLlmConfigAsync</c>），无 HTTP。不在此启 SignalR（ChatViewModel
/// 首次激活时 lazy 连，同 AlphaAgent）。</para>
/// <para>Tier2 <see cref="StartBackgroundSyncsAsync"/>（fire-and-forget，ChatPage 激活后调）：catch-all，永不阻塞 UI。
/// 各缓存/会话同步由对应 VM 激活时自行触发，此处仅作占位 + 未来扩展（如后台证券同步）。</para>
/// </summary>
public sealed class MobilePostLoginInitializer : IPostLoginInitializer
{
    private readonly IAgentConfigInitializer? _agentConfigInitializer;

    public MobilePostLoginInitializer(IAgentConfigInitializer? agentConfigInitializer = null)
        => _agentConfigInitializer = agentConfigInitializer;

    public async Task<PostLoginResult> InitializeCriticalAsync()
    {
        var loaded = false;
        try
        {
            if (_agentConfigInitializer != null)
            {
                await _agentConfigInitializer.RefreshLlmConfigAsync().ConfigureAwait(false);
                loaded = true;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[PostLogin] Tier1 LLM 配置加载失败: {ex.Message}");
        }
        return new PostLoginResult { AgentConfigLoaded = loaded };
    }

    public async Task StartBackgroundSyncsAsync()
    {
        try
        {
            // Tier2 占位：各社交缓存的同步由对应 VM 激活时触发（cache-first + 后台增量）。
            // 未来可在此追加后台证券同步等租户无关的 fire-and-forget 任务。
            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[PostLogin] Tier2 后台同步异常: {ex.Message}");
        }
    }
}
