namespace Aether.Server.Domain.Shared.Enums;

/// <summary>
/// 崩溃记录严重级别（客户端镜像枚举值须对齐：Fatal=0 / NonFatal=1）。
/// </summary>
public enum CrashSeverity
{
    /// <summary>致命：未处理异常 / 非干净关机（进程崩溃或被强杀）。</summary>
    Fatal = 0,

    /// <summary>非致命：SafeFireAndForget 捕获的后台异常 / 关窗路径被吞的异常等，进程未死。</summary>
    NonFatal = 1
}
