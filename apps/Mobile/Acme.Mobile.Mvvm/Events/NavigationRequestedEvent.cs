using System;
using System.Collections.Generic;

namespace Acme.Mobile.Mvvm.Events;

/// <summary>
/// 导航请求事件，用于 ViewModel 请求页面导航
/// 平台层（MAUI/Avalonia）订阅此事件并执行实际导航
/// </summary>
public class NavigationRequestedEvent : EventArgs
{
    /// <summary>
    /// 目标页面名称
    /// </summary>
    public string TargetPage { get; set; } = string.Empty;

    /// <summary>
    /// 导航参数
    /// </summary>
    public Dictionary<string, object> Parameters { get; set; } = new();

    /// <summary>
    /// 是否使用绝对路径导航（如 "//ChatPage"）
    /// </summary>
    public bool IsAbsolute { get; set; }

    /// <summary>
    /// 是否返回上一页（使用 ".."）
    /// </summary>
    public bool IsBack { get; set; }

    /// <summary>
    /// 创建简单导航请求
    /// </summary>
    public static NavigationRequestedEvent To(string targetPage, bool isAbsolute = false)
    {
        return new NavigationRequestedEvent
        {
            TargetPage = targetPage,
            IsAbsolute = isAbsolute
        };
    }

    /// <summary>
    /// 创建带参数的导航请求
    /// </summary>
    public static NavigationRequestedEvent To(string targetPage, Dictionary<string, object> parameters)
    {
        return new NavigationRequestedEvent
        {
            TargetPage = targetPage,
            Parameters = parameters
        };
    }

    /// <summary>
    /// 创建返回上一页的导航请求
    /// </summary>
    public static NavigationRequestedEvent Back()
    {
        return new NavigationRequestedEvent
        {
            IsBack = true
        };
    }

    /// <summary>
    /// 构建导航 URI（供 MAUI Shell 使用）
    /// </summary>
    public string BuildNavigationUri()
    {
        if (IsBack)
            return "..";

        var uri = IsAbsolute ? $"//{TargetPage}" : TargetPage;

        if (Parameters.Count > 0)
        {
            var queryParts = new List<string>();
            foreach (var kvp in Parameters)
            {
                var value = kvp.Value?.ToString() ?? string.Empty;
                queryParts.Add($"{kvp.Key}={Uri.EscapeDataString(value)}");
            }
            uri += "?" + string.Join("&", queryParts);
        }

        return uri;
    }
}