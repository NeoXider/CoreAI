using System;
using System.Collections.Generic;
using CoreAI.Mods.Rbx.Datatypes;

namespace CoreAI.Mods.Rbx.Instances
{
    /// <summary>
    /// Attribute name and value validation per R6.7 (Instance.yaml limitations): names are
    /// alphanumeric plus period/hyphen/slash/underscore, at most 100 characters, and may not
    /// start with the reserved "RBX" prefix. Values are the primitive subset (string/bool/number)
    /// plus the datatype subset Roblox attributes support that exists today (Vector3, Vector2,
    /// Color3, UDim). Tables and every other type are rejected — Roblox parity (§5.1.5).
    /// </summary>
    public static class AttributeContract
    {
        public const int MaxNameLength = 100;
        public const string ReservedPrefix = "RBX";

        public static void ValidateName(string attributeName)
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
                bool allowed = char.IsLetterOrDigit(c) || c == '.' || c == '-' || c == '/' || c == '_';
                if (!allowed)
                {
                    throw RbxError.BadArgument(
                        "attribute name contains the disallowed character '" + c + "'",
                        "use only letters, digits, periods, hyphens, slashes, and underscores");
                }
            }
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
            var list = new List<KeyValuePair<string, object>>(attributes);
            list.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
            return list;
        }
    }
}
