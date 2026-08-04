using System;
using Acme.Securities.Domain;
using Microsoft.Extensions.Logging;

namespace Acme.Securities.Infrastructure;

/// <summary>ISyncLogger 的 Infrastructure 实现，适配 Microsoft.Extensions.Logging.ILogger。</summary>
public sealed class SyncLogger : ISyncLogger
{
    private readonly ILogger<SyncLogger> _logger;
    public SyncLogger(ILogger<SyncLogger> logger) => _logger = logger;

    public void LogDebug(string message, params object[] args) => _logger.LogDebug(message, args);
    public void LogInformation(string message, params object[] args) => _logger.LogInformation(message, args);
    public void LogWarning(string message, params object[] args) => _logger.LogWarning(message, args);
    public void LogError(Exception? exception, string message, params object[] args) => _logger.LogError(exception, message, args);
}
