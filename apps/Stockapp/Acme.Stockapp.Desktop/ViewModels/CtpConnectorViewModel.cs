using System.Collections.ObjectModel;
using Acme.Ctp.Application;
using Acme.Ctp.Domain;
using Acme.Desktop.Mvvm;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Acme.Stockapp.Desktop.ViewModels;

/// <summary>
/// CTP 期货连接器卡片 ViewModel（Stockapp 租户）。连接/断开 CTP、持久化凭据、显示连接状态。
/// 继承 <see cref="ConnectorCardViewModel"/> 作为连接器 tab 的一张卡片；「+」打开 CTP 设置对话框
/// （<see cref="CtpLoginWindow"/>，对话框内填表单 + 连接）。UI 无关。
/// </summary>
public sealed partial class CtpConnectorViewModel : ConnectorCardViewModel
{
    private readonly ICtpTradingService _trading;
    private readonly ICtpConfigStore _configStore;

    public CtpConnectorViewModel(ICtpTradingService trading, ICtpConfigStore configStore,
        IConnectorSettingsDialogService dialogs)
    {
        _trading = trading ?? throw new ArgumentNullException(nameof(trading));
        _configStore = configStore ?? throw new ArgumentNullException(nameof(configStore));

        Icon = "📈";
        IconBackground = "#E8F5E9";
        Title = "CTP 期货交易";
        Description = "连接期货柜台，供交易/行情工具调用";
        PrimaryCommand = new AsyncRelayCommand(() => dialogs.ShowSettingsAsync(this));

        _trading.ConnectionStateChanged += OnConnectionStateChanged;
        IsConnected = _trading.IsConnected;
        StatusText = IsConnected ? "已连接" : "未连接";
    }

    [ObservableProperty] private string _frontAddress = string.Empty;
    [ObservableProperty] private string _mdFrontAddress = string.Empty;
    [ObservableProperty] private string _brokerId = string.Empty;
    [ObservableProperty] private string _userId = string.Empty;
    [ObservableProperty] private string _password = string.Empty;
    [ObservableProperty] private string _appId = string.Empty;
    [ObservableProperty] private string _authCode = string.Empty;
    [ObservableProperty] private bool _autoLogin;

    [ObservableProperty] private bool _isConnecting;

    /// <summary>连接标签（chip）列表：点 chip = 应用该 profile 并直接登录（见 <see cref="ApplyProfileAsync"/>）。</summary>
    public ObservableCollection<CtpProfileItem> Profiles { get; } = new();

    [ObservableProperty] private bool _hasProfiles;

    /// <summary>chip → 完整 <see cref="CtpConnectionConfig"/> 查找缓存（与 <see cref="Profiles"/> 同步重建）。</summary>
    private List<CtpConnectionConfig> _profileConfigs = new();

    /// <summary>当前表单对应的 profile（仅高亮追踪；连接即建档语义见 <see cref="ConnectAsync"/>）。</summary>
    private Guid _currentProfileId;

    /// <summary>
    /// 从存储加载 profile 列表（重建 chips）+ last-used 配置到表单，刷新连接状态，若启用自动登录则自动连接。
    /// 入口全在 UI 线程（CtpViewModel.InitializeAsync / ExpertsViewModel.LoadAsync）——勿加 ConfigureAwait(false)。
    /// </summary>
    public override async Task RefreshAsync()
    {
        if (IsConnecting) return; // 连接中跳过：防重建 chips 与连接流程的保存-刷新互踩

        var profiles = await _configStore.GetAllAsync();
        RebuildProfiles(profiles);

        var cfg = profiles.FirstOrDefault(p => p.IsLastUsed); // = GetAsync 语义，免二次查库
        if (cfg is not null)
        {
            FrontAddress = cfg.FrontAddress;
            MdFrontAddress = cfg.MdFrontAddress;
            BrokerId = cfg.BrokerId;
            UserId = cfg.UserId;
            Password = cfg.Password;
            AppId = cfg.AppId;
            AuthCode = cfg.AuthCode;
            AutoLogin = cfg.AutoLogin;
            MarkCurrentChip(cfg.Id);
        }
        IsConnected = _trading.IsConnected;
        StatusText = IsConnected ? "已连接" : "未连接";

        // 自动登录：有配置 + 未连接 + AutoLogin 开启
        if (AutoLogin && !IsConnected && !string.IsNullOrWhiteSpace(FrontAddress) && !string.IsNullOrWhiteSpace(UserId))
            await ConnectAsync();
    }

    [RelayCommand]
    private async Task ConnectAsync()
    {
        if (IsConnecting) return;
        if (string.IsNullOrWhiteSpace(FrontAddress) || string.IsNullOrWhiteSpace(UserId))
        {
            StatusText = "请填写交易前置地址与用户号";
            return;
        }

        IsConnecting = true;
        StatusText = "连接中…";
        try
        {
            // 连接即自动建档：键 = 前置地址 + 用户号（命中既有 profile 复用其 Id，否则新建），
            // 保存先于登录尝试（沿用既有语义：失败尝试也留档，误档可用 chip 上的 ✕ 删）
            var profiles = await _configStore.GetAllAsync();
            var id = profiles.FirstOrDefault(p =>
                p.FrontAddress == FrontAddress && p.UserId == UserId)?.Id ?? Guid.NewGuid();
            await _configStore.SaveAsync(new CtpConnectionConfig
            {
                Id = id,
                Name = BuildProfileName(profiles, id, UserId, FrontAddress),
                FrontAddress = FrontAddress,
                MdFrontAddress = MdFrontAddress,
                BrokerId = BrokerId,
                UserId = UserId,
                Password = Password,
                AppId = AppId,
                AuthCode = AuthCode,
                AutoLogin = AutoLogin,
                IsLastUsed = true,
            });
            _currentProfileId = id;
            await LoadProfilesAsync();

            var result = await _trading.LoginAsync(new CtpLoginRequest
            {
                FrontAddress = FrontAddress,
                MdFrontAddress = MdFrontAddress,
                BrokerId = BrokerId,
                UserId = UserId,
                Password = Password,
                AppId = AppId,
                AuthCode = AuthCode,
            });
            StatusText = $"已连接（交易日 {result.TradingDay}）";
        }
        catch (Exception ex)
        {
            StatusText = $"连接失败：{ex.Message}";
        }
        finally
        {
            IsConnecting = false;
            IsConnected = _trading.IsConnected;
        }
    }

    [RelayCommand]
    private async Task DisconnectAsync()
    {
        try
        {
            await _trading.LogoutAsync();
            StatusText = "已断开";
        }
        catch (Exception ex)
        {
            StatusText = $"断开失败：{ex.Message}";
        }
        finally
        {
            IsConnected = _trading.IsConnected;
        }
    }

    /// <summary>
    /// 点连接标签 = 载入该 profile 到表单 + 直接登录。已连接时先登出：
    /// 同柜台换账号 <c>CtpSessionService.LoginAsync</c> 会抛「已有活跃连接，请先登出」，故统一先 Logout 再 Login。
    /// </summary>
    [RelayCommand]
    private async Task ApplyProfileAsync(CtpProfileItem? item)
    {
        if (item is null || IsConnecting) return;
        var cfg = _profileConfigs.FirstOrDefault(p => p.Id == item.Id);
        if (cfg is null) return; // chips 刚被重建的极端时序——静默跳过

        FrontAddress = cfg.FrontAddress;
        MdFrontAddress = cfg.MdFrontAddress;
        BrokerId = cfg.BrokerId;
        UserId = cfg.UserId;
        Password = cfg.Password;
        AppId = cfg.AppId;
        AuthCode = cfg.AuthCode;
        AutoLogin = cfg.AutoLogin;
        MarkCurrentChip(cfg.Id);

        if (IsConnected)
        {
            try { await _trading.LogoutAsync(); }
            catch { /* 登出失败让后续 LoginAsync 的报错兜底 */ }
        }
        await ConnectAsync();
    }

    /// <summary>删除连接标签（不影响当前连接，只删档）。</summary>
    [RelayCommand]
    private async Task DeleteProfileAsync(CtpProfileItem? item)
    {
        if (item is null) return;
        await _configStore.DeleteAsync(item.Id);
        await LoadProfilesAsync();
    }

    /// <summary>拉取 profile 列表并重建 chips（ConnectAsync 保存后 / DeleteProfileAsync 后调用）。</summary>
    private async Task LoadProfilesAsync()
    {
        var profiles = await _configStore.GetAllAsync();
        RebuildProfiles(profiles);
    }

    private void RebuildProfiles(List<CtpConnectionConfig> profiles)
    {
        _profileConfigs = profiles;
        Profiles.Clear();
        foreach (var p in profiles)
            Profiles.Add(new CtpProfileItem { Id = p.Id, Name = p.Name });
        HasProfiles = Profiles.Count > 0;
        MarkCurrentChip(_currentProfileId);
    }

    private void MarkCurrentChip(Guid id)
    {
        _currentProfileId = id;
        foreach (var chip in Profiles)
            chip.IsCurrent = chip.Id == id;
    }

    /// <summary>
    /// 标签名：无其它 profile（Id 不同）占用该 UserId → UserId；否则加前置区分后缀（如「12345·sim」）。
    /// 重名判定仅在建档时做一次（MVP 无改名）。
    /// </summary>
    private static string BuildProfileName(IReadOnlyList<CtpConnectionConfig> profiles, Guid id, string userId, string front)
    {
        var taken = profiles.Any(p => p.Id != id && p.UserId == userId);
        return taken ? $"{userId}·{FrontTag(front)}" : userId;
    }

    /// <summary>前置区分标签：sim（不分大小写）→「sim」；否则剥 tcp:// 取 host；解析不出用原文。纯文本判定，不引 Infrastructure 的 SimAddress。</summary>
    private static string FrontTag(string front)
    {
        var value = front.Trim();
        if (string.Equals(value, "sim", StringComparison.OrdinalIgnoreCase)) return "sim";
        var host = value.StartsWith("tcp://", StringComparison.OrdinalIgnoreCase)
            ? value["tcp://".Length..]
            : value;
        var colon = host.IndexOf(':');
        if (colon > 0) host = host[..colon];
        return string.IsNullOrWhiteSpace(host) ? value : host;
    }

    /// <summary>
    /// 交易前置是否指向本地仿真柜台（镜像 <c>SimAddress.IsSimFront</c> 的纯文本判定——VM 不引 Acme.Ctp.Sim，
    /// 先例 = <see cref="FrontTag"/>）：sim（不分大小写）/ 127.0.0.1:17800（旧 JSON-RPC 形态）/ 127.0.0.1:17801
    /// （CTP 前置交易腿——2026-10-01 起本地柜台统一形态；容忍 tcp:// 前缀与 localhost）。
    /// 行情随柜台：本地柜台登录 ⇒ 行情走前置行情腿 17802（前置地址形态须填行情前置）。
    /// </summary>
    public bool IsSimFront
    {
        get
        {
            var a = FrontAddress.Trim();
            if (string.IsNullOrWhiteSpace(a)) return false;
            if (string.Equals(a, "sim", StringComparison.OrdinalIgnoreCase)) return true;
            var bare = a.StartsWith("tcp://", StringComparison.OrdinalIgnoreCase) ? a["tcp://".Length..] : a;
            bare = bare.Replace("localhost", "127.0.0.1", StringComparison.OrdinalIgnoreCase);
            return string.Equals(bare, "127.0.0.1:17800", StringComparison.OrdinalIgnoreCase)
                || string.Equals(bare, "127.0.0.1:17801", StringComparison.OrdinalIgnoreCase);
        }
    }

    private void OnConnectionStateChanged(object? sender, CtpConnectionStatus status)
    {
        IsConnected = status == CtpConnectionStatus.Connected;
        StatusText = status switch
        {
            CtpConnectionStatus.Connected => "已连接",
            CtpConnectionStatus.Reconnecting => "重连中…",
            CtpConnectionStatus.Closed => "闭市",
            _ => "未连接",
        };
    }
}
