using System.Security.Cryptography;
using System.Text;

namespace CoreAI.Infrastructure.Lua
{
    /// <summary>
    /// The one rule for the folder names the file stores generate from an id: a store id's namespace folder under
    /// <c>Stores</c> and a mod's folder in a mod source store. New entries get a short name, a 128-bit prefix of the
    /// SHA-256 digest in lowercase base32 (28 characters, case-safe on Windows); a folder an earlier build wrote under
    /// a long name keeps that name, so the stores look for it first (load, update in place, delete).
    /// </summary>
    /// <remarks>
    /// WHY short names: the long names (<c>id-</c> plus 64 hex characters) took 134 of the 259 characters a Windows
    /// path may have below the storage root, so a confirmed world load needed a persistentDataPath of at most 55
    /// characters. With the short ones every store path fits a root of 121 characters. A 128-bit prefix still makes a
    /// collision between two ids about 2^-64 likely at a billion ids.
    /// </remarks>
    internal static class CoreAiStorageNames
    {
        /// <summary>Prefix of a short store namespace folder.</summary>
        public const string StoreFolderPrefix = "s-";

        /// <summary>Prefix of a short mod folder.</summary>
        public const string ModFolderPrefix = "m-";

        /// <summary>Prefix of every long (legacy) generated name.</summary>
        public const string LegacyPrefix = "id-";

        private const string LowerBase32Alphabet = "abcdefghijklmnopqrstuvwxyz234567";

        private const int ShortDigestBytes = 16;

        private static readonly UTF8Encoding StrictUtf8 = new(false, true);

        /// <summary>The short namespace folder of <paramref name="storeId"/>; a lone surrogate throws, as it always did.</summary>
        public static string StoreFolderName(string storeId)
        {
            return StoreFolderPrefix + ShortDigest(StoreDigest(storeId));
        }

        /// <summary>The namespace folder 7.56.0 and earlier builds wrote: <c>id-</c> and 64 hex characters.</summary>
        public static string LegacyStoreFolderName(string storeId)
        {
            return LegacyPrefix + Hex(StoreDigest(storeId));
        }

        /// <summary>The short folder of <paramref name="modId"/> in a mod source store.</summary>
        public static string ModFolderName(string modId)
        {
            return ModFolderPrefix + ShortDigest(ModDigest(modId));
        }

        /// <summary>The canonical mod folder 7.56.0 and earlier builds wrote: <c>id-</c> and 64 hex characters.</summary>
        public static string LegacyModFolderName(string modId)
        {
            return LegacyPrefix + Hex(ModDigest(modId));
        }

        /// <summary>The mod folder of a version-1 session layout: <c>id-</c> and the whole digest in 52 base32 characters.</summary>
        public static string LegacyCompactModFolderName(string modId)
        {
            return LegacyPrefix + EncodeLowerBase32(ModDigest(modId));
        }

        /// <summary>Lowercase RFC 4648 base32 without padding.</summary>
        public static string EncodeLowerBase32(byte[] bytes)
        {
            StringBuilder encoded = new((bytes.Length * 8 + 4) / 5);
            uint pending = 0;
            int pendingBits = 0;
            foreach (byte value in bytes)
            {
                pending = (pending << 8) | value;
                pendingBits += 8;
                while (pendingBits >= 5)
                {
                    pendingBits -= 5;
                    encoded.Append(LowerBase32Alphabet[(int)((pending >> pendingBits) & 31u)]);
                }

                pending &= (1u << pendingBits) - 1u;
            }

            if (pendingBits > 0)
            {
                encoded.Append(LowerBase32Alphabet[(int)(pending << (5 - pendingBits))]);
            }

            return encoded.ToString();
        }

        // WHY strict for store ids and lenient for mod ids: the names they replace were made that way, and the long
        // names must stay byte for byte what earlier builds wrote.
        private static byte[] StoreDigest(string storeId)
        {
            using SHA256 algorithm = SHA256.Create();
            return algorithm.ComputeHash(StrictUtf8.GetBytes(storeId));
        }

        private static byte[] ModDigest(string modId)
        {
            using SHA256 algorithm = SHA256.Create();
            return algorithm.ComputeHash(Encoding.UTF8.GetBytes(modId));
        }

        private static string ShortDigest(byte[] digest)
        {
            byte[] prefix = new byte[ShortDigestBytes];
            System.Array.Copy(digest, prefix, ShortDigestBytes);
            return EncodeLowerBase32(prefix);
        }

        private static string Hex(byte[] digest)
        {
            StringBuilder hex = new(digest.Length * 2);
            foreach (byte value in digest)
            {
                hex.Append(value.ToString("x2"));
            }

            return hex.ToString();
        }
    }
}
