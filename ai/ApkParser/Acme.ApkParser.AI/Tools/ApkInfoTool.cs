using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Acme.ApkParser.AI;

/// <summary>
/// APK 信息工具（接入 Agent 工具池，类别"APK 工具"）。让动态 Agent 能解析一个 .apk 文件，
/// 提取 app_name（应用名）与 app_code（包名），并附带安全相关字段：权限清单、可导出组件攻击面、
/// debuggable、签名信息。底层用 AlphaOmega.ApkReader 库（解析 AXML + resources.arsc + DEX + 签名）。
/// </summary>
public sealed class ApkInfoTool
{
    private readonly ApkInfoExtractor _extractor;

    public ApkInfoTool(ApkInfoExtractor extractor)
    {
        _extractor = extractor;
    }

    /// <summary>解析 APK 返回结果</summary>
    public class GetApkInfoOutput
    {
        public bool IsSuccess { get; set; }
        /// <summary>应用名（已解析 @string/app_name 引用为真实文本）。</summary>
        public string? AppName { get; set; }
        /// <summary>包名（app_code）。</summary>
        public string? AppCode { get; set; }
        public string? VersionName { get; set; }
        public string? VersionCode { get; set; }
        /// <summary>Application 类名。</summary>
        public string? ApplicationName { get; set; }
        /// <summary>是否可调试（发布包应为 false）。</summary>
        public bool? Debuggable { get; set; }
        /// <summary>申请的权限清单。</summary>
        public List<string> Permissions { get; set; } = new();
        /// <summary>组件统计（含可导出组件数 = 攻击面）。</summary>
        public ApkComponentsInfo? Components { get; set; }
        /// <summary>签名信息。</summary>
        public ApkSignatureInfo? Signature { get; set; }
        public string? Error { get; set; }
    }

    /// <summary>
    /// 解析后缀为 .apk 的文件，提取 app_name（应用名）与 app_code（包名），并附带权限/可导出组件/debuggable/签名等安全相关信息。
    /// </summary>
    /// <param name="apkPath">APK 文件的本地绝对路径</param>
    /// <param name="cancellationToken">取消令牌</param>
    public async Task<GetApkInfoOutput> GetApkInfo(
        [Description("APK 文件的本地绝对路径，如 /Users/xxx/Downloads/app-Signed.apk")] string apkPath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apkPath))
            return new GetApkInfoOutput { Error = "apkPath 不能为空" };
        if (!File.Exists(apkPath))
            return new GetApkInfoOutput { Error = $"APK 文件不存在: {apkPath}" };

        try
        {
            var info = await Task.Run(() => _extractor.Extract(apkPath), cancellationToken);
            return new GetApkInfoOutput
            {
                IsSuccess = true,
                AppName = info.AppName,
                AppCode = info.AppCode,
                VersionName = info.VersionName,
                VersionCode = info.VersionCode,
                ApplicationName = info.ApplicationName,
                Debuggable = info.Debuggable,
                Permissions = info.Permissions,
                Components = info.Components,
                Signature = info.Signature,
            };
        }
        catch (System.Exception ex)
        {
            return new GetApkInfoOutput { Error = $"解析 APK 失败: {ex.Message}" };
        }
    }
}
