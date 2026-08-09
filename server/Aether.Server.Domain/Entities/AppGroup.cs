using System;
using Volo.Abp.Domain.Entities.Auditing;
using Volo.Abp.MultiTenancy;

namespace Aether.Server.Domain.Entities
{
    public class AppGroup : FullAuditedAggregateRoot<Guid>, IMultiTenant
    {
        public Guid? TenantId { get; set; }

        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public Guid OwnerId { get; set; }

        public AppGroup() { }

        public AppGroup(Guid id, string name, Guid ownerId, string description = "")
        {
            Id = id;
            Name = name;
            OwnerId = ownerId;
            Description = description;
        }

        public void Rename(string newName) => Name = newName;
        public void UpdateDescription(string description) => Description = description;
    }
}