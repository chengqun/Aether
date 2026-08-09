using Aether.Server.Application.Contracts.DTOs.Security;
using SecurityEntity = Aether.Server.Domain.Entities.AppSecurity;
using Volo.Abp.Mapperly;

namespace Aether.Server.Application.Mappings;

public class SecurityCreateDtoToSecurityMapper : MapperBase<SecurityCreateDto, SecurityEntity>
{
    public override SecurityEntity Map(SecurityCreateDto source)
    {
        return new SecurityEntity(source.Code, source.Name, source.Type, source.Exchange, source.BaseCode);
    }

    public override void Map(SecurityCreateDto source, SecurityEntity destination)
    {
        destination.UpdateAll(source.Code, source.Name, source.Type, source.Exchange, source.BaseCode);
    }
}

public class SecurityUpdateDtoToSecurityMapper : MapperBase<SecurityUpdateDto, SecurityEntity>
{
    public override SecurityEntity Map(SecurityUpdateDto source)
    {
        return new SecurityEntity("", source.Name, source.Type, source.Exchange, source.BaseCode);
    }

    public override void Map(SecurityUpdateDto source, SecurityEntity destination)
    {
        destination.UpdateDetails(source.Name, source.Exchange, source.BaseCode);
    }
}

public class SecurityToSecurityDtoMapper : MapperBase<SecurityEntity, SecurityDto>
{
    public override SecurityDto Map(SecurityEntity source)
    {
        return new SecurityDto
        {
            Id = source.Id,
            Code = source.Code,
            Name = source.Name,
            Type = source.Type,
            Exchange = source.Exchange,
            BaseCode = source.BaseCode
        };
    }

    public override void Map(SecurityEntity source, SecurityDto destination)
    {
        destination.Id = source.Id;
        destination.Code = source.Code;
        destination.Name = source.Name;
        destination.Type = source.Type;
        destination.Exchange = source.Exchange;
        destination.BaseCode = source.BaseCode;
    }
}
