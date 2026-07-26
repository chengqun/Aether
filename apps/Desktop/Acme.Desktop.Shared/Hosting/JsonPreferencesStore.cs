using System.Collections.Concurrent;
using System.Text.Json;

namespace Acme.Desktop.Shared.Hosting;

/// <summary>
/// <see cref="IPreferencesStore"/> 的 JSON 文件实现：偏好落 <c>preferences.json</c> 于给定目录。
/// 单用户、低频写、无事务需求，用文件比上 SQLite 更轻。内存缓存 + 懒加载 + 写时整体覆盖。
/// </summary>
public sealed class JsonPreferencesStore : IPreferencesStore
{
    private readonly string _filePath;
    private readonly object _gate = new();
    private ConcurrentDictionary<string, string?>? _cache;
    private int _init;

    public JsonPreferencesStore(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) throw new ArgumentException("required", nameof(directory));
        Directory.CreateDirectory(directory);
        _filePath = Path.Combine(directory, "preferences.json");
    }

    public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        var values = EnsureLoaded();
        return Task.FromResult(values.TryGetValue(key, out var v) ? v : null);
    }

    public async Task SetAsync(string key, string? value, CancellationToken cancellationToken = default)
    {
        var values = EnsureLoaded();
        values[key] = value;
        await PersistAsync(cancellationToken);
    }

    private ConcurrentDictionary<string, string?> EnsureLoaded()
    {
        if (_cache is not null) return _cache;
        lock (_gate)
        {
            if (_cache is not null) return _cache;
            if (Interlocked.CompareExchange(ref _init, 1, 0) != 0) return _cache!;
            var dict = new ConcurrentDictionary<string, string?>();
            try
            {
                if (File.Exists(_filePath))
                {
                    var json = File.ReadAllText(_filePath);
                    var doc = JsonSerializer.Deserialize<Dictionary<string, string?>>(json);
                    if (doc is not null)
                        foreach (var kv in doc) dict[kv.Key] = kv.Value;
                }
            }
            catch { /* 损坏则视为空 */ }
            _cache = dict;
            return _cache;
        }
    }

    private async Task PersistAsync(CancellationToken ct)
    {
        if (_cache is null) return;
        var json = JsonSerializer.Serialize(_cache, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(_filePath, json, ct);
    }
}
