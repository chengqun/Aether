using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Acme.Notes.Domain.Catalog;

/// <summary>
/// 笔记单元来源端口：note 节点（SQLite，<c>SqliteNoteCatalogSource</c>）与 labs 文件（磁盘，
/// <c>DirectoryNoteCatalogSource</c>）两个实现的统一出口。消费方依赖此端口而非 <see cref="INoteService"/>，
/// 实现双源兼容——DI 里切注册即回滚，不触碰批量消费逻辑。
/// 镜像 <see cref="IKnowledgeSourceParser"/>/<see cref="IDocumentKnowledgeSourceParser"/> 的「note 版/文档版并列」
/// 范式，但把差异从「两套不兼容解析器」收敛到「同一端口、两个来源」。
/// </summary>
public interface INoteCatalogSource
{
    /// <summary>数据源根目录绝对路径（目录版=labs 根；SQLite 源无文件根→null）。写入端据此落盘。</summary>
    string? Root { get; }

    /// <summary>枚举某类型的全部单元；<paramref name="nodeType"/> 为 null 时返回全部类型。</summary>
    Task<IReadOnlyList<CatalogNote>> EnumerateAsync(NoteNodeType? nodeType = null, CancellationToken ct = default);

    /// <summary>按 <see cref="CatalogNote.ExternalId"/> 取单个单元；不存在返回 null。目录版按相对路径。</summary>
    Task<CatalogNote?> FindByExternalIdAsync(string externalId, CancellationToken ct = default);

    /// <summary>源变更时触发（SQLite 源转发 <c>INoteService.NotesChanged</c>；目录源由宿主 watcher 驱动）。</summary>
    event Action? Changed;
}
