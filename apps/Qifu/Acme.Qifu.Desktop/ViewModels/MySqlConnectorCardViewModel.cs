using Acme.DataQuery.Application;
using Acme.DataQuery.Domain;
using Acme.Desktop.Mvvm;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Acme.Qifu.Desktop.ViewModels;

/// <summary>
/// MySQL 连接器卡片（连接器 tab 的一张子面板，UI 无关）。
/// 镜像 Stockapp 的 <c>ThsConnectorViewModel</c>：配置持久化到 <see cref="IPreferencesStore"/>，
/// 「+」弹设置窗填 host/port/user/password/database + 测试连接。
/// 连接参数同时写入 <see cref="DataQueryOptions"/>（Domain 可变 POCO，DI 单例）——
/// <c>MySqlSqlExecutor</c> 每次查询现读 options 建连接串，故改字段即时生效（无需重启）。
/// <c>_configured</c>：用户曾成功「测试连接」过（配置已验证）。重启后据此尝试重连确认。
/// </summary>
public sealed partial class MySqlConnectorCardViewModel : ConnectorCardViewModel
{
    private const string KeyHost = "mysql.host";
    private const string KeyPort = "mysql.port";
    private const string KeyUser = "mysql.user";
    private const string KeyPassword = "mysql.password";
    private const string KeyDatabase = "mysql.database";
    private const string KeyConfigured = "mysql.configured";

    private readonly IConnectorSettingsDialogService? _dialogs;
    private readonly IPreferencesStore? _preferences;
    private readonly DataQueryOptions? _options;
    private readonly ISqlQueryService? _sql;

    /// <summary>用户曾成功「测试连接」过（配置已验证）。持久化，重启后据此尝试重连确认。</summary>
    private bool _configured;

    public MySqlConnectorCardViewModel(
        IConnectorSettingsDialogService? dialogs,
        IPreferencesStore? preferences,
        DataQueryOptions? options,
        ISqlQueryService? sql)
    {
        _dialogs = dialogs;
        _preferences = preferences;
        _options = options;
        _sql = sql;

        Icon = "🗄️";
        IconBackground = "#FFF3E0";
        Title = "MySQL 数据库";
        Description = "连接 MySQL 数据库，供 AI 执行任意 SQL 查询";
        PrimaryCommand = new AsyncRelayCommand(() => dialogs is null ? Task.CompletedTask : dialogs.ShowSettingsAsync(this));

        // 启动期用已持久化的配置回填字段 + 同步进 options（executor 读 options 即时生效）
        _host = preferences?.GetAsync(KeyHost).GetAwaiter().GetResult() ?? "";
        _port = preferences?.GetAsync(KeyPort).GetAwaiter().GetResult() ?? DataQueryDefaults.DefaultPort.ToString();
        _user = preferences?.GetAsync(KeyUser).GetAwaiter().GetResult() ?? "";
        _password = preferences?.GetAsync(KeyPassword).GetAwaiter().GetResult() ?? "";
        _database = preferences?.GetAsync(KeyDatabase).GetAwaiter().GetResult() ?? "";
        var cfg = preferences?.GetAsync(KeyConfigured).GetAwaiter().GetResult();
        _configured = bool.TryParse(cfg, out var b) && b;
        SyncToOptions();
    }

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

    /// <summary>持久化字段到 preferences + 同步进 DataQueryOptions（即时生效）。</summary>
    private void PersistAndSync(string key, string value)
    {
        _preferences?.SetAsync(key, string.IsNullOrEmpty(value) ? null : value).SafeFireAndForget();
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

    /// <summary>启动期/切到连接器 tab 时刷新：若曾配置过则试连确认；否则显示「未配置」。</summary>
    public override async Task RefreshAsync()
    {
        if (_configured)
        {
            await ConnectCoreAsync(autoConfirm: true);
        }
        else
        {
            IsConnected = false;
            StatusText = "未配置";
        }
    }

    /// <summary>设置窗「测试连接」按钮：跑 SELECT 1，成功设 configured + 持久化。</summary>
    [RelayCommand]
    private Task ConnectAsync() => ConnectCoreAsync(autoConfirm: false);

    /// <summary>连接（跑 SELECT 1 确认能连上）。
    /// autoConfirm=true：由 RefreshAsync 自动发起的重连确认——失败时不报「连接失败」，
    /// 而是提示「无法连接」（配置仍可能有效，只是 DB 暂不可达）。</summary>
    private async Task ConnectCoreAsync(bool autoConfirm)
    {
        if (_sql is null)
        {
            StatusText = "SQL 服务未注册";
            return;
        }
        if (!IsConfiguredFields)
        {
            StatusText = autoConfirm ? "未配置" : "请填写 host/user 等连接信息";
            IsConnected = false;
            return;
        }
        IsBusy = true;
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
                    _preferences?.SetAsync(KeyConfigured, "true").SafeFireAndForget();
                }
            }
            else
            {
                IsConnected = false;
                StatusText = autoConfirm ? $"未连接（{r.Error ?? "无法连接"}）" : $"连接失败：{r.Error}";
            }
        }
        catch (Exception ex)
        {
            IsConnected = false;
            StatusText = autoConfirm ? $"未连接（{ex.Message}）" : $"连接失败：{ex.Message}";
        }
        finally { IsBusy = false; }
    }

    /// <summary>关键字段（host+user）是否已填——决定是否可试连。</summary>
    private bool IsConfiguredFields =>
        !string.IsNullOrWhiteSpace(Host) && !string.IsNullOrWhiteSpace(User);
}
