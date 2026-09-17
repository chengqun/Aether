using Acme.Llm.Application;
using Acme.Llm.Domain;

namespace Acme.Desktop.Mvvm;

/// <summary>
/// 聊天头 LLM 选择逻辑：从 ChatWorkspaceViewModel 抽出（2026-09 ChatVM 拆分 B2），方法体逐字搬入。
/// 负责「加载配置列表 + 恢复上次选中（saved → IsDefault → first）+ 同步 holder」与
/// 「选中变化 → 持久化 + 更新 holder + invalidate 缓存 agent」两件事。
/// <para>VM 留 <c>LlmSelectItems</c>/<c>SelectedLlmItem</c> bindable + <c>_isInitializing</c> 守卫的
/// <c>OnSelectedLlmItemChanged</c> 壳；集合填充/赋值留在 VM（保持原有线程语义——LoadLlmConfigsAsync 不自带
/// dispatcher 包裹，由调用方 InitializeAsync（UI 线程）/RefreshLlmConfigsAsync（已包 InvokeAsync）保证）。</para>
/// <para>invalidate 经 <c>Action</c> 委托注入（遍历 Experts + _agentRunner.Invalidate）而非 ExpertsViewModel——
/// 本类可脱离 VM 直接单测。</para>
/// </summary>
public sealed class ChatLlmSelectionService
{
    private readonly ILlmConfigService _llmService;
    private readonly IPreferencesStore _preferences;
    private readonly Aether.AI.Harness.SelectedLlmHolder? _holder;
    private readonly Action? _invalidateExperts;

    public ChatLlmSelectionService(ILlmConfigService llmService, IPreferencesStore preferences,
        Aether.AI.Harness.SelectedLlmHolder? holder, Action? invalidateExperts)
    {
        _llmService = llmService;
        _preferences = preferences;
        _holder = holder;
        _invalidateExperts = invalidateExperts;
    }

    /// <summary>加载 LLM 配置并解析应选中项（saved → IsDefault → first；holder 同步）。
    /// 返回 (null, null) = 加载失败（吞异常）。configs 为空列表时 selected 为 null。</summary>
    public async Task<(List<LlmConfigDto>? Configs, LlmConfigDto? Selected)> LoadAsync()
    {
        try
        {
            var configs = await _llmService.GetCachedAsync();
            if (configs != null)
            {
                // 选中：上次保存的 → IsDefault → 第一个
                var savedId = await _preferences.GetAsync("selectedLlmId");
                var saved = Guid.TryParse(savedId, out var lid)
                    ? configs.FirstOrDefault(c => c.Id == lid)
                    : null;
                var selected = saved ?? configs.FirstOrDefault(c => c.IsDefault) ?? configs.FirstOrDefault();
                // 启动时同步 holder（首次 agent 构建就能用对的 LLM）
                if (selected is not null && _holder is not null)
                    _holder.LlmConfigId = selected.Id.ToString();
                return (configs.ToList(), selected);
            }
        }
        catch
        {
        }
        return (null, null);
    }

    /// <summary>选中 LLM 变化：持久化 + 更新 holder + invalidate 缓存 agent（下轮 run 用新 LLM 重建）。
    /// 原语义：持久化无条件（值非空即写）；holder/invalidate 仅在 holder 已解析时执行。</summary>
    public void OnSelectionChanged(LlmConfigDto? value)
    {
        if (value is not null)
        {
            _preferences.SetAsync("selectedLlmId", value.Id.ToString()).SafeFireAndForget();

            // 更新 LLM holder + invalidate 所有缓存的 agent → 下轮 run 用新 LLM 重建
            if (_holder is not null)
            {
                _holder.LlmConfigId = value.Id.ToString();
                _invalidateExperts?.Invoke();
            }
        }
    }
}
