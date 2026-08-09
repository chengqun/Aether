using System;
using System.Linq;
using System.Threading.Tasks;
using Aether.Server.Domain.Entities;
using Aether.Server.Domain.Services.Securities;
using Volo.Abp;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.Domain.Services;
using Volo.Abp.Guids;

namespace Aether.Server.Domain.Services.Moment
{
    public class MomentManager : DomainService, IMomentManager
    {
        private readonly IRepository<AppMoment, Guid> _momentRepository;
        private readonly ISecurityManager _securityManager;
        private readonly IGuidGenerator _guidGenerator;

        public MomentManager(
            IRepository<AppMoment, Guid> momentRepository,
            ISecurityManager securityManager,
            IGuidGenerator guidGenerator)
        {
            _momentRepository = momentRepository;
            _securityManager = securityManager;
            _guidGenerator = guidGenerator;
        }

        public async Task<AppMoment> CreateMomentAsync(Guid userId, string content, string imageUrl = null, string type = "User", string visibility = "Friends")
        {
            var moment = new AppMoment(_guidGenerator.Create(), userId, content)
                .WithImage(imageUrl)
                .WithType(type)
                .WithVisibility(visibility);
            return await _momentRepository.InsertAsync(moment);
        }

        public async Task<AppMoment> CreateStockMomentAsync(int stockId, string content, string imageUrl = null)
        {
            return await CreateStockMomentAsync(stockId, content, DateTime.UtcNow, imageUrl);
        }

        public async Task<AppMoment> CreateStockMomentAsync(int stockId, string content, DateTime createdAt, string imageUrl = null)
        {
            var securities = await _securityManager.GetAllAsync();
            var stock = securities.FirstOrDefault(s => s.Id == stockId);
            if (stock == null)
            {
                throw new BusinessException("Aether:StockNotFound").WithData("StockId", stockId);
            }

            var moment = new AppMoment(_guidGenerator.Create(), CreateStockGuid(stockId), content)
                .WithImage(imageUrl)
                .WithType("Stock")
                .WithVisibility("Public");
            moment.SetCreatedAt(createdAt);
            return await _momentRepository.InsertAsync(moment);
        }

        public async Task<AppMoment> CreateDeviceMomentAsync(string deviceId, string content, string imageUrl = null)
        {
            var moment = new AppMoment(_guidGenerator.Create(), CreateTargetGuid(deviceId), content)
                .WithImage(imageUrl)
                .WithType("Device")
                .WithVisibility("Public");
            return await _momentRepository.InsertAsync(moment);
        }

        public async Task<AppMoment> CreateGroupMomentAsync(string groupId, string content, string imageUrl = null)
        {
            var moment = new AppMoment(_guidGenerator.Create(), CreateTargetGuid(groupId), content)
                .WithImage(imageUrl)
                .WithType("Group")
                .WithVisibility("Public");
            return await _momentRepository.InsertAsync(moment);
        }

        public async Task DeleteMomentAsync(Guid momentId, Guid currentUserId)
        {
            var moment = await _momentRepository.GetAsync(momentId);
            if (moment.UserId != currentUserId)
            {
                throw new BusinessException("Aether:UnauthorizedDelete");
            }
            await _momentRepository.DeleteAsync(moment);
        }

        public Guid CreateStockGuid(int stockId)
        {
            string stockIdHex = stockId.ToString("X8");
            string guidString = stockIdHex.PadRight(32, '0');
            guidString = $"{guidString.Substring(0, 8)}-{guidString.Substring(8, 4)}-{guidString.Substring(12, 4)}-{guidString.Substring(16, 4)}-{guidString.Substring(20)}";
            return new Guid(guidString);
        }

        public Guid CreateTargetGuid(string targetId)
        {
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(targetId);
            byte[] hash = System.Security.Cryptography.SHA256.Create().ComputeHash(bytes);
            return new Guid(hash.Take(16).ToArray());
        }
    }
}