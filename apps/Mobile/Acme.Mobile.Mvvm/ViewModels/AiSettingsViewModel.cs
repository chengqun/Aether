using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Acme.Notes.Domain.Agents;
using Acme.AppAuth.Domain;
using Acme.Notes.Domain;
using Acme.Mobile.Mvvm.Models;
using Acme.Mobile.Mvvm.Abstractions;
using Acme.Mobile.Mvvm.Events;
using Acme.Mobile.Mvvm.Services;

namespace Acme.Mobile.Mvvm.ViewModels;

/// <summary>
/// AI 设置 ViewModel：管理 labs 资料库入口 + 股票模式默认 Agent 选择。
///
/// 毓数/云文档/mcphub 认证已迁出至独立「连接器」页（<see cref="ConnectorViewModel"/> + auth 卡），
/// 本页只留 labs 资料库（agent/workflow/skill/mcp/knowledge）入口与 Stockapp 股票模式默认 agent picker。
///
/// Aether 适配：旧 IAgentService.GetAvailableAgentsAsync → <see cref="ICustomAgentCatalog.ListAsync"/>
/// （返回 <c>CustomAgentDefinition</c>，映射成本地 <see cref="AgentInfoDto"/>）。
/// </summary>
public partial class AiSettingsViewModel : NavigableViewModelBase
{
    private const string StockModeAgentNameKey = "Aether_StockModeAgentName";
    private const string DefaultStockModeAgentName = "指标分析Agent";

    /// <summary>
    /// 静态 IAppSettings 引用，供静态方法 GetStockModeAgentName() 使用。
    /// 在 DI 容器构建后由平台层设置（替代 MAUI Preferences 的静态访问）。
    /// </summary>
    private static IAppSettings? _staticAppSettings;

    /// <summary>
    /// 缓存的股票模式 Agent 名称，供静态方法快速返回。
    /// </summary>
    private static string _cachedStockModeAgentName = DefaultStockModeAgentName;

    private readonly ICustomAgentCatalog? _agentCatalog;
    private readonly IAppSettings? _appSettings;
    private readonly AppServerOptions? _serverOptions;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private int _selectedAgentIndex = -1;

    /// <summary>是否为股票租户（Stockapp）。股票模式 AI 助手选择仅 Stockapp 显示；Qifu 不串股票功能。</summary>
    public bool IsStockTenant => _serverOptions?.Tenant == "stockapp";

    /// <summary>所有可用的 Agent/Workflow 列表（股票模式 Picker 用）。</summary>
    public ObservableCollection<AgentInfoDto> AvailableAgents { get; } = new();

    /// <summary>当前选择的描述文本</summary>
    public string CurrentSelectionText
    {
        get
        {
            if (SelectedAgentIndex >= 0 && SelectedAgentIndex < AvailableAgents.Count)
            {
                var agent = AvailableAgents[SelectedAgentIndex];
                return $"当前选择：{agent.Name}\n{agent.Description}";
            }
            return "请选择一个AI助手";
        }
    }

    public AiSettingsViewModel(ICustomAgentCatalog? agentCatalog = null,
                               IAppSettings? appSettings = null,
                               IUIThreadDispatcher? uiThreadDispatcher = null,
                               AppServerOptions? serverOptions = null)
    {
        _agentCatalog = agentCatalog;
        _appSettings = appSettings;
        _serverOptions = serverOptions;

        // 设置静态引用，供 GetStockModeAgentName() 使用
        if (appSettings != null)
            _staticAppSettings = appSettings;

        _uiThreadDispatcher = uiThreadDispatcher;
    }

    /// <summary>
    /// 初始化静态 IAppSettings 引用（在 DI 容器构建后调用）。
    /// </summary>
    public static void InitializeStatic(IAppSettings appSettings)
    {
        _staticAppSettings = appSettings;
        // 预加载缓存
        _cachedStockModeAgentName = appSettings.GetString(StockModeAgentNameKey, DefaultStockModeAgentName);
    }

    public override async Task OnActivatedAsync()
    {
        await LoadDataAsync();
    }

    [RelayCommand]
    public async Task LoadDataAsync()
    {
        IsLoading = true;
        try
        {
            // —— agent/rule 列表（ICustomAgentCatalog，可能为 null）——
            if (_agentCatalog != null)
            {
                var agents = await Task.Run(async () => await _agentCatalog.ListAsync());
                AvailableAgents.Clear();
                foreach (var a in agents)
                {
                    AvailableAgents.Add(new AgentInfoDto
                    {
                        Id = a.Id,
                        Name = a.Name,
                        Description = a.Description,
                        Kind = a.NodeType switch
                        {
                            NoteNodeType.Workflow => AgentKind.Workflow,
                            NoteNodeType.Rule or NoteNodeType.CodeRule => AgentKind.RuleBased,
                            _ => AgentKind.Agent,
                        }
                    });
                }

                // 从配置读取已保存的选择并匹配到 Picker 索引（股票模式默认 agent）
                var savedName = await GetStockModeAgentNameAsync();
                var idx = AvailableAgents.IndexOf(AvailableAgents.FirstOrDefault(a => a.Name == savedName));
                if (idx >= 0)
                {
                    SelectedAgentIndex = idx;
                }
                else
                {
                    idx = AvailableAgents.IndexOf(AvailableAgents.FirstOrDefault(a => a.Name == DefaultStockModeAgentName));
                    if (idx >= 0)
                    {
                        SelectedAgentIndex = idx;
                        await SetStockModeAgentNameAsync(DefaultStockModeAgentName);
                    }
                    else if (AvailableAgents.Count > 0)
                    {
                        SelectedAgentIndex = 0;
                        await SetStockModeAgentNameAsync(AvailableAgents[0].Name);
                    }
                }

                OnPropertyChanged(nameof(CurrentSelectionText));
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AiSettingsViewModel] 加载列表失败: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
        }
    }

    partial void OnSelectedAgentIndexChanged(int value)
    {
        // 保存选择到配置
        if (value >= 0 && value < AvailableAgents.Count)
        {
            var agentName = AvailableAgents[value].Name;
            _ = SetStockModeAgentNameAsync(agentName);
        }
        OnPropertyChanged(nameof(CurrentSelectionText));
    }

    /// <summary>
    /// 获取股票模式默认 Agent 名称（供其他 ViewModel 调用）。
    /// </summary>
    public async Task<string> GetStockModeAgentNameAsync()
    {
        if (_appSettings != null)
        {
            var name = await Task.Run(() => _appSettings.GetString(StockModeAgentNameKey, DefaultStockModeAgentName));
            _cachedStockModeAgentName = name; // 更新缓存
            return name;
        }
        return DefaultStockModeAgentName;
    }

    /// <summary>
    /// 设置股票模式默认 Agent 名称。
    /// </summary>
    public async Task SetStockModeAgentNameAsync(string agentName)
    {
        _cachedStockModeAgentName = agentName; // 更新缓存
        if (_appSettings != null)
        {
            await Task.Run(() => _appSettings.SetString(StockModeAgentNameKey, agentName));
        }
    }

    /// <summary>
    /// 静态方法：获取股票模式默认 Agent 名称（供其他 ViewModel 调用）。
    /// 使用静态 IAppSettings 引用或缓存值，替代 MAUI Preferences.Default.Get。
    /// </summary>
    public static string GetStockModeAgentName()
    {
        if (_staticAppSettings != null)
        {
            try
            {
                return _staticAppSettings.GetString(StockModeAgentNameKey, DefaultStockModeAgentName);
            }
            catch
            {
                return _cachedStockModeAgentName;
            }
        }
        return _cachedStockModeAgentName;
    }

    /// <summary>导航请求事件（AI 设置 → 某类型入口）。</summary>
    public event EventHandler<NavigationRequestedEvent>? NavigationRequested;

    /// <summary>进入某类型的资产列表页（AssetListPage?type=…）。</summary>
    private void NavigateToAssetList(string type)
        => NavigationRequested?.Invoke(this, NavigationRequestedEvent.To("AssetListPage",
            new Dictionary<string, object> { ["type"] = type }));

    [RelayCommand]
    private void NavigateToAgentList() => NavigateToAssetList("agent");

    [RelayCommand]
    private void NavigateToWorkflowList() => NavigateToAssetList("workflow");

    [RelayCommand]
    private void NavigateToSkillList() => NavigateToAssetList("skill");

    [RelayCommand]
    private void NavigateToMcpList() => NavigateToAssetList("mcp");

    [RelayCommand]
    private void NavigateToKnowledgeList() => NavigateToAssetList("knowledge");
}
