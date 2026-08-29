using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Acme.Mobile.Mvvm.Events;
using Acme.Mobile.Mvvm.Services;
using Acme.Notes.Domain.Knowledge;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Acme.Mobile.Mvvm.ViewModels;

/// <summary>
/// 知识对象新建 VM（AI 设置 → 新建 知识对象）。字段 = kind + body + links（简单文本，每行 <c>role|target</c>），
/// 经 <see cref="IDocumentKnowledgeSourceParser.Serialize"/> 生成 YAML front-matter + markdown 正文，写
/// <c>labs/knowledge/&lt;name&gt;.md</c>。文档版 targetId 是相对路径，手机端新建时 links 用 target 字符串（相对路径）。
/// </summary>
public partial class KnowledgeCreateViewModel : NavigableViewModelBase
{
    private readonly Func<string?> _labsRoot;
    private readonly IDocumentKnowledgeSourceParser _parser;

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _kind = string.Empty;
    [ObservableProperty] private string _body = string.Empty;
    /// <summary>links 简单文本：每行 <c>role|target</c>（target 为相对路径，指向 knowledge/ 下另一 .md 文件）。</summary>
    [ObservableProperty] private string _linksText = string.Empty;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _statusMessage = string.Empty;

    public event EventHandler<NavigationRequestedEvent>? NavigationRequested;

    public KnowledgeCreateViewModel(Func<string?> labsRoot, IDocumentKnowledgeSourceParser parser)
    {
        _labsRoot = labsRoot ?? throw new ArgumentNullException(nameof(labsRoot));
        _parser = parser ?? throw new ArgumentNullException(nameof(parser));
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            StatusMessage = "请填写名称";
            return;
        }
        IsLoading = true;
        try
        {
            var def = new KnowledgeObjectDefinition
            {
                Kind = Kind?.Trim() ?? string.Empty,
                Body = Body ?? string.Empty,
                Links = ParseLinks(LinksText),
            };
            var source = _parser.Serialize(def);
            await Task.Run(() => LabsFileWriter.WriteAsync(
                _labsRoot() ?? string.Empty, Acme.Notes.Domain.NoteNodeType.Knowledge, Name.Trim(), source));
            NavigationRequested?.Invoke(this, NavigationRequestedEvent.Back());
        }
        catch (Exception ex)
        {
            StatusMessage = $"保存失败: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private static List<KnowledgeLink> ParseLinks(string? text)
    {
        var result = new List<KnowledgeLink>();
        if (string.IsNullOrWhiteSpace(text)) return result;
        foreach (var line in text.Split('\n'))
        {
            var t = line.Trim();
            if (t.Length == 0) continue;
            var idx = t.IndexOf('|');
            var role = idx >= 0 ? t[..idx].Trim() : "uses";
            var target = idx >= 0 ? t[(idx + 1)..].Trim() : t;
            if (target.Length == 0) continue;
            result.Add(new KnowledgeLink { Role = role, Target = target });
        }
        return result;
    }

    [RelayCommand]
    private void Cancel() => NavigationRequested?.Invoke(this, NavigationRequestedEvent.Back());
}
