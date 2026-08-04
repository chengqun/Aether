namespace Acme.Securities.Domain;

/// <summary>
/// 同步日志端口（Domain 纯接口，Infrastructure 适配 Microsoft.Extensions.Logging 实现）。
/// 让 Application 层通过端口获取日志能力，不直接依赖 Logging.Abstractions 基础设施包。
/// </summary>
public interface ISyncLogger
{
    void LogDebug(string message, params object[] args);
    void LogInformation(string message, params object[] args);
    void LogWarning(string message, params object[] args);
    void LogError(Exception? exception, string message, params object[] args);
}
