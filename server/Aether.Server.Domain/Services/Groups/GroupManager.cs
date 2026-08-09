using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Aether.Server.Domain.Entities;
using Aether.Server.Domain.Shared.Enums;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.Domain.Services;
using Volo.Abp.Guids;
using Microsoft.Extensions.Logging;

namespace Aether.Server.Domain.Services.Groups
{
    public class GroupManager : DomainService, IGroupManager
    {
        private readonly IRepository<AppGroup, Guid> _groupRepository;
        private readonly IRepository<AppRelationship, Guid> _relationshipRepository;
        private readonly ILogger<GroupManager> _logger;

        public GroupManager(
            IRepository<AppGroup, Guid> groupRepository,
            IRepository<AppRelationship, Guid> relationshipRepository,
            ILogger<GroupManager> logger)
        {
            _groupRepository = groupRepository;
            _relationshipRepository = relationshipRepository;
            _logger = logger;
        }

        public async Task<AppGroup> CreateGroupAsync(string name, Guid ownerId, string description = "")
        {
            _logger.LogInformation("[GroupManager] CreateGroupAsync start. Name: {Name}, OwnerId: {OwnerId}", name, ownerId);

            var group = new AppGroup(GuidGenerator.Create(), name, ownerId, description);
            await _groupRepository.InsertAsync(group, true); // 立即保存更改
            _logger.LogInformation("[GroupManager] Group inserted. Id: {GroupId}", group.Id);

            var existingRelationship = await _relationshipRepository.FirstOrDefaultAsync(
                r => r.UserId == ownerId && r.TargetType == RelationshipType.Group && r.TargetId == group.Id.ToString()
            );

            if (existingRelationship == null)
            {
                var relationship = new AppRelationship(ownerId, RelationshipType.Group, group.Id.ToString(), RelationshipStatus.Accepted);
                await _relationshipRepository.InsertAsync(relationship, true); // 立即保存更改
                _logger.LogInformation("[GroupManager] Owner relationship created for group {GroupId}", group.Id);
            }

            _logger.LogInformation("[GroupManager] Created chat group '{Name}' by user {OwnerId}", name, ownerId);
            return group;
        }

        public async Task<AppGroup> GetGroupAsync(Guid groupId)
        {
            return await _groupRepository.GetAsync(groupId);
        }

        public async Task<List<AppGroup>> SearchGroupsAsync(string keyword)
        {
            return await _groupRepository.GetListAsync(g => g.Name.Contains(keyword));
        }

        public async Task DisbandGroupAsync(Guid groupId)
        {
            await _groupRepository.DeleteAsync(groupId);
            _logger.LogInformation("[GroupManager] Disbanded chat group {GroupId}", groupId);
        }
    }
}