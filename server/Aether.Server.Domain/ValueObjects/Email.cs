using System;
using System.Collections.Generic;
using Volo.Abp.Domain.Values;

namespace Aether.Server.Domain.ValueObjects
{
    public class Email : ValueObject
    {
        public string Address { get; private set; }

        private Email() { }

        public Email(string address)
        {
            if (string.IsNullOrEmpty(address))
            {
                throw new ArgumentException("Email address cannot be empty");
            }

            if (!IsValidEmail(address))
            {
                throw new ArgumentException("Invalid email address");
            }

            Address = address;
        }

        private bool IsValidEmail(string email)
        {
            try
            {
                var addr = new System.Net.Mail.MailAddress(email);
                return addr.Address == email;
            }
            catch
            {
                return false;
            }
        }

        protected override IEnumerable<object> GetAtomicValues()
        {
            yield return Address;
        }
    }
}