using System;
using System.Collections.Generic;
using System.Text;
using Aether.Server.Localization;
using Volo.Abp.Application.Services;

namespace Aether.Server;

/* Inherit your application services from this class.
 */
public abstract class AbpAppService : ApplicationService
{
    protected AbpAppService()
    {
        LocalizationResource = typeof(AbpResource);
    }
}
