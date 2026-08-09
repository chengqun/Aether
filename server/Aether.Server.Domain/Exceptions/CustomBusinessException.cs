using System;
using Volo.Abp; 

namespace Aether.Server.Domain.Exceptions
{
    public class CustomBusinessException : BusinessException
    {
        public CustomBusinessException(
            string code = null,
            string message = null,
            string details = null,
            Exception innerException = null)
            : base(code, message, details, innerException)
        {
        }
    }
}