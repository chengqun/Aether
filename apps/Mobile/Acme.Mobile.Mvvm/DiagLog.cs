using System;
using System.IO;

namespace Acme.Mobile.Mvvm;

/// <summary>
/// 临时诊断日志（写到 AppData 目录的 diag.log，便于模拟器沙盒读取排查运行时数据流）。
/// 排查完毕后可删。
/// </summary>
public static class DiagLog
{
    private static readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) ?? "/tmp",
        "diag.log");

    public static void Log(string msg)
    {
        try { File.AppendAllText(_path, $"{DateTime.UtcNow:HH:mm:ss.fff}Z {msg}\n"); }
        catch { /* 诊断日志不得影响业务 */ }
    }
}
