using System;
using Volo.Abp.Domain.Entities;
using Volo.Abp.MultiTenancy;

namespace Aether.Server.Domain.Entities
{
    public class AppDevice : Entity<Guid>, IMultiTenant
    {
        public Guid? TenantId { get; set; }

        public string DeviceId { get; internal set; } = string.Empty;
        public string DeviceName { get; internal set; } = string.Empty;
        public string DeviceType { get; internal set; } = string.Empty;
        public string AuthorizationCode { get; internal set; } = string.Empty;
        public Guid UserId { get; internal set; }

        /// <summary>
        /// 是否允许其他用户搜索到该设备。默认 false，只允许添加自己创建的设备。
        /// </summary>
        public bool IsSearchable { get; internal set; } = false;

        public AppDevice() { }

        public AppDevice(Guid id, string deviceId, string deviceName, string deviceType, string authorizationCode, Guid userId) : base(id)
        {
            DeviceId = deviceId;
            DeviceName = deviceName;
            DeviceType = deviceType;
            AuthorizationCode = authorizationCode;
            UserId = userId;
        }

        /// <summary>
        /// 重命名设备
        /// </summary>
        public void Rename(string deviceName)
        {
            DeviceName = deviceName;
        }

        /// <summary>
        /// 更改设备类型
        /// </summary>
        public void ChangeDeviceType(string deviceType)
        {
            DeviceType = deviceType;
        }

        /// <summary>
        /// 设置授权码
        /// </summary>
        public void SetAuthorizationCode(string code)
        {
            AuthorizationCode = code;
        }

        /// <summary>
        /// 设置是否可搜索
        /// </summary>
        public void SetSearchable(bool searchable)
        {
            IsSearchable = searchable;
        }
    }
}
