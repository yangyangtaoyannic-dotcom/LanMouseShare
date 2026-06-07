using System;
using System.Security.Cryptography;

namespace LanMouseShare.Core.Crypto
{
    public static class SecureRandom
    {
        public static byte[] Bytes(int length)
        {
            if (length <= 0)
            {
                throw new ArgumentOutOfRangeException("length");
            }

            var bytes = new byte[length];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(bytes);
            }

            return bytes;
        }

        public static string NumericCode(int digits)
        {
            if (digits <= 0 || digits > 9)
            {
                throw new ArgumentOutOfRangeException("digits");
            }

            var bytes = Bytes(4);
            var value = BitConverter.ToUInt32(bytes, 0);
            var max = (int)Math.Pow(10, digits);
            return (value % max).ToString(new string('0', digits));
        }
    }
}
