using System.Threading.Tasks;

namespace Aether.Server.Data;

public interface IAbpDbSchemaMigrator
{
    Task MigrateAsync();
}
