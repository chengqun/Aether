using Aether.Server.Application.Contracts.DTOs.AgentConfig;
using Aether.Server.Domain.Entities;
using Volo.Abp.Mapperly;

namespace Aether.Server.Application.Mappings;

/// <summary>
/// CreateDto → AppNoteNode 映射（CrudAppService CreateAsync 用）。
/// </summary>
public class NoteNodeCreateDtoToNoteNodeMapper : MapperBase<NoteNodeCreateDto, AppNoteNode>
{
    public override AppNoteNode Map(NoteNodeCreateDto source)
    {
        return new AppNoteNode(
            source.Title ?? string.Empty,
            source.Content ?? string.Empty,
            string.IsNullOrEmpty(source.NodeType) ? "Agent" : source.NodeType)
        {
            IsPublished = source.IsPublished,
            Description = source.Description ?? string.Empty,
            Category = string.IsNullOrEmpty(source.Category) ? "普通对话" : source.Category,
            MinAppVersion = source.MinAppVersion,
        };
    }

    public override void Map(NoteNodeCreateDto source, AppNoteNode destination)
    {
        destination.NodeType = string.IsNullOrEmpty(source.NodeType) ? "Agent" : source.NodeType;
        destination.Title = source.Title;
        destination.Content = source.Content ?? string.Empty;
        destination.IsPublished = source.IsPublished;
        destination.Description = source.Description ?? string.Empty;
        destination.Category = string.IsNullOrEmpty(source.Category) ? "普通对话" : source.Category;
        destination.MinAppVersion = source.MinAppVersion;
    }
}

/// <summary>
/// UpdateDto → AppNoteNode 映射（CrudAppService UpdateAsync 用）。
/// </summary>
public class NoteNodeUpdateDtoToNoteNodeMapper : MapperBase<NoteNodeUpdateDto, AppNoteNode>
{
    public override AppNoteNode Map(NoteNodeUpdateDto source)
    {
        return new AppNoteNode(
            source.Title ?? string.Empty,
            source.Content ?? string.Empty,
            string.IsNullOrEmpty(source.NodeType) ? "Agent" : source.NodeType)
        {
            IsPublished = source.IsPublished,
            Description = source.Description ?? string.Empty,
            Category = string.IsNullOrEmpty(source.Category) ? "普通对话" : source.Category,
            MinAppVersion = source.MinAppVersion,
        };
    }

    public override void Map(NoteNodeUpdateDto source, AppNoteNode destination)
    {
        destination.NodeType = string.IsNullOrEmpty(source.NodeType) ? "Agent" : source.NodeType;
        destination.Title = source.Title;
        destination.Content = source.Content ?? string.Empty;
        destination.IsPublished = source.IsPublished;
        destination.Description = source.Description ?? string.Empty;
        destination.Category = string.IsNullOrEmpty(source.Category) ? "普通对话" : source.Category;
        destination.MinAppVersion = source.MinAppVersion;
    }
}

/// <summary>
/// AppNoteNode → NoteNodeDto 映射（CrudAppService Get/GetList 用）。替代旧 AgentConfigMappers。
/// </summary>
public class NoteNodeToNoteNodeDtoMapper : MapperBase<AppNoteNode, NoteNodeDto>
{
    public override NoteNodeDto Map(AppNoteNode source)
    {
        return new NoteNodeDto
        {
            Id = source.Id,
            TenantId = source.TenantId,
            UserId = source.UserId,
            CreatorId = source.CreatorId,
            NodeType = string.IsNullOrEmpty(source.NodeType) ? "Agent" : source.NodeType,
            Title = source.Title,
            Content = source.Content ?? string.Empty,
            IsPublished = source.IsPublished,
            Description = source.Description ?? string.Empty,
            Category = string.IsNullOrEmpty(source.Category) ? "普通对话" : source.Category,
            MinAppVersion = source.MinAppVersion,
            LastModifiedAt = source.LastModificationTime ?? source.CreationTime,
        };
    }

    public override void Map(AppNoteNode source, NoteNodeDto destination)
    {
        destination.Id = source.Id;
        destination.TenantId = source.TenantId;
        destination.UserId = source.UserId;
        destination.CreatorId = source.CreatorId;
        destination.NodeType = string.IsNullOrEmpty(source.NodeType) ? "Agent" : source.NodeType;
        destination.Title = source.Title;
        destination.Content = source.Content ?? string.Empty;
        destination.IsPublished = source.IsPublished;
        destination.Description = source.Description ?? string.Empty;
        destination.Category = string.IsNullOrEmpty(source.Category) ? "普通对话" : source.Category;
        destination.MinAppVersion = source.MinAppVersion;
        destination.LastModifiedAt = source.LastModificationTime ?? source.CreationTime;
    }
}
