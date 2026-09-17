namespace Acme.Desktop.Shared.Hosting;

/// <summary>
/// 桌面租户数据目录路径（单一来源）。原逻辑散在两租户 App.ConfigureServices 里各写一份，
/// Program.cs 的崩溃遥测初始化（DI 之前）也要用同一目录——抽出来去重。
/// Mac 用 ~/Library/Application Support（保数据连续），Windows/Linux 用 %LOCALAPPDATA%（系统标准应用数据目录）。
/// 绝对路径：.app 双击启动 CWD=/ 不可写，不能依赖相对路径。
/// </summary>
public static class TenantDataPaths
{
    public static string GetTenantDir(string tenant)
        => OperatingSystem.IsMacOS()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library", "Application Support", "Aether", $"Desktop-{tenant}")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Aether", $"Desktop-{tenant}");
}
