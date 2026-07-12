using System;
using System.Security.Cryptography;
using System.Text;

namespace CoreAI.Audit
{
    public static class AuditHash
    {
        public static string Compute(string input)
        {
            if (string.IsNullOrEmpty(input))
            {
                return "";
            }

            using SHA256 sha = SHA256.Create();
            byte[] data = sha.ComputeHash(Encoding.UTF8.GetBytes(input));
            return ByteArrayToHex(data);
        }

        public static string Chain(string prevHash, string jsonLine)
        {
            return Compute(prevHash + jsonLine);
        }

        /// <summary>
        /// Keyed (HMAC-SHA256) equivalent of <see cref="Compute"/>. Unlike the plain hash, this
        /// cannot be recomputed by a party that does not hold <paramref name="key"/> — it is the
        /// primitive behind a genuinely tamper-evident chain when the key is withheld from whoever
        /// owns the file (e.g. a host- or server-held session key).
        /// </summary>
        public static string ComputeHmac(string key, string input)
        {
            if (string.IsNullOrEmpty(input))
            {
                return "";
            }

            using HMACSHA256 hmac = new(Encoding.UTF8.GetBytes(key ?? ""));
            byte[] data = hmac.ComputeHash(Encoding.UTF8.GetBytes(input));
            return ByteArrayToHex(data);
        }

        /// <summary>
        /// Keyed (HMAC-SHA256) equivalent of <see cref="Chain"/>. Use when the chain must resist
        /// tampering by the party that owns the file; verify with the same key via
        /// <see cref="AuditLogVerifier.Verify(string,string)"/>.
        /// </summary>
        public static string HmacChain(string key, string prevHash, string jsonLine)
        {
            return ComputeHmac(key, prevHash + jsonLine);
        }

        private static string ByteArrayToHex(byte[] bytes)
        {
            char[] result = new char[bytes.Length * 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                int val = bytes[i];
                result[i * 2] = HexChar(val >> 4);
                result[i * 2 + 1] = HexChar(val & 0x0F);
            }

            return new string(result);
        }

        private static char HexChar(int nibble)
        {
            return (char)(nibble < 10 ? '0' + nibble : 'a' + nibble - 10);
        }
    }
}
