using System;
using System.Collections.Generic;
using System.Globalization;
using CoreAI.Mods.Rbx.Datatypes;

namespace CoreAI.Mods.Rbx.Instances
{
    /// <summary>
    /// Attribute name and value validation per R6.7 (Instance.yaml limitations): names are
    /// alphanumeric plus period/hyphen/slash/underscore, at most 100 characters, and may not
    /// start with the reserved "RBX" prefix. Two name rules exist: <see cref="ValidateNewName"/>
    /// is the mirror's ASCII-only rule for a name being created, <see cref="ValidateName"/> the
    /// stored-name rule a saved world, a replication snapshot or a read may carry. Values are the
    /// primitive subset (string/bool/number) plus the datatype subset Roblox attributes support
    /// that exists today (Vector3, Vector2, Color3, UDim). Tables and every other type are
    /// rejected — Roblox parity (§5.1.5).
    /// </summary>
    public static class AttributeContract
    {
        public const int MaxNameLength = 100;
        public const string ReservedPrefix = "RBX";

        private const string AsciiNameHint =
            "use only ASCII letters A-Z and a-z, digits 0-9, periods, hyphens, slashes, and underscores";

        /// <summary>
        /// The stored-name rule: non-empty, at most 100 characters, no "RBX" prefix, and only
        /// letters, digits (any script), periods, hyphens, slashes and underscores. Restore,
        /// replication and reads validate through this rule.
        /// </summary>
        public static void ValidateName(string attributeName)
        {
            // WHY letters and digits of every script stay accepted here: scripts have been able to
            // create names such as Cyrillic words, so saved world packages and replication
            // snapshots may already hold them, and restore validates every stored name through
            // this rule. Refusing them here would make such a world unloadable, so the mirror's
            // ASCII-only rule belongs where a name is created, in ValidateNewName.
            ValidateNameCore(attributeName, false);
        }

        /// <summary>
        /// The mirror rule for a name being created (Instance.yaml SetAttribute: "Names must only
        /// use alphanumeric characters", plus periods, hyphens, slashes and underscores): ASCII
        /// letters A-Z and a-z and digits 0-9 only, at most 100 characters, no "RBX" prefix.
        /// Raises BAD_ARGUMENT naming the first offending character; a non-ASCII letter or digit
        /// is named with its code point as well.
        /// </summary>
        public static void ValidateNewName(string attributeName)
        {
            // TODO: M1-23 — call this from the Lua SetAttribute binding when the call creates a name
            // (a non-nil value for a name the instance does not hold yet); restore and replication
            // must keep going through ValidateName.
            ValidateNameCore(attributeName, true);
        }

        private static void ValidateNameCore(string attributeName, bool asciiOnly)
        {
            if (string.IsNullOrEmpty(attributeName))
            {
                throw RbxError.BadArgument("attribute name must be a non-empty string",
                    "pass a name like \"Health\" at argument 1");
            }

            if (attributeName.Length > MaxNameLength)
            {
                throw RbxError.BadArgument(
                    "attribute name exceeds " + MaxNameLength + " characters",
                    "shorten the attribute name to 100 characters or less");
            }

            if (attributeName.StartsWith(ReservedPrefix, StringComparison.Ordinal))
            {
                throw RbxError.BadArgument(
                    "attribute names starting with \"RBX\" are reserved for Roblox",
                    "rename the attribute without the RBX prefix");
            }

            for (int i = 0; i < attributeName.Length; i++)
            {
                char c = attributeName[i];
                if (IsAsciiLetterOrDigit(c) || c == '.' || c == '-' || c == '/' || c == '_')
                {
                    continue;
                }

                if (!char.IsLetterOrDigit(c))
                {
                    throw RbxError.BadArgument(
                        "attribute name contains the disallowed character '" + c + "'",
                        asciiOnly
                            ? AsciiNameHint
                            : "use only letters, digits, periods, hyphens, slashes, and underscores");
                }

                if (asciiOnly)
                {
                    throw RbxError.BadArgument(
                        "attribute name contains the non-ASCII character '" + c + "' (U+"
                        + ((int)c).ToString("X4", CultureInfo.InvariantCulture) + ")",
                        AsciiNameHint);
                }
            }
        }

        private static bool IsAsciiLetterOrDigit(char c)
        {
            return (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9');
        }

        /// <summary>
        /// Normalizes an accepted value (numbers become double for stable serialization; the
        /// datatype subset is stored as-is) or throws BAD_ARGUMENT naming the offending type and
        /// the exact supported list. Null is handled by the caller (remove).
        /// </summary>
        public static object NormalizeValue(object value)
        {
            switch (value)
            {
                case string s:
                    return s;
                case bool b:
                    return b;
                case double d:
                    return d;
                case float f:
                    return (double)f;
                case int i:
                    return (double)i;
                case long l:
                    return (double)l;
                case RbxVector3 v3:
                    return v3;
                case RbxVector2 v2:
                    return v2;
                case RbxColor3 c:
                    return c;
                case RbxUDim u:
                    return u;
                default:
                    throw RbxError.BadArgument(
                        "attribute value of type " + value.GetType().Name + " is not supported",
                        "pass a string, boolean, number, Vector3, Vector2, Color3, or UDim at argument 2");
            }
        }

        /// <summary>Stable enumeration order for snapshots and GetAttributes.</summary>
        public static IReadOnlyList<KeyValuePair<string, object>> Sorted(
            IReadOnlyDictionary<string, object> attributes)
        {
            List<KeyValuePair<string, object>> list = new(attributes);
            list.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
            return list;
        }
    }
}
