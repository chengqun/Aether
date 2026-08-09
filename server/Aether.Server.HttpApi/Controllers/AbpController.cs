using Aether.Server.Localization;
using Volo.Abp.AspNetCore.Mvc;

namespace Aether.Server.Controllers;

/* Inherit your controllers from this class.
 */
public abstract class AbpController : AbpControllerBase
{
    protected AbpController()
    {
        LocalizationResource = typeof(AbpResource);
    }
}
