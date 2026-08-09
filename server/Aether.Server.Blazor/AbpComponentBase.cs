using Aether.Server.Localization;
using Volo.Abp.AspNetCore.Components;

namespace Aether.Server.Blazor;

public abstract class AbpComponentBase : Volo.Abp.AspNetCore.Components.AbpComponentBase
{
    protected AbpComponentBase()
    {
        LocalizationResource = typeof(AbpResource);
    }
}

