using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using AlphaOmega.Debug;
using AlphaOmega.Debug.Manifest;

namespace Acme.ApkParser.AI;

/// <summary>
/// APK 信息提取器：基于 <see cref="ApkFile"/>（AlphaOmega.ApkReader 库）解析 APK，
/// 提取 app_name（应用名，自动解析 @string 引用）+ app_code（包名），并附带安全相关字段
/// （权限清单、可导出组件攻击面、debuggable、签名信息）。
/// 解析分节防御：单节失败不影响其它节，返回部分结果。
/// </summary>
public sealed class ApkInfoExtractor
{
    /// <summary>从 APK 文件路径提取应用信息。</summary>
    public ApkInfo Extract(string apkPath)
    {
        using var apk = new ApkFile(apkPath);
        var info = new ApkInfo();

        var manifest = apk.AndroidManifest;
        if (manifest != null)
        {
            info.AppCode = manifest.Package;
            info.VersionName = manifest.VersionName;
            info.VersionCode = manifest.VersionCode;

            var app = manifest.Application;
            if (app != null)
            {
                info.AppName = SafeString(() => app.Label);
                info.ApplicationName = SafeString(() => app.Name);
                info.Debuggable = SafeBool(() => app.Debuggable);
                info.Components = CountComponents(app);
            }
        }

        // apk.UsesPermission 直接返回 IEnumerable<string>（权限名）
        info.Permissions = (apk.UsesPermission ?? Enumerable.Empty<string>())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!)
            .ToList();

        info.Signature = ExtractSignature(apk.Signatures);

        return info;
    }

    private static ApkComponentsInfo CountComponents(ApkApplication app)
    {
        var c = new ApkComponentsInfo();
        (c.Activities, c.ExportedActivities) = CountExported(app.Activity);
        (c.Receivers, c.ExportedReceivers) = CountExported(app.Reciever); // 库拼写为 Reciever
        (c.Services, c.ExportedServices) = CountExported(app.Service);
        (c.Providers, c.ExportedProviders) = CountExported(app.Provider);
        return c;
    }

    /// <summary>统计组件总数与可导出（Exported==true）数。Exported 在不同组件类型上为 bool 或 Nullable&lt;bool&gt;。</summary>
    private static (int total, int exported) CountExported<T>(IEnumerable<T>? items)
    {
        if (items == null) return (0, 0);
        int total = 0, exported = 0;
        foreach (var item in items)
        {
            total++;
            if (item == null) continue;
            var exp = item.GetType().GetProperty("Exported")?.GetValue(item);
            // Exported 在不同组件类型上为 bool 或 Nullable<bool>；Nullable<bool> 装箱后为 bool 或 null，
            // 故一个 bool 分支即可覆盖两种类型。
            bool isExported = exp switch
            {
                bool b => b,
                _ => false,
            };
            if (isExported) exported++;
        }
        return (total, exported);
    }

    private static ApkSignatureInfo ExtractSignature(ApkSignature? sig)
    {
        var s = new ApkSignatureInfo();
        if (sig == null) return s;
        // 不报告 ApkSignature.IsValid：库的签名验证未完整实现，对正常签名 APK 也会返回 false，易误导。
        s.HasV1 = sig.V1SchemeBlock?.Manifest != null;
        s.V1FileCount = CountEnumerable(sig.V1SchemeBlock?.Manifest);
        s.Note = "仅解析 V1 JAR 签名；库对 V2/V3 解析偏弱，现代 APK 的 V2/V3 签名不报告，且不做加密校验";
        return s;
    }

    private static int CountEnumerable(object? obj)
    {
        if (obj is IEnumerable e and not string)
        {
            int n = 0;
            foreach (var _ in e) n++;
            return n;
        }
        return 0;
    }

    private static string? SafeString(Func<string?> f) { try { return f(); } catch { return null; } }
    private static bool? SafeBool(Func<bool> f) { try { return f(); } catch { return null; } }
}
