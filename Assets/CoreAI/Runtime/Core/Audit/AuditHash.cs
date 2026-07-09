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
