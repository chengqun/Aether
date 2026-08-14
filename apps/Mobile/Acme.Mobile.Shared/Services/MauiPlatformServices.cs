using System.Collections.Concurrent;
using System.Text.Json;
using Acme.AppUpdate.Domain;
using Acme.Mobile.Mvvm.Abstractions;
using Acme.Mobile.Mvvm.Services;
using CommunityToolkit.Maui.Alerts;
using CommunityToolkit.Maui.Core;
using Application = Microsoft.Maui.Controls.Application;

namespace Acme.Mobile.Shared.Services;

/// <summary>
/// UI 线程调度：MAUI <c>MainThread.BeginInvokeOnMainThread</c>。
/// </summary>
public sealed class MauiUIThreadDispatcher : IUIThreadDispatcher
{
    public void Post(Action action) => MainThread.BeginInvokeOnMainThread(action);
}

/// <summary>
/// 应用配置：JSON 文件落 <c>preferences.json</c> 于租户目录（与桌面 <c>JsonPreferencesStore</c> 同源，
/// 启动期 <c>PreferencesFileReader.ReadServerUrl</c> 也读此文件，保证一致）。
/// ServerBaseAddress 读 <c>app.serverUrl</c>。
/// </summary>
public sealed class MauiAppSettings : IAppSettings
{
    private readonly string _filePath;
    private readonly object _gate = new();
    private ConcurrentDictionary<string, string?>? _cache;

    public MauiAppSettings(string tenantDir)
    {
        Directory.CreateDirectory(tenantDir);
        _filePath = Path.Combine(tenantDir, "preferences.json");
    }

    public string ServerBaseAddress => GetString("app.serverUrl", string.Empty);

    public string GetString(string key, string defaultValue)
    {
        var values = EnsureLoaded();
        return values.TryGetValue(key, out var v) && v != null ? v : defaultValue;
    }

    public void SetString(string key, string value)
    {
        var values = EnsureLoaded();
        values[key] = value;
        Persist();
    }

    private ConcurrentDictionary<string, string?> EnsureLoaded()
    {
        if (_cache is not null) return _cache;
        lock (_gate)
        {
            if (_cache is not null) return _cache;
            var dict = new ConcurrentDictionary<string, string?>();
            try
            {
                if (File.Exists(_filePath))
                {
                    var doc = JsonSerializer.Deserialize<Dictionary<string, string?>>(File.ReadAllText(_filePath));
                    if (doc is not null)
                        foreach (var kv in doc) dict[kv.Key] = kv.Value;
                }
            }
            catch { /* 损坏则视为空 */ }
            _cache = dict;
            return _cache;
        }
    }

    private void Persist()
    {
        if (_cache is null) return;
        var json = JsonSerializer.Serialize(_cache, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_filePath, json);
    }
}

/// <summary>平台信息：MAUI <c>AppInfo</c> / <c>DeviceInfo</c>。</summary>
public sealed class MauiPlatformInfo : IPlatformInfo
{
    public AppPlatform GetAppPlatform()
    {
        var p = DeviceInfo.Current.Platform;
        if (p == DevicePlatform.Android) return AppPlatform.Android;
        if (p == DevicePlatform.iOS) return AppPlatform.iOS;
        if (p == DevicePlatform.macOS) return AppPlatform.MacOS;
        if (p == DevicePlatform.WinUI) return AppPlatform.Windows;
        return AppPlatform.Unknown;
    }

    public int GetVersionCode()
    {
        var v = AppInfo.Current.Version;
        return (v.Major << 16) | (v.Minor & 0xFFFF);
    }

    public string GetVersionString() => AppInfo.Current.VersionString;
}

/// <summary>轻提示：CommunityToolkit <c>Toast</c>。</summary>
public sealed class MauiToastService : IToastService
{
    public async Task ShowAsync(string message, int durationMs = 1500)
    {
        var duration = durationMs > 2000 ? ToastDuration.Long : ToastDuration.Short;
        var toast = Toast.Make(message, duration);
        await toast.Show();
    }
}

/// <summary>剪贴板：MAUI <c>Clipboard.Default</c>。</summary>
public sealed class MauiClipboardService : IClipboardService
{
    public Task SetTextAsync(string text) => Clipboard.Default.SetTextAsync(text);
}

/// <summary>
/// 主题管理：MAUI <c>Application.Current.UserAppTheme</c>，持久化到 preferences.json（<c>app.theme</c>）。
/// 三态 System/Light/Dark。
/// </summary>
public sealed class MauiThemeManager : IThemeManager
{
    private const string ThemeKey = "app.theme";
    private readonly IAppSettings _appSettings;
    private ThemeMode _current = ThemeMode.System;

    public MauiThemeManager(IAppSettings appSettings) => _appSettings = appSettings;

    public ThemeMode CurrentTheme => _current;
    public event EventHandler<ThemeMode>? ThemeChanged;

    public void Initialize()
    {
        _current = ParseTheme(_appSettings.GetString(ThemeKey, "System"));
        Apply();
    }

    public async Task SetThemeAsync(ThemeMode themeMode)
    {
        _current = themeMode;
        _appSettings.SetString(ThemeKey, themeMode.ToString());
        Apply();
        ThemeChanged?.Invoke(this, themeMode);
        await Task.CompletedTask;
    }

    public Task<ThemeMode> GetSavedThemeAsync()
        => Task.FromResult(ParseTheme(_appSettings.GetString(ThemeKey, "System")));

    private void Apply()
    {
        if (Application.Current is not null)
        {
            Application.Current.UserAppTheme = _current switch
            {
                ThemeMode.Light => AppTheme.Light,
                ThemeMode.Dark => AppTheme.Dark,
                _ => AppTheme.Unspecified,
            };
        }
    }

    private static ThemeMode ParseTheme(string? s) => s switch
    {
        "Light" => ThemeMode.Light,
        "Dark" => ThemeMode.Dark,
        _ => ThemeMode.System,
    };
}

/// <summary>
/// 流式后台保活：iOS <c>BeginBackgroundTask</c>（约 30s）；Android 前台服务 Stage 2 接。
/// Stage 1 用 UIApplication 后台任务保流式不被挂起。
/// </summary>
public sealed class MauiStreamingBackgroundService : IStreamingBackgroundService
{
    private nint _bgTaskId = -1;
    public bool IsActive => _bgTaskId != -1;

    public Task<bool> StartAsync(string title, string message)
    {
#if IOS
        _bgTaskId = UIKit.UIApplication.SharedApplication.BeginBackgroundTask(new System.Action(() => { }));
#endif
        return Task.FromResult(true);
    }

    public void UpdateMessage(string message) { }

    public Task StopAsync()
    {
#if IOS
        if (_bgTaskId != -1)
        {
            UIKit.UIApplication.SharedApplication.EndBackgroundTask(_bgTaskId);
            _bgTaskId = -1;
        }
#endif
        return Task.CompletedTask;
    }

    public Task<bool> RequestPermissionAsync() => Task.FromResult(true);
}

/// <summary>语音识别 stub（Stage 2 接平台语音 API）。</summary>
public sealed class StubSpeechRecognitionService : ISpeechRecognitionService
{
    public bool IsListening => false;
    public Task<string?> ListenAsync(Action<string>? onPartialResult = null, CancellationToken cancellationToken = default)
        => Task.FromResult<string?>(null);
    public void StopListening() { }
    public Task<bool> RequestPermissionAsync() => Task.FromResult(false);
}
