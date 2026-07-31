using System.ComponentModel;
using Acme.Notes.Application;

namespace Acme.Notes.AI.Tools;

public class NoteTool
{
    private readonly INoteService _service;

    public NoteTool(INoteService service)
    {
        _service = service;
    }

    public class CreateOutput
    {
        public string? Id { get; set; }
        public string? Title { get; set; }
        public string? Error { get; set; }
    }

    public class ReadOutput
    {
        public string? Id { get; set; }
        public string? Title { get; set; }
        public string? Content { get; set; }
        public string? Syntax { get; set; }
        public string? Tags { get; set; }
        public bool IsReadOnly { get; set; }
        public string? Error { get; set; }
    }

    public class ActionOutput
    {
        public bool Success { get; set; }
        public string? Id { get; set; }
        public string? Title { get; set; }
        public string? Error { get; set; }
    }

    public class ListOutput
    {
        public List<NoteSummary> Notes { get; set; } = new();
        public string? Error { get; set; }
    }

    public class NoteSummary
    {
        public string? Id { get; set; }
        public string? Title { get; set; }
        public string? Syntax { get; set; }
        public string? Tags { get; set; }
        public string? Preview { get; set; }
    }

    [Description("新建一条本地 Markdown 笔记。parentTitle 为空时挂到根节点。")]
    public async Task<CreateOutput> CreateNote(
        [Description("笔记标题")] string title,
        [Description("笔记正文（Markdown）")] string content,
        [Description("父笔记标题（留空挂到根节点）")] string? parentTitle = null,
        [Description("语法/语言标记，留空=Markdown；也可传 sql/python/json 等建代码笔记")] string? syntax = null,
        [Description("标签，逗号分隔")] string? tags = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(title))
                return new CreateOutput { Error = "标题不能为空" };

            string? parentId = null;
            if (!string.IsNullOrWhiteSpace(parentTitle))
            {
                var all = await _service.GetAllAsync(cancellationToken);
                var parent = all.FirstOrDefault(n => n.Title == parentTitle);
                if (parent == null)
                    return new CreateOutput { Error = $"找不到父笔记: {parentTitle}" };
                parentId = parent.Id;
            }

            var request = new CreateNoteRequest
            {
                Title = title,
                Content = content ?? string.Empty,
                ParentId = parentId,
                Syntax = string.IsNullOrWhiteSpace(syntax) ? "markdown" : syntax,
                Tags = string.IsNullOrWhiteSpace(tags) ? null : tags,
            };
            var created = await _service.CreateAsync(request, cancellationToken);
            return new CreateOutput { Id = created.Id, Title = created.Title };
        }
        catch (Exception ex)
        {
            return new CreateOutput { Error = $"创建笔记失败: {ex.Message}" };
        }
    }

    [Description("按标题读取一条笔记的完整内容。")]
    public async Task<ReadOutput> ReadNote(
        [Description("笔记标题")] string title,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var all = await _service.GetAllAsync(cancellationToken);
            var note = all.FirstOrDefault(n => n.Title == title);
            if (note == null)
                return new ReadOutput { Error = $"找不到笔记: {title}" };

            return new ReadOutput
            {
                Id = note.Id,
                Title = note.Title,
                Content = note.Content,
                Syntax = note.Syntax,
                Tags = note.Tags,
                IsReadOnly = note.IsReadOnly,
            };
        }
        catch (Exception ex)
        {
            return new ReadOutput { Error = $"读取笔记失败: {ex.Message}" };
        }
    }

    [Description("向已有笔记末尾追加内容。")]
    public async Task<ActionOutput> AppendNote(
        [Description("笔记标题")] string title,
        [Description("要追加的文本（Markdown）")] string text,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var all = await _service.GetAllAsync(cancellationToken);
            var note = all.FirstOrDefault(n => n.Title == title);
            if (note == null)
                return new ActionOutput { Error = $"找不到笔记: {title}" };
            if (note.IsReadOnly)
                return new ActionOutput { Error = $"笔记只读: {title}" };

            var newContent = string.IsNullOrEmpty(note.Content) ? text : note.Content + "\n\n" + text;
            var request = new UpdateNoteRequest
            {
                Id = note.Id,
                Title = note.Title,
                Content = newContent,
                Syntax = note.Syntax,
                Tags = note.Tags,
            };
            await _service.UpdateAsync(request, cancellationToken);
            return new ActionOutput { Success = true, Id = note.Id, Title = note.Title };
        }
        catch (Exception ex)
        {
            return new ActionOutput { Error = $"追加笔记失败: {ex.Message}" };
        }
    }

    [Description("整篇替换一条笔记的内容。只读笔记不可更新。")]
    public async Task<ActionOutput> UpdateNote(
        [Description("笔记标题")] string title,
        [Description("新的正文内容（Markdown）")] string content,
        [Description("代码语言标记，如 sql（留空不变）")] string? syntax = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var all = await _service.GetAllAsync(cancellationToken);
            var note = all.FirstOrDefault(n => n.Title == title);
            if (note == null)
                return new ActionOutput { Error = $"找不到笔记: {title}" };
            if (note.IsReadOnly)
                return new ActionOutput { Error = $"笔记只读: {title}" };

            var request = new UpdateNoteRequest
            {
                Id = note.Id,
                Title = note.Title,
                Content = content ?? string.Empty,
                Syntax = syntax ?? note.Syntax,
                Tags = note.Tags,
            };
            await _service.UpdateAsync(request, cancellationToken);
            return new ActionOutput { Success = true, Id = note.Id, Title = note.Title };
        }
        catch (Exception ex)
        {
            return new ActionOutput { Error = $"更新笔记失败: {ex.Message}" };
        }
    }

    [Description("跨标题/正文/标签搜索笔记，返回命中列表（含正文预览）。")]
    public async Task<ListOutput> SearchNotes(
        [Description("搜索关键词")] string query,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var hits = await _service.SearchAsync(query, cancellationToken);
            return new ListOutput { Notes = hits.Select(ToSummary).ToList() };
        }
        catch (Exception ex)
        {
            return new ListOutput { Error = $"搜索笔记失败: {ex.Message}" };
        }
    }

    [Description("列出笔记。parentTitle 留空列出根节点，否则列出指定父笔记的直接子节点。")]
    public async Task<ListOutput> ListNotes(
        [Description("父笔记标题（留空列根节点）")] string? parentTitle = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            string? parentId = null;
            if (!string.IsNullOrWhiteSpace(parentTitle))
            {
                var all = await _service.GetAllAsync(cancellationToken);
                var parent = all.FirstOrDefault(n => n.Title == parentTitle);
                if (parent == null)
                    return new ListOutput { Error = $"找不到父笔记: {parentTitle}" };
                parentId = parent.Id;
            }

            var children = await _service.GetChildrenAsync(parentId, cancellationToken);
            return new ListOutput { Notes = children.Select(ToSummary).ToList() };
        }
        catch (Exception ex)
        {
            return new ListOutput { Error = $"列出笔记失败: {ex.Message}" };
        }
    }

    [Description("按标题删除一条笔记。其子节点会提升为根节点（不会被一起删掉）。")]
    public async Task<ActionOutput> DeleteNote(
        [Description("笔记标题")] string title,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var all = await _service.GetAllAsync(cancellationToken);
            var note = all.FirstOrDefault(n => n.Title == title);
            if (note == null)
                return new ActionOutput { Error = $"找不到笔记: {title}" };

            await _service.DeleteAsync(note.Id, cancellationToken);
            return new ActionOutput { Success = true, Id = note.Id, Title = note.Title };
        }
        catch (Exception ex)
        {
            return new ActionOutput { Error = $"删除笔记失败: {ex.Message}" };
        }
    }

    private static NoteSummary ToSummary(NoteDto n) => new()
    {
        Id = n.Id,
        Title = n.Title,
        Syntax = n.Syntax,
        Tags = n.Tags,
        Preview = n.Content.Length > 80 ? n.Content[..80] + "…" : n.Content,
    };
}