using System;
using System.Threading.Tasks;
using Acme.DataQuery.Application;
using Acme.DataQuery.Domain;
using Acme.Mobile.Mvvm.Abstractions;
using Acme.Mobile.Mvvm.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Acme.Mobile.Mvvm.ViewModels.Connector;

/// <summary>
/// MySQL 连接器卡（手机端，UI 无关，共享——两租户通用）。
///
/// 镜像桌面 <c>MySqlConnectorCardViewModel</c>，差异：
/// - 持久化走 <see cref="IAppSettings"/>（手机沙盒 preferences.json，<c>SetString</c> 同步落盘）而非 <c>IPreferencesStore</c>；
/// - 不弹设置窗（桌面 <c>IConnectorSettingsDialogService</c>），而是暴露 <see cref="SettingsPageRoute"/>="MySqlSettingsPage"，
///   由 <see cref="ConnectorViewModel"/> 接管 PrimaryCommand 为「导航到该子页」——表单绑本卡单例。
///
/// 配置键名 <c>mysql.host/port/user/password/database/configured</c>（与租户 <c>MauiProgram.cs</c> 启动期 hydration 同键，
/// 故手机端在 AI 设置页外配完即对 AI 的 <c>ExecuteSql</c> 工具生效）。字段同时镜像进 <see cref="DataQueryOptions"/> 单例——
/// <c>MySqlSqlExecutor</c> 每次查询现读 options 建连接串，故改字段即时生效（无需重启）。
/// <c>_configured</c>：用户曾成功「测试连接」过；重启后据此尝试重连确认（镜像桌面/THS「config-ready」语义）。
/// </summary>
public sealed partial class MySqlConnectorCard : ConnectorCardViewModel
{
    private const string KeyHost = "mysql.host";
    private const string KeyPort = "mysql.port";
    private const string KeyUser = "mysql.user";
    private const string KeyPassword = "mysql.password";
    private const string KeyDatabase = "mysql.database";
    private const string KeyConfigured = "mysql.configured";

    private readonly IAppSettings? _settings;
    private readonly DataQueryOptions? _options;
    private readonly ISqlQueryService? _sql;
    private readonly IToastService? _toast;

    /// <summary>用户曾成功「测试连接」过（配置已验证）。持久化，重启后据此尝试重连确认。</summary>
    private bool _configured;

    public MySqlConnectorCard(
        IAppSettings? settings,
        DataQueryOptions? options,
        ISqlQueryService? sql,
        IToastService? toast)
    {
        _settings = settings;
        _options = options;
        _sql = sql;
        _toast = toast;

        Icon = "🗄️";
        IconBackground = "#FFF3E0";
        Title = "MySQL 数据库";
        Description = "连接 MySQL 数据库，供 AI 执行任意 SQL 查询";
        PrimaryButtonText = "配置连接";

        // 启动期用已持久化的配置回填字段 + 同步进 options（executor 读 options 即时生效）。
        // IAppSettings.GetString 同步（MauiAppSettings 内存 cache），ctor 期安全读。
        _host = settings?.GetString(KeyHost, "") ?? "";
        _port = settings?.GetString(KeyPort, DataQueryDefaults.DefaultPort.ToString()) ?? DataQueryDefaults.DefaultPort.ToString();
        _user = settings?.GetString(KeyUser, "") ?? "";
        _password = settings?.GetString(KeyPassword, "") ?? "";
        _database = settings?.GetString(KeyDatabase, "") ?? "";
        var cfg = settings?.GetString(KeyConfigured, "false") ?? "false";
        _configured = bool.TryParse(cfg, out var b) && b;
        SyncToOptions();
    }

    /// <summary>非空 → ConnectorViewModel 接管 PrimaryCommand 为「导航到 MySqlSettingsPage」。</summary>
    public override string SettingsPageRoute => "MySqlSettingsPage";

    [ObservableProperty] private string _host = "";
    [ObservableProperty] private string _port = DataQueryDefaults.DefaultPort.ToString();
    [ObservableProperty] private string _user = "";
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private string _database = "";

    [ObservableProperty] private bool _isBusy;
    partial void OnIsBusyChanged(bool value) => IsPrimaryEnabled = !value;

    partial void OnHostChanged(string value) => PersistAndSync(KeyHost, value);
    partial void OnPortChanged(string value) => PersistAndSync(KeyPort, value);
    partial void OnUserChanged(string value) => PersistAndSync(KeyUser, value);
    partial void OnPasswordChanged(string value) => PersistAndSync(KeyPassword, value);
    partial void OnDatabaseChanged(string value) => PersistAndSync(KeyDatabase, value);

    /// <summary>持久化字段到 preferences + 同步进 DataQueryOptions（即时生效）。
    /// IAppSettings.SetString 同步落盘（MauiAppSettings 内存 cache + 写 JSON），UI 线程安全。</summary>
    private void PersistAndSync(string key, string value)
    {
        _settings?.SetString(key, value ?? "");
        SyncToOptions();
    }

    /// <summary>把当前字段同步进 <see cref="DataQueryOptions"/>（executor 每次查询现读）。</summary>
    private void SyncToOptions()
    {
        if (_options is null) return;
        _options.Host = Host;
        _options.Port = int.TryParse(Port, out var p) && p > 0 ? p : DataQueryDefaults.DefaultPort;
        _options.User = User;
        _options.Password = Password;
        _options.Database = Database;
    }

    /// <summary>进入连接器页/切回时刷新：若曾配置过则试连确认；否则显示「未配置」。</summary>
    public override async Task RefreshAsync()
    {
        if (_configured)
            await ConnectCoreAsync(autoConfirm: true);
        else
        {
            IsConnected = false;
            StatusText = "未配置";
        }
    }

    /// <summary>设置子页「测试连接」按钮：跑 SELECT 1，成功设 configured + 持久化。</summary>
    [RelayCommand]
    private Task ConnectAsync() => ConnectCoreAsync(autoConfirm: false);

    /// <summary>连接（跑 SELECT 1 确认能连上）。
    /// autoConfirm=true：由 RefreshAsync 自动发起的重连确认——失败时不报「连接失败」，
    /// 而是提示「未连接」（配置仍可能有效，只是 DB 暂不可达）。</summary>
    private async Task ConnectCoreAsync(bool autoConfirm)
    {
        if (_sql is null)
        {
            StatusText = "SQL 服务未注册";
            await (_toast?.ShowAsync("SQL 服务未注册") ?? Task.CompletedTask);
            return;
        }
        if (!IsConfiguredFields)
        {
            StatusText = autoConfirm ? "未配置" : "请填写主机/用户名等连接信息";
            IsConnected = false;
            return;
        }
        IsBusy = true;
        IsPrimaryInProgress = true;
        StatusText = "正在测试连接...";
        try
        {
            var r = await _sql.ExecuteAsync("SELECT 1");
            if (r.Success)
            {
                IsConnected = true;
                StatusText = "已连接";
                if (!_configured)
                {
                    _configured = true;
                    _settings?.SetString(KeyConfigured, "true");
                }
                await (_toast?.ShowAsync("连接成功") ?? Task.CompletedTask);
            }
            else
            {
                IsConnected = false;
                StatusText = autoConfirm ? $"未连接（{r.Error ?? "无法连接"}）" : $"连接失败：{r.Error}";
                await (_toast?.ShowAsync(autoConfirm ? "无法连接数据库" : "连接失败") ?? Task.CompletedTask);
            }
        }
        catch (Exception ex)
        {
            IsConnected = false;
            StatusText = autoConfirm ? $"未连接（{ex.Message}）" : $"连接失败：{ex.Message}";
            await (_toast?.ShowAsync("连接失败，请重试") ?? Task.CompletedTask);
        }
        finally
        {
            IsBusy = false;
            IsPrimaryInProgress = false;
        }
    }

    /// <summary>关键字段（host+user）是否已填——决定是否可试连。</summary>
    private bool IsConfiguredFields =>
        !string.IsNullOrWhiteSpace(Host) && !string.IsNullOrWhiteSpace(User);
}
