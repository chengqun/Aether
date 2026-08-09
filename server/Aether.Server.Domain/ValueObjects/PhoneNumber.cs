using System;
using System.Collections.Generic;
using Volo.Abp.Domain.Values;

namespace Aether.Server.Domain.ValueObjects
{
    public class PhoneNumber : ValueObject
    {
        public string Number { get; private set; }

        private PhoneNumber() { }

        public PhoneNumber(string number)
        {
            if (string.IsNullOrEmpty(number))
            {
                throw new ArgumentException("Phone number cannot be empty");
            }

            if (!IsValidPhoneNumber(number))
            {
                throw new ArgumentException("Invalid phone number");
            }

            Number = number;
        }

        private bool IsValidPhoneNumber(string phoneNumber)
        {
            // 简单的电话号码验证逻辑
            return phoneNumber.Length >= 7 && phoneNumber.Length <= 15;
        }

        protected override IEnumerable<object> GetAtomicValues()
        {
            yield return Number;
        }
    }
}