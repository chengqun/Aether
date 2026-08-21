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
using Acme.AppUpdate.Domain;
using Acme.Mobile.Mvvm.Abstractions;
using Acme.Mobile.Mvvm.Services;

namespace Acme.Mobile.Mvvm.ViewModels;

/// <summary>
/// AI 设置 ViewModel：管理股票模式默认 Agent 选择、毓数平台认证等配置。
///
/// Aether 适配：旧 IAgentService.GetAvailableAgentsAsync → <see cref="ICustomAgentCatalog.ListAsync"/>
/// （返回 <c>CustomAgentDefinition</c>，映射成本地 <see cref="AgentInfoDto"/>）。
/// 毓数/云文档认证：Qifu 业务项目隐藏于 slnx，故用 <c>Acme.Mobile.Mvvm.Services</c> 下的本地手机接口
/// （<see cref="IYushuAuthService"/> 等），由 Qifu.Mobile 租户宿主提供适配器实现。
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
    private readonly IYushuAuthService? _yushuAuthService;
    private readonly IYushuTokenSyncService? _yushuTokenSyncService;
    private readonly ICloudDocsAuthService? _cloudDocsAuthService;
    private readonly ICloudDocsTokenSyncService? _cloudDocsTokenSyncService;
    private readonly IPlatformInfo? _platformInfo;
    private readonly IToastService? _toastService;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private int _selectedAgentIndex = -1;

    [ObservableProperty]
    private bool _isYushuAuthenticated;

    [ObservableProperty]
    private string _yushuStatusText = "未认证";

    [ObservableProperty]
    private bool _isYushuLoggingIn;

    [ObservableProperty]
    private bool _isYushuSyncing;

    [ObservableProperty]
    private bool _isCloudDocsAuthenticated;

    [ObservableProperty]
    private string _cloudDocsStatusText = "未认证";

    [ObservableProperty]
    private bool _isCloudDocsLoggingIn;

    [ObservableProperty]
    private bool _isCloudDocsSyncing;

    /// <summary>是否为桌面平台（支持浏览器 OAuth 登录）</summary>
    public bool IsDesktopPlatform => _platformInfo?.GetAppPlatform() is AppPlatform.Windows or AppPlatform.MacOS;

    /// <summary>是否接入了云文档认证服务（未接入时隐藏云文档卡片，如 MAUI）</summary>
    public bool HasCloudDocs => _cloudDocsAuthService != null;

    /// <summary>是否接入了毓数认证服务（未接入时隐藏毓数卡片——Stockapp 不注册毓数适配器）</summary>
    public bool HasYushu => _yushuAuthService != null;

    /// <summary>是否为股票租户（Stockapp）。股票模式 AI 助手选择仅 Stockapp 显示；Qifu 不串股票功能。</summary>
    public bool IsStockTenant => _serverOptions?.Tenant == "stockapp";

    /// <summary>所有可用的 Agent/Workflow 列表</summary>
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
                               AppServerOptions? serverOptions = null,
                               IYushuAuthService? yushuAuthService = null,
                               IYushuTokenSyncService? yushuTokenSyncService = null,
                               ICloudDocsAuthService? cloudDocsAuthService = null,
                               ICloudDocsTokenSyncService? cloudDocsTokenSyncService = null,
                               IPlatformInfo? platformInfo = null,
                               IToastService? toastService = null)
    {
        _agentCatalog = agentCatalog;
        _appSettings = appSettings;
        _serverOptions = serverOptions;
        _yushuAuthService = yushuAuthService;
        _yushuTokenSyncService = yushuTokenSyncService;
        _cloudDocsAuthService = cloudDocsAuthService;
        _cloudDocsTokenSyncService = cloudDocsTokenSyncService;
        _platformInfo = platformInfo;
        _toastService = toastService;

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
        await CheckYushuAuthStatusAsync();
        await CheckCloudDocsAuthStatusAsync();
    }

    [RelayCommand]
    public async Task LoadDataAsync()
    {
        if (_agentCatalog == null) return;

        IsLoading = true;
        try
        {
            // 将 I/O 放到后台线程：ListAsync 读 Notes
            var agents = await Task.Run(async () => await _agentCatalog.ListAsync());
            AvailableAgents.Clear();
            foreach (var a in agents)
            {
                AvailableAgents.Add(new AgentInfoDto
                {
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

            // 从配置读取已保存的选择并匹配到 Picker 索引
            var savedName = await GetStockModeAgentNameAsync();
            var idx = AvailableAgents.IndexOf(AvailableAgents.FirstOrDefault(a => a.Name == savedName));
            if (idx >= 0)
            {
                SelectedAgentIndex = idx;
            }
            else
            {
                // 保存的选择无效，回退到默认
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
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AiSettingsViewModel] 加载Agent列表失败: {ex.Message}");
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
    /// 检查毓数平台认证状态
    /// </summary>
    private async Task CheckYushuAuthStatusAsync()
    {
        if (_yushuAuthService == null) return;

        try
        {
            var isAuth = await Task.Run(async () => await _yushuAuthService.IsAuthenticatedAsync());
            IsYushuAuthenticated = isAuth;
            YushuStatusText = isAuth ? "已认证" : "未认证";
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AiSettingsViewModel] 检查毓数认证状态失败: {ex.Message}");
            IsYushuAuthenticated = false;
            YushuStatusText = "状态未知";
        }
    }

    /// <summary>
    /// 毓数平台登录命令（桌面端走浏览器 OAuth，移动端从服务器同步 token）
    /// </summary>
    [RelayCommand]
    private async Task YushuLoginAsync()
    {
        if (_yushuAuthService == null || IsYushuLoggingIn) return;

        IsYushuLoggingIn = true;
        YushuStatusText = IsDesktopPlatform ? "正在登录..." : "正在从服务器同步...";

        try
        {
            var token = await Task.Run(async () => await _yushuAuthService.LoginAsync());

            if (token != null)
            {
                IsYushuAuthenticated = true;
                YushuStatusText = "已认证";
                await (_toastService?.ShowAsync(IsDesktopPlatform ? "毓数平台登录成功" : "毓数平台同步成功") ?? Task.CompletedTask);
            }
            else
            {
                YushuStatusText = IsDesktopPlatform ? "登录失败" : "同步失败（服务器无有效token）";
                await (_toastService?.ShowAsync("操作失败，请重试") ?? Task.CompletedTask);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AiSettingsViewModel] 毓数登录失败: {ex.Message}");
            YushuStatusText = "操作失败";
            await (_toastService?.ShowAsync("操作失败，请重试") ?? Task.CompletedTask);
        }
        finally
        {
            IsYushuLoggingIn = false;
        }
    }

    /// <summary>
    /// 手动将本地毓数 token 同步到服务器（供桌面端使用）
    /// </summary>
    [RelayCommand]
    private async Task YushuSyncToServerAsync()
    {
        if (_yushuTokenSyncService == null || IsYushuSyncing) return;

        IsYushuSyncing = true;
        YushuStatusText = "正在同步到服务器...";

        try
        {
            var success = await Task.Run(async () => await _yushuTokenSyncService.SyncToServerAsync());

            if (success)
            {
                YushuStatusText = "已认证（同步成功）";
                await (_toastService?.ShowAsync("Token 已同步到服务器") ?? Task.CompletedTask);
            }
            else
            {
                YushuStatusText = IsYushuAuthenticated ? "已认证（同步失败）" : "同步失败";
                await (_toastService?.ShowAsync("同步失败，请检查网络连接") ?? Task.CompletedTask);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AiSettingsViewModel] 毓数同步失败: {ex.Message}");
            YushuStatusText = IsYushuAuthenticated ? "已认证（同步失败）" : "同步失败";
            await (_toastService?.ShowAsync("同步失败，请重试") ?? Task.CompletedTask);
        }
        finally
        {
            IsYushuSyncing = false;
        }
    }

    /// <summary>
    /// 检查云文档认证状态
    /// </summary>
    private async Task CheckCloudDocsAuthStatusAsync()
    {
        if (_cloudDocsAuthService == null) return;

        try
        {
            var isAuth = await Task.Run(async () => await _cloudDocsAuthService.IsAuthenticatedAsync());
            IsCloudDocsAuthenticated = isAuth;
            CloudDocsStatusText = isAuth ? "已认证" : "未认证";
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AiSettingsViewModel] 检查云文档认证状态失败: {ex.Message}");
            IsCloudDocsAuthenticated = false;
            CloudDocsStatusText = "状态未知";
        }
    }

    /// <summary>
    /// 云文档登录命令：桌面端打开浏览器完成 SSO 登录并捕获 sid（需先 `playwright install chromium`）；
    /// 手机端从服务器拉取桌面端已上传的 sid（与毓数手机端行为一致）。
    /// </summary>
    [RelayCommand]
    private async Task CloudDocsLoginAsync()
    {
        if (_cloudDocsAuthService == null || IsCloudDocsLoggingIn) return;

        IsCloudDocsLoggingIn = true;
        CloudDocsStatusText = IsDesktopPlatform ? "正在打开浏览器登录..." : "正在从服务器同步...";

        try
        {
            bool success;
            if (IsDesktopPlatform)
            {
                // 桌面端：Playwright 浏览器 SSO 登录捕获 sid
                var result = await Task.Run(async () => await _cloudDocsAuthService.LoginAsync());
                success = result?.Success == true;
                if (!success)
                {
                    await (_toastService?.ShowAsync(result?.Error ?? "登录失败，请重试") ?? Task.CompletedTask);
                }
            }
            else
            {
                // 手机端：从服务器拉取 sid（依赖桌面端已上传）
                if (_cloudDocsTokenSyncService == null)
                {
                    success = false;
                }
                else
                {
                    var synced = await Task.Run(async () => await _cloudDocsTokenSyncService.SyncFromServerAsync());
                    // 拉取后再校验 sid 是否有效
                    success = synced && await Task.Run(async () => await _cloudDocsAuthService.IsAuthenticatedAsync());
                }
            }

            if (success)
            {
                IsCloudDocsAuthenticated = true;
                CloudDocsStatusText = "已认证";
                await (_toastService?.ShowAsync(IsDesktopPlatform ? "云文档登录成功" : "云文档同步成功") ?? Task.CompletedTask);
            }
            else
            {
                CloudDocsStatusText = IsDesktopPlatform ? "登录失败" : "同步失败（服务器无有效sid）";
                if (IsDesktopPlatform)
                {
                    // 桌面端失败提示已在上方给出，避免重复
                }
                else
                {
                    await (_toastService?.ShowAsync("同步失败，服务器无有效sid") ?? Task.CompletedTask);
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AiSettingsViewModel] 云文档登录失败: {ex.Message}");
            CloudDocsStatusText = "操作失败";
            await (_toastService?.ShowAsync("操作失败，请重试") ?? Task.CompletedTask);
        }
        finally
        {
            IsCloudDocsLoggingIn = false;
        }
    }

    /// <summary>
    /// 手动将本地云文档 sid 同步到服务器（供手机端拉取使用）
    /// </summary>
    [RelayCommand]
    private async Task CloudDocsSyncToServerAsync()
    {
        if (_cloudDocsTokenSyncService == null || IsCloudDocsSyncing) return;

        IsCloudDocsSyncing = true;
        CloudDocsStatusText = "正在同步到服务器...";

        try
        {
            var success = await Task.Run(async () => await _cloudDocsTokenSyncService.SyncToServerAsync());

            if (success)
            {
                CloudDocsStatusText = "已认证（同步成功）";
                await (_toastService?.ShowAsync("sid 已同步到服务器") ?? Task.CompletedTask);
            }
            else
            {
                CloudDocsStatusText = IsCloudDocsAuthenticated ? "已认证（同步失败）" : "同步失败";
                await (_toastService?.ShowAsync("同步失败，请检查网络连接") ?? Task.CompletedTask);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AiSettingsViewModel] 云文档同步失败: {ex.Message}");
            CloudDocsStatusText = IsCloudDocsAuthenticated ? "已认证（同步失败）" : "同步失败";
            await (_toastService?.ShowAsync("同步失败，请重试") ?? Task.CompletedTask);
        }
        finally
        {
            IsCloudDocsSyncing = false;
        }
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
}
