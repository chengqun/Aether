using Acme.Quotes.Domain;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Acme.Quotes.Infrastructure;

public class FailoverQuoteProvider : IFailoverQuoteProvider
{
    private readonly IEnumerable<IQuoteProvider> _providers;
    private readonly ILogger<FailoverQuoteProvider> _logger;

    /// <summary>
    /// 记录上一次失败的 provider，Key 为 provider 类型名
    /// </summary>
    private static readonly ConcurrentDictionary<string, DateTime> _failedProviders = new();

    public FailoverQuoteProvider(IEnumerable<IQuoteProvider> providers, ILogger<FailoverQuoteProvider> logger)
    {
        _providers = providers ?? throw new ArgumentNullException(nameof(providers));
        _logger = logger;
        _logger.LogInformation("Registered providers count: {ProviderCount}", providers.Count());
    }

    public async Task<List<Quote>> GetKlineAsync(string code, string freq, string type, string exchange)
    {
        _logger.LogInformation("GetKlineAsync - Code: {Code}, Freq: {Freq}, Type: {Type}, Exchange: {Exchange}", code, freq, type, exchange);

        var supportedProviders = _providers
            .Where(p => p.IsSupported(code, freq, type, exchange))
            .ToList();

        _logger.LogDebug("Supported providers count: {ProviderCount}", supportedProviders.Count);

        if (!supportedProviders.Any())
        {
            _logger.LogWarning("No supported providers found for Code: {Code}, Freq: {Freq}, Type: {Type}, Exchange: {Exchange}", code, freq, type, exchange);
            return new List<Quote>();
        }

        // 固定顺序（不随机）——保证同一输入多次取数据结果一致（回测确定性）
        var skippedProviders = new List<string>();

        foreach (var provider in supportedProviders)
        {
            var providerName = provider.GetType().Name;

            // 跳过上一次失败的 provider
            if (_failedProviders.ContainsKey(providerName))
            {
                skippedProviders.Add(providerName);
                _logger.LogDebug("Skipping provider {ProviderName} due to previous failure", providerName);
                continue;
            }

            try
            {
                _logger.LogDebug("Trying provider: {ProviderName}", providerName);
                var result = await provider.GetKlineAsync(code, freq, type, exchange);
                if (result != null && result.Count > 0)
                {
                    _logger.LogInformation("Provider {ProviderName} returned {QuoteCount} quotes", providerName, result.Count);

                    // 成功后清除失败记录
                    _failedProviders.TryRemove(providerName, out _);

                    return result;
                }
                else
                {
                    _logger.LogDebug("Provider {ProviderName} returned empty result", providerName);

                    // 返回空结果也标记为失败
                    _failedProviders[providerName] = DateTime.UtcNow;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Provider {ProviderName} threw exception: {Message}", providerName, ex.Message);

                // 异常也标记为失败
                _failedProviders[providerName] = DateTime.UtcNow;
            }
        }

        // 如果所有正常 provider 都失败了，但有跳过的 provider，重新尝试跳过的
        if (skippedProviders.Any() && !_failedProviders.IsEmpty)
        {
            _logger.LogInformation("All normal providers failed, retrying skipped providers: {SkippedProviders}", string.Join(", ", skippedProviders));

            foreach (var providerName in skippedProviders)
            {
                var provider = supportedProviders.FirstOrDefault(p => p.GetType().Name == providerName);
                if (provider == null) continue;

                try
                {
                    _logger.LogDebug("Retrying skipped provider: {ProviderName}", providerName);
                    var result = await provider.GetKlineAsync(code, freq, type, exchange);
                    if (result != null && result.Count > 0)
                    {
                        _logger.LogInformation("Retry provider {ProviderName} returned {QuoteCount} quotes", providerName, result.Count);
                        _failedProviders.TryRemove(providerName, out _);
                        return result;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Retry provider {ProviderName} threw exception: {Message}", providerName, ex.Message);
                }
            }
        }

        _logger.LogWarning("All providers failed for Code: {Code}, Freq: {Freq}, Type: {Type}, Exchange: {Exchange}, clearing failure records", code, freq, type, exchange);

        // 所有 provider 都失败了，清除失败记录，下次重新尝试
        _failedProviders.Clear();

        return new List<Quote>();
    }

    /// <summary>
    /// 清除所有失败记录
    /// </summary>
    public static void ClearAllFailures()
    {
        _failedProviders.Clear();
    }

    /// <summary>
    /// 清除指定 provider 的失败记录
    /// </summary>
    public static void ClearFailure(string providerName)
    {
        _failedProviders.TryRemove(providerName, out _);
    }
}
