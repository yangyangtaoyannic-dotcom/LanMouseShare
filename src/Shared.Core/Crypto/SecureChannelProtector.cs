using System;
using System.IO;
using System.Security.Cryptography;

namespace LanMouseShare.Core.Crypto
{
    public sealed class SecureChannelProtector
    {
        private readonly byte[] _encryptionKey;
        private readonly byte[] _macKey;

        public SecureChannelProtector(byte[] sharedSecret)
        {
            if (sharedSecret == null || sharedSecret.Length < 32)
            {
                throw new ArgumentException("A 32 byte shared secret is required.", "sharedSecret");
            }

            _encryptionKey = Derive(sharedSecret, "lan-mouse-share encryption");
            _macKey = Derive(sharedSecret, "lan-mouse-share mac");
        }

        public byte[] Protect(byte[] plaintext)
        {
            if (plaintext == null)
            {
                throw new ArgumentNullException("plaintext");
            }

            byte[] iv = SecureRandom.Bytes(16);
            byte[] ciphertext;
            using (var aes = Aes.Create())
            {
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                aes.Key = _encryptionKey;
                aes.IV = iv;

                using (var output = new MemoryStream())
                using (var crypto = new CryptoStream(output, aes.CreateEncryptor(), CryptoStreamMode.Write))
                {
                    crypto.Write(plaintext, 0, plaintext.Length);
                    crypto.FlushFinalBlock();
                    ciphertext = output.ToArray();
                }
            }

            var body = Combine(iv, ciphertext);
            var mac = ComputeMac(body);
            return Combine(body, mac);
        }

        public byte[] Unprotect(byte[] protectedBytes)
        {
            if (protectedBytes == null || protectedBytes.Length < 16 + 32)
            {
                throw new ArgumentException("Invalid protected payload.", "protectedBytes");
            }

            var bodyLength = protectedBytes.Length - 32;
            var body = new byte[bodyLength];
            var suppliedMac = new byte[32];
            Buffer.BlockCopy(protectedBytes, 0, body, 0, bodyLength);
            Buffer.BlockCopy(protectedBytes, bodyLength, suppliedMac, 0, suppliedMac.Length);

            var expectedMac = ComputeMac(body);
            if (!FixedTimeEquals(suppliedMac, expectedMac))
            {
                throw new CryptographicException("Payload authentication failed.");
            }

            var iv = new byte[16];
            var ciphertext = new byte[body.Length - iv.Length];
            Buffer.BlockCopy(body, 0, iv, 0, iv.Length);
            Buffer.BlockCopy(body, iv.Length, ciphertext, 0, ciphertext.Length);

            using (var aes = Aes.Create())
            {
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                aes.Key = _encryptionKey;
                aes.IV = iv;

                using (var input = new MemoryStream(ciphertext))
                using (var crypto = new CryptoStream(input, aes.CreateDecryptor(), CryptoStreamMode.Read))
                using (var output = new MemoryStream())
                {
                    crypto.CopyTo(output);
                    return output.ToArray();
                }
            }
        }

        private byte[] ComputeMac(byte[] body)
        {
            using (var hmac = new HMACSHA256(_macKey))
            {
                return hmac.ComputeHash(body);
            }
        }

        private static byte[] Derive(byte[] secret, string purpose)
        {
            using (var hmac = new HMACSHA256(secret))
            {
                return hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes(purpose));
            }
        }

        private static byte[] Combine(byte[] left, byte[] right)
        {
            var combined = new byte[left.Length + right.Length];
            Buffer.BlockCopy(left, 0, combined, 0, left.Length);
            Buffer.BlockCopy(right, 0, combined, left.Length, right.Length);
            return combined;
        }

        private static bool FixedTimeEquals(byte[] left, byte[] right)
        {
            if (left == null || right == null || left.Length != right.Length)
            {
                return false;
            }

            int diff = 0;
            for (int i = 0; i < left.Length; i++)
            {
                diff |= left[i] ^ right[i];
            }

            return diff == 0;
        }
    }
}
