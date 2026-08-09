using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Acme.Mobile.Mvvm.Models;

/// <summary>
/// 工具开关项，用于显示 Agent 工具的启用/禁用状态。
/// </summary>
public partial class ToolToggleItem : ObservableObject
{
    [ObservableProperty]
    private bool _isEnabled;

    public string Name { get; }
    public string Description { get; }
    public string Category { get; }

    public event Action<ToolToggleItem>? IsEnabledChanged;

    public ToolToggleItem(string name, string description, string category, bool isEnabled = true)
    {
        Name = name;
        Description = description;
        Category = category;
        IsEnabled = isEnabled;
    }

    partial void OnIsEnabledChanged(bool value)
    {
        IsEnabledChanged?.Invoke(this);
    }
}

/// <summary>
/// 工作流步骤展示项（纯显示，无开关）。
/// </summary>
public class WorkflowStepItem
{
    /// <summary>步骤序号（从 1 开始）</summary>
    public int StepIndex { get; }

    /// <summary>子 Agent 显示名称（如 "技术分析专家"）</summary>
    public string Name { get; }

    /// <summary>子 Agent 描述</summary>
    public string Description { get; }

    /// <summary>子 Agent 使用的工具列表（如 "CalculateIndicators: 计算股票的技术指标"）</summary>
    public List<string> Tools { get; }

    /// <summary>是否有工具</summary>
    public bool HasTools => Tools.Count > 0;

    public WorkflowStepItem(int stepIndex, string name, string description, List<string> tools)
    {
        StepIndex = stepIndex;
        Name = name;
        Description = description;
        Tools = tools;
    }
}