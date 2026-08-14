using System.IO;
using System.Text.RegularExpressions;

namespace Acme.Shared.HostGlue;

/// <summary>
/// 从 <c>preferences.json</c> 读 <c>app.serverUrl</c>（UI 无关，桌面/手机共用）。
/// 启动期 DI 尚未构建，不能注 IPreferencesStore/IPreferencesStore，故直接读文件正则取值。
/// 合并自原桌面 <c>ReadSavedServerUrl</c> 与手机 <c>ReadSavedServerUrl</c>（函数体逐字节相同）。
/// </summary>
public static class PreferencesFileReader
{
    public static string? ReadServerUrl(string tenantDir)
    {
        try
        {
            var prefsFile = Path.Combine(tenantDir, "preferences.json");
            if (!File.Exists(prefsFile)) return null;
            var json = File.ReadAllText(prefsFile);
            var match = Regex.Match(json, @"""app\.serverUrl""\s*:\s*""([^""]+)""");
            return match.Success ? match.Groups[1].Value : null;
        }
        catch { return null; }
    }
}
