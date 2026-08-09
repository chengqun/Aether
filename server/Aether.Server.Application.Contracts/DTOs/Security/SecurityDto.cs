using System;
using Volo.Abp.Application.Dtos;
using Volo.Abp.Application.Services;

namespace Aether.Server.Application.Contracts.DTOs.Security
{
    public class SecurityDto : EntityDto<int>
    {
        public string Code { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public string Exchange { get; set; } = string.Empty;
        public string BaseCode { get; set; } = string.Empty;
    }

    public class SecurityCreateDto
    {
        public string Code { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public string Exchange { get; set; } = string.Empty;
        public string BaseCode { get; set; } = string.Empty;
    }

    public class SecurityUpdateDto
    {
        public string Name { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public string Exchange { get; set; } = string.Empty;
        public string BaseCode { get; set; } = string.Empty;
    }
}