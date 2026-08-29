using System;
using Acme.Notes.Domain;

namespace Acme.Mobile.Mvvm.Models;

/// <summary>labs 目录下的一个分组（子目录：agents/rules/workflows/knowledge/skills/mcp-servers 等）。</summary>
public class LabsSection
{
    /// <summary>目录名（如 "agents"）。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>显示标签（中文，如 "智能体"）。</summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>对应 NoteNodeType（用于图标/后续编辑分发）。</summary>
    public NoteNodeType NodeType { get; init; }

    /// <summary>该目录下的文件列表。</summary>
    public System.Collections.ObjectModel.ObservableCollection<LabsFileItem> Files { get; } = new();

    /// <summary>是否无文件（空目录不显示空壳）。</summary>
    public bool HasFiles => Files.Count > 0;
}

/// <summary>labs 目录下的一个文件（相对 labs 根的路径 + 类型）。</summary>
public class LabsFileItem
{
    /// <summary>相对 labs 根的路径（如 "agents/foo.agent.md"）。</summary>
    public string RelativePath { get; init; } = string.Empty;

    /// <summary>显示名称（文件名 stem，去目录 + 去扩展名）。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>文件扩展名（含点，如 ".agent.md"）。</summary>
    public string Extension { get; init; } = string.Empty;

    /// <summary>绝对路径（读取内容用）。</summary>
    public string FullPath { get; init; } = string.Empty;
}
