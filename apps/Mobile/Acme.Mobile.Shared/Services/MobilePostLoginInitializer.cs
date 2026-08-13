using Acme.AppAuth.Domain;
using Acme.Mobile.Mvvm.Services;
using Acme.Securities.Application;

namespace Acme.Mobile.Shared.Services;

/// <summary>
/// 手机端登录后初始化（三段式）。镜像 AlphaAgent <c>PostLoginInitializer</c>，适配 Aether 契约：
/// <para>Tier1 <see cref="InitializeCriticalAsync"/>（阻塞，Splash 自动登录成功后调）：应用缓存 LLM 配置到
/// 运行时（<c>IAgentConfigInitializer.RefreshLlmConfigAsync</c>），无 HTTP。不在此启 SignalR（ChatViewModel
/// 首次激活时 lazy 连，同 AlphaAgent）。</para>
/// <para>Tier2 <see cref="StartBackgroundSyncsAsync"/>（fire-and-forget，ChatPage 激活后调）：catch-all，永不阻塞 UI。
/// 各缓存/会话同步由对应 VM 激活时自行触发；此处对行情（Stockapp）租户做后台证券增量同步——
/// Quotes AI 工具查本地 securities.db，不同步则查空库（对标桌面 CtpViewModel 启动 fire-and-forget 同步）。</para>
/// </summary>
public sealed class MobilePostLoginInitializer : IPostLoginInitializer
{
    private readonly IAgentConfigInitializer? _agentConfigInitializer;
    private readonly ISecurityClientSyncService? _securitySyncService;
    private readonly AppServerOptions? _serverOptions;

    public MobilePostLoginInitializer(IAgentConfigInitializer? agentConfigInitializer = null,
                                      ISecurityClientSyncService? securitySyncService = null,
                                      AppServerOptions? serverOptions = null)
    {
        _agentConfigInitializer = agentConfigInitializer;
        _securitySyncService = securitySyncService;
        _serverOptions = serverOptions;
    }

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
            // Tier2：各社交缓存的同步由对应 VM 激活时触发（cache-first + 后台增量）。
            // 行情（Stockapp）租户：后台证券增量同步（fire-and-forget，永不阻塞 UI）。
            // Quotes AI 工具（QuerySecurity）查本地 securities.db，首次全量、后续增量。
            if (_securitySyncService != null && _serverOptions?.Tenant == "stockapp")
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var result = await _securitySyncService.SyncFromServerAsync().ConfigureAwait(false);
                        System.Diagnostics.Debug.WriteLine($"[PostLogin] 后台证券同步完成: {result.SyncedCount} 条");
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[PostLogin] 后台证券同步失败: {ex.Message}");
                    }
                });
            }

            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[PostLogin] Tier2 后台同步异常: {ex.Message}");
        }
    }
}
