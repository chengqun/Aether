using System.Collections.Generic;

namespace Acme.ApkParser.AI.Tools;

/// <summary>
/// APK 解析结果。app_name=应用名（已解析 @string 引用），app_code=包名（package），
/// 并附带安全相关字段：权限清单、可导出组件（攻击面）、debuggable、签名信息。
/// </summary>
public sealed class ApkInfo
{
    /// <summary>应用名（android:label，已从 @string/app_name 引用解析为真实文本）。</summary>
    public string? AppName { get; set; }

    /// <summary>包名（manifest 的 package 属性，即 app_code）。</summary>
    public string? AppCode { get; set; }

    public string? VersionName { get; set; }

    /// <summary>版本号（android:versionCode，库返回字符串）。</summary>
    public string? VersionCode { get; set; }

    /// <summary>Application 类名（android:name，如 crc6477...MainApplication）。</summary>
    public string? ApplicationName { get; set; }

    /// <summary>是否可调试（android:debuggable，发布包应为 false）。</summary>
    public bool? Debuggable { get; set; }

    /// <summary>申请的权限清单（uses-permission）。</summary>
    public List<string> Permissions { get; set; } = new();

    /// <summary>组件统计（含可导出组件数 = 攻击面）。</summary>
    public ApkComponentsInfo Components { get; set; } = new();

    /// <summary>签名信息（V1 JAR / V2+ APK Signature Block）。</summary>
    public ApkSignatureInfo Signature { get; set; } = new();
}

/// <summary>四大组件总数与可导出数。可导出组件 = 潜在攻击面。</summary>
public sealed class ApkComponentsInfo
{
    public int Activities { get; set; }
    public int Receivers { get; set; }
    public int Services { get; set; }
    public int Providers { get; set; }
    public int ExportedActivities { get; set; }
    public int ExportedReceivers { get; set; }
    public int ExportedServices { get; set; }
    public int ExportedProviders { get; set; }

    /// <summary>可导出组件总数。</summary>
    public int ExportedTotal => ExportedActivities + ExportedReceivers + ExportedServices + ExportedProviders;
}

/// <summary>
/// 签名信息。V1=JAR 签名（MANIFEST.MF 文件哈希，库可可靠解析）。
/// 注意：库对 V2/V3 APK Signature Block 解析偏弱（README 自述 needs broader testing），
/// 故仅暴露 V1；现代 APK 通常还有 V2/V3 签名，但本工具不报告。
/// </summary>
public sealed class ApkSignatureInfo
{
    public bool HasV1 { get; set; }
    /// <summary>V1 JAR manifest 覆盖的文件数（完整性校验覆盖面）。</summary>
    public int V1FileCount { get; set; }
    public string? Note { get; set; }
}
