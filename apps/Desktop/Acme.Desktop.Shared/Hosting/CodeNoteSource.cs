using Acme.Notes.Domain;
using Acme.Notes.Domain.Agents;
using Acme.Notes.Domain.Catalog;
using Aether.AI.Harness.Scripting;

namespace Acme.Desktop.Shared.Hosting;

/// <summary>
/// <see cref="ICodeNoteSource"/> 的 host 实现：桥接 Notes 上下文（经 <see cref="INoteCatalogSource"/>）→
/// Harness.Scripting。只返回 <see cref="NoteNodeType.CodeRule"/> 节点，让 Scripting 服务不直接依赖 Notes.Application。
/// 对称于 Stockapp 的 <c>StrategyNoteSource</c>——区别：策略笔记无 front-matter（纯 C#），代码规则笔记是
/// front-matter + C# 正文，故这里用 <see cref="ICodeSourceParser"/> 剥掉 front-matter，只把 C# 正文（SystemPrompt）
/// 交给编译器（否则 YAML 元数据行被当 C# 编译 → 一堆语法错）。经 INoteCatalogSource 读，可切目录源。
/// </summary>
public sealed class CodeNoteSource : ICodeNoteSource
{
    private readonly INoteCatalogSource _source;
    private readonly ICodeSourceParser _parser;
    public CodeNoteSource(INoteCatalogSource source, ICodeSourceParser parser)
    {
        _source = source;
        _parser = parser;
    }

    public async Task<IReadOnlyList<CodeNote>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var all = await _source.EnumerateAsync(NoteNodeType.CodeRule, cancellationToken);
        return all.Select(ToCodeNote).ToList();
    }

    /// <summary>把目录笔记转成 CodeNote。
    /// <para><see cref="CodeNote.Id"/> 须用 front-matter 的 <c>id:</c> Guid——它须与
    /// <see cref="Aether.AI.Core.Agents.AgentDefinition.Id"/> 一致，<see cref="Aether.AI.Harness.Agents.AgentFactory"/>
    /// 的规则 agent 构建才能经此键从 <c>ICodeBrainRegistry</c> 查到编译产物（否则一直 miss →「尚未编译」）。
    /// 解析失败或遗留文件无 <c>id:</c>（<see cref="Guid.Empty"/>）回退 <see cref="CatalogNote.ExternalId"/>（路径），
    /// 避免所有此类文件撞同一个空 Guid 键。</para>
    /// <para>正文取 front-matter 后的 C# 源文本；解析失败（如编辑中 front-matter 不完整）回退原文本，
    /// 让编译器报真实错误而非误导性的「YAML 当 C#」错误。</para></summary>
    private CodeNote ToCodeNote(CatalogNote n)
    {
        var content = n.Content ?? string.Empty;
        var id = n.ExternalId;
        if (_parser.TryParse(content, out var def, out _) && def is not null)
        {
            if (def.Id != Guid.Empty)
                id = def.Id.ToString();
            return new CodeNote(id, n.Name, def.SystemPrompt ?? string.Empty);
        }
        return new CodeNote(id, n.Name, content);
    }
}
