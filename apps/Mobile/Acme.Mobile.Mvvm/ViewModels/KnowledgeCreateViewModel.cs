using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Acme.Mobile.Mvvm.Events;
using Acme.Mobile.Mvvm.Services;
using Acme.Notes.Domain;
using Acme.Notes.Domain.Catalog;
using Acme.Notes.Domain.Knowledge;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Acme.Mobile.Mvvm.ViewModels;

/// <summary>
/// 知识对象新建/编辑 VM（AI 设置 → 知识对象）。字段 = kind + body + links（简单文本，每行 <c>role|target</c>），
/// 经 <see cref="IDocumentKnowledgeSourceParser.Serialize"/> 生成 YAML front-matter + markdown 正文，写
/// <c>labs/knowledge/&lt;name&gt;.md</c>。文档版 targetId 是相对路径，手机端新建时 links 用 target 字符串（相对路径）。
/// 编辑态经导航参数 <c>name</c> 加载已有 .md 回填，保存覆盖，可删除。
/// </summary>
public partial class KnowledgeCreateViewModel : NavigableViewModelBase
{
    private readonly Func<string?> _labsRoot;
    private readonly IDocumentKnowledgeSourceParser _parser;

    /// <summary>编辑态：原始知识对象名。null=新建。</summary>
    private string? _editingName;

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _kind = string.Empty;
    [ObservableProperty] private string _body = string.Empty;
    /// <summary>links 简单文本：每行 <c>role|target</c>（target 为相对路径，指向 knowledge/ 下另一 .md 文件）。</summary>
    [ObservableProperty] private string _linksText = string.Empty;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _statusMessage = string.Empty;

    /// <summary>是否编辑态（true=编辑已有知识对象，显示删除按钮；false=新建）。</summary>
    public bool IsEditing => _editingName != null;

    /// <summary>页标题（编辑/新建）。</summary>
    public string ScreenTitle => IsEditing ? "编辑知识对象" : "新建知识对象";

    public event EventHandler<NavigationRequestedEvent>? NavigationRequested;

    public KnowledgeCreateViewModel(Func<string?> labsRoot, IDocumentKnowledgeSourceParser parser)
    {
        _labsRoot = labsRoot ?? throw new ArgumentNullException(nameof(labsRoot));
        _parser = parser ?? throw new ArgumentNullException(nameof(parser));
    }

    /// <summary>接收导航参数：<c>name</c> 非空 = 编辑态（加载已有 .md 回填）。</summary>
    public void SetParameters(IDictionary<string, object> query)
    {
        if (query.TryGetValue("name", out var raw) && raw is string name && !string.IsNullOrWhiteSpace(name))
        {
            _editingName = name;
            LoadExisting();
        }
        OnPropertyChanged(nameof(IsEditing));
        OnPropertyChanged(nameof(ScreenTitle));
    }

    private void LoadExisting()
    {
        var root = _labsRoot();
        if (string.IsNullOrWhiteSpace(root) || _editingName == null) return;
        var file = Path.Combine(root, CatalogNoteDirectory.For(NoteNodeType.Knowledge).DirectoryName,
            LabsFileWriter.Sanitize(_editingName) + ".md");
        if (!File.Exists(file)) return;
        try
        {
            if (_parser.TryParse(File.ReadAllText(file), out var def, out _) && def != null)
            {
                Name = _editingName;
                Kind = def.Kind;
                Body = def.Body;
                LinksText = string.Join("\n", def.Links.Select(l => $"{l.Role}|{l.Target}"));
            }
        }
        catch { /* 读/解析失败 → 空白表单 */ }
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
            var checkError = await Task.Run<string?>(() =>
            {
                var root = _labsRoot();
                if (!string.IsNullOrWhiteSpace(root) && _editingName != null
                    && !string.Equals(LabsFileWriter.Sanitize(_editingName), LabsFileWriter.Sanitize(Name.Trim()), StringComparison.Ordinal))
                {
                    var oldFile = Path.Combine(root, CatalogNoteDirectory.For(NoteNodeType.Knowledge).DirectoryName,
                        LabsFileWriter.Sanitize(_editingName) + ".md");
                    try { if (File.Exists(oldFile)) File.Delete(oldFile); }
                    catch (Exception ex) { return $"删除旧知识对象失败: {ex.Message}"; }
                }
                LabsFileWriter.WriteAsync(root ?? string.Empty, NoteNodeType.Knowledge, Name.Trim(), source).GetAwaiter().GetResult();
                return null;
            });
            if (checkError != null) { StatusMessage = checkError; return; }
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

    /// <summary>删除（仅编辑态；删 knowledge/<name>.md）。</summary>
    [RelayCommand]
    private void Delete()
    {
        if (_editingName == null) return;
        var root = _labsRoot();
        if (string.IsNullOrWhiteSpace(root)) return;
        try
        {
            var file = Path.Combine(root, CatalogNoteDirectory.For(NoteNodeType.Knowledge).DirectoryName,
                LabsFileWriter.Sanitize(_editingName) + ".md");
            if (File.Exists(file)) File.Delete(file);
            StatusMessage = "已删除";
        }
        catch (Exception ex)
        {
            StatusMessage = $"删除失败: {ex.Message}";
            return;
        }
        NavigationRequested?.Invoke(this, NavigationRequestedEvent.Back());
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
