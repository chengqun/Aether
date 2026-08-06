using System.Collections.Generic;

namespace Acme.AppAutomation.Domain;

/// <summary>元素定位方式。优先 Name/Role/AutomationId（结构化，稳）；坐标兜底（脆）。</summary>
public enum LocatorKind { Name, Role, AutomationId, Coordinates }

/// <summary>桌面元素定位器。构造用静态工厂：<see cref="ByName"/>/ByRole/ByAutomationId/ByCoordinates。</summary>
public sealed class DesktopLocator
{
    public LocatorKind Kind { get; init; } = LocatorKind.Name;
    public string Value { get; init; } = "";

    public static DesktopLocator ByName(string name) => new() { Kind = LocatorKind.Name, Value = name };
    public static DesktopLocator ByRole(string role) => new() { Kind = LocatorKind.Role, Value = role };
    public static DesktopLocator ByAutomationId(string id) => new() { Kind = LocatorKind.AutomationId, Value = id };
    public static DesktopLocator ByCoordinates(int x, int y) => new() { Kind = LocatorKind.Coordinates, Value = $"{x},{y}" };

    /// <summary>转成 MCP 工具调用参数（locator_kind + locator_value）。</summary>
    public Dictionary<string, object?> ToArgs() => new()
    {
        ["locator_kind"] = Kind.ToString().ToLowerInvariant(),
        ["locator_value"] = Value,
    };

    public override string ToString() => $"{Kind}={Value}";
}

/// <summary>找到的桌面元素（只取固定过程需要的字段）。</summary>
public sealed class DesktopElement
{
    public string? Id { get; init; }
    public string? Name { get; init; }
    public string? Role { get; init; }
    public string? Text { get; init; }
}
