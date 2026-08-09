using Aether.Server.Application.Contracts.DTOs.VersionConfig;
using VersionConfigEntity = Aether.Server.Domain.Entities.AppVersionConfig;
using Volo.Abp.Mapperly;

namespace Aether.Server.Application.Mappings;

public class VersionConfigCreateDtoToVersionConfigMapper : MapperBase<VersionConfigCreateDto, VersionConfigEntity>
{
    public override VersionConfigEntity Map(VersionConfigCreateDto source)
    {
        return new VersionConfigEntity
        {
            Platform = source.Platform,
            VersionCode = source.VersionCode,
            VersionName = source.VersionName,
            UpdateUrl = source.UpdateUrl,
            UpdateNote = source.UpdateNote,
            IsForce = source.IsForce
        };
    }

    public override void Map(VersionConfigCreateDto source, VersionConfigEntity destination)
    {
        destination.Platform = source.Platform;
        destination.VersionCode = source.VersionCode;
        destination.VersionName = source.VersionName;
        destination.UpdateUrl = source.UpdateUrl;
        destination.UpdateNote = source.UpdateNote;
        destination.IsForce = source.IsForce;
    }
}

public class VersionConfigUpdateDtoToVersionConfigMapper : MapperBase<VersionConfigUpdateDto, VersionConfigEntity>
{
    public override VersionConfigEntity Map(VersionConfigUpdateDto source)
    {
        return new VersionConfigEntity
        {
            Platform = source.Platform,
            VersionCode = source.VersionCode,
            VersionName = source.VersionName,
            UpdateUrl = source.UpdateUrl,
            UpdateNote = source.UpdateNote,
            IsForce = source.IsForce
        };
    }

    public override void Map(VersionConfigUpdateDto source, VersionConfigEntity destination)
    {
        destination.Platform = source.Platform;
        destination.VersionCode = source.VersionCode;
        destination.VersionName = source.VersionName;
        destination.UpdateUrl = source.UpdateUrl;
        destination.UpdateNote = source.UpdateNote;
        destination.IsForce = source.IsForce;
    }
}

public class VersionConfigToVersionConfigDtoMapper : MapperBase<VersionConfigEntity, VersionConfigDto>
{
    public override VersionConfigDto Map(VersionConfigEntity source)
    {
        return new VersionConfigDto
        {
            Id = source.Id,
            Platform = source.Platform,
            VersionCode = source.VersionCode,
            VersionName = source.VersionName,
            UpdateUrl = source.UpdateUrl,
            UpdateNote = source.UpdateNote,
            IsForce = source.IsForce
        };
    }

    public override void Map(VersionConfigEntity source, VersionConfigDto destination)
    {
        destination.Id = source.Id;
        destination.Platform = source.Platform;
        destination.VersionCode = source.VersionCode;
        destination.VersionName = source.VersionName;
        destination.UpdateUrl = source.UpdateUrl;
        destination.UpdateNote = source.UpdateNote;
        destination.IsForce = source.IsForce;
    }
}
