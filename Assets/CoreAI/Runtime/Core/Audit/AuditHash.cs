using System;
using System.Buffers;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace CoreAI.Audit
{
    public static class AuditHash
    {
        /// <summary>Characters encoded per step by <see cref="ComputeParts"/>.</summary>
        private const int ChunkChars = 1024;

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

        /// <summary>
        /// Hashes the UTF-8 bytes of <paramref name="parts"/> concatenated in order WITHOUT building the
        /// concatenation: the digest is byte-for-byte the one <see cref="Compute(string)"/> returns for
        /// the joined string. Null parts contribute nothing; an input with no characters yields
        /// <c>""</c> exactly like the single-string form.
        /// <para>
        /// WHY: the orchestrator fingerprints every request (system prompt, user payload, the whole chat
        /// history). Joining those into one string first cost a copy of the entire prompt plus its UTF-8
        /// encoding - twice the prompt size in garbage per turn, hundreds of kilobytes on a long lesson -
        /// only to feed the hash. Here the text is encoded through a pooled buffer in bounded steps, and
        /// the UTF-8 encoder carries a surrogate pair split across two parts (or two steps) exactly as
        /// the joined string would have encoded it.
        /// </para>
        /// </summary>
        public static string ComputeParts(IReadOnlyList<string> parts)
        {
            if (parts == null)
            {
                return "";
            }

            long total = 0;
            for (int i = 0; i < parts.Count; i++)
            {
                total += parts[i]?.Length ?? 0;
            }

            if (total == 0)
            {
                return "";
            }

            char[] chars = ArrayPool<char>.Shared.Rent(ChunkChars);
            byte[] bytes = ArrayPool<byte>.Shared.Rent(Encoding.UTF8.GetMaxByteCount(ChunkChars));
            try
            {
                using SHA256 sha = SHA256.Create();
                Encoder encoder = Encoding.UTF8.GetEncoder();
                for (int i = 0; i < parts.Count; i++)
                {
                    string part = parts[i];
                    if (string.IsNullOrEmpty(part))
                    {
                        continue;
                    }

                    for (int offset = 0; offset < part.Length; offset += ChunkChars)
                    {
                        int count = Math.Min(ChunkChars, part.Length - offset);
                        part.CopyTo(offset, chars, 0, count);
                        int written = encoder.GetBytes(chars, 0, count, bytes, 0, false);
                        sha.TransformBlock(bytes, 0, written, null, 0);
                    }
                }

                int tail = encoder.GetBytes(chars, 0, 0, bytes, 0, true);
                sha.TransformFinalBlock(bytes, 0, tail);
                return ByteArrayToHex(sha.Hash);
            }
            finally
            {
                ArrayPool<char>.Shared.Return(chars);
                ArrayPool<byte>.Shared.Return(bytes);
            }
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

        /// <summary>Lowercase hex of <paramref name="bytes"/>; one string, no per-byte formatting.</summary>
        internal static string ByteArrayToHex(byte[] bytes)
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
