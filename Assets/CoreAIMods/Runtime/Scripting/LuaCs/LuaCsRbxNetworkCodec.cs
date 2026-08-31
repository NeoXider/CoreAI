using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using Lua;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using static CoreAI.Ai.LuaCs.LuaCsRbxLua;

namespace CoreAI.Ai.LuaCs
{
    /// <summary>Portable table copied across the byte boundary before a destination state rebuilds it.</summary>
    internal sealed class LuaCsRbxNetworkTable
    {
        public LuaCsRbxNetworkTable(IReadOnlyList<object> arrayValues)
        {
            IsArray = true;
            ArrayValues = arrayValues ?? throw new ArgumentNullException(nameof(arrayValues));
            DictionaryValues = Array.Empty<KeyValuePair<string, object>>();
        }

        public LuaCsRbxNetworkTable(
            IReadOnlyList<KeyValuePair<string, object>> dictionaryValues)
        {
            IsArray = false;
            ArrayValues = Array.Empty<object>();
            DictionaryValues = dictionaryValues
                ?? throw new ArgumentNullException(nameof(dictionaryValues));
        }

        public bool IsArray { get; }

        public IReadOnlyList<object> ArrayValues { get; }

        public IReadOnlyList<KeyValuePair<string, object>> DictionaryValues { get; }
    }

    /// <summary>
    /// Lua-CSharp remote marshaller. It wraps the scalar precedent in <see cref="LuaCsValueMarshaller"/>
    /// with Rbx datatype and Instance tags plus the R5.10 table-copy rules, then emits UTF-8 JSON bytes.
    /// </summary>
    internal sealed class LuaCsRbxNetworkCodec
    {
        private const string TypeKey = "$rbx";
        internal const int MaxNestingDepth = 64;
        internal const int MaxAggregateEntries = 100_000;
        private const int MaxJsonEnvelopeDepth = MaxNestingDepth * 2 + 4;
        private static readonly UTF8Encoding Utf8 = new(false);

        private sealed class TraversalState
        {
            private int _aggregateEntries;

            public void ConsumeEntries(int count, string path)
            {
                if (count < 0 || count > MaxAggregateEntries - _aggregateEntries)
                {
                    throw RbxError.BadArgument(
                        "remote payload exceeds CoreAI's " + MaxAggregateEntries
                        + " aggregate entry limit at " + path,
                        "send fewer total arguments and table entries");
                }

                _aggregateEntries += count;
            }
        }

        private sealed class LuaTableReferenceComparer : IEqualityComparer<LuaTable>
        {
            public bool Equals(LuaTable left, LuaTable right)
            {
                return ReferenceEquals(left, right);
            }

            public int GetHashCode(LuaTable value)
            {
                return RuntimeHelpers.GetHashCode(value);
            }
        }

        private readonly InstanceRegistry _registry;
        private readonly RbxEnumRegistry _enums;
        private readonly Action<string> _log;

        public LuaCsRbxNetworkCodec(InstanceRegistry registry, RbxEnumRegistry enums,
            Action<string> log)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _enums = enums ?? throw new ArgumentNullException(nameof(enums));
            _log = log;
        }

        public byte[] EncodeArguments(IReadOnlyList<LuaValue> arguments)
        {
            JArray root = new();
            TraversalState state = new();
            HashSet<LuaTable> activeTables = new(new LuaTableReferenceComparer());
            int count = arguments?.Count ?? 0;
            state.ConsumeEntries(count, "$");
            for (int index = 0; index < count; index++)
            {
                root.Add(EncodeValue(arguments[index], state, activeTables,
                    0, "$[" + index + "]"));
            }

            string json = root.ToString(Formatting.None);
            return Utf8.GetBytes(json);
        }

        public object[] DecodeArguments(byte[] payload)
        {
            string json = Utf8.GetString(payload ?? Array.Empty<byte>());
            JToken token;
            try
            {
                using StringReader stringReader = new(json);
                using JsonTextReader reader = new(stringReader)
                {
                    DateParseHandling = DateParseHandling.None,
                    FloatParseHandling = FloatParseHandling.Double,
                    MaxDepth = MaxJsonEnvelopeDepth
                };
                token = JToken.ReadFrom(reader);
                if (reader.Read())
                {
                    throw new JsonReaderException(
                        "Additional text follows the remote payload envelope.");
                }
            }
            catch (Exception ex)
            {
                throw RbxError.BadArgument(
                    "remote payload is not a valid CoreAI Rbx envelope: " + ex.Message,
                    "send payloads through the Rbx remote marshaller");
            }

            if (!(token is JArray array))
            {
                throw RbxError.BadArgument(
                    "remote payload root must be an argument array",
                    "send payloads through the Rbx remote marshaller");
            }

            object[] arguments = new object[array.Count];
            TraversalState state = new();
            state.ConsumeEntries(array.Count, "$");
            for (int index = 0; index < array.Count; index++)
            {
                arguments[index] = DecodeValue(
                    array[index], state, 0, "$[" + index + "]");
            }

            return arguments;
        }

        public LuaValue ToLuaValue(LuaCsRbxModContext context, object value)
        {
            switch (value)
            {
                case null:
                    return LuaValue.Nil;
                case bool boolean:
                    return boolean;
                case double number:
                    return number;
                case string text:
                    return text;
                case RbxInstance instance:
                    return context.WrapInstance(instance);
                case RbxVector3 vector3:
                    return LuaCsRbxDatatypeBindings.Wrap(vector3);
                case RbxVector2 vector2:
                    return LuaCsRbxDatatypeBindings.Wrap(vector2);
                case RbxCFrame cframe:
                    return LuaCsRbxDatatypeBindings.Wrap(cframe);
                case RbxColor3 color3:
                    return LuaCsRbxDatatypeBindings.Wrap(color3);
                case RbxUDim udim:
                    return LuaCsRbxDatatypeBindings.Wrap(udim);
                case RbxUDim2 udim2:
                    return LuaCsRbxDatatypeBindings.Wrap(udim2);
                case RbxEnumItem enumItem:
                    return LuaCsRbxDatatypeBindings.Wrap(enumItem);
                case LuaCsRbxNetworkTable table:
                    return BuildLuaTable(context, table);
                default:
                    return LuaValue.Nil;
            }
        }

        private JToken EncodeValue(LuaValue value, TraversalState state,
            HashSet<LuaTable> activeTables, int depth, string path)
        {
            switch (value.Type)
            {
                case LuaValueType.Nil:
                    return JValue.CreateNull();
                case LuaValueType.Boolean:
                    return new JValue(value.Read<bool>());
                case LuaValueType.Number:
                    return new JValue(value.Read<double>());
                case LuaValueType.String:
                    return new JValue(value.Read<string>());
                case LuaValueType.Function:
                    return JValue.CreateNull();
                case LuaValueType.Table:
                    return EncodeTable(
                        value.Read<LuaTable>(), state, activeTables, depth, path);
                default:
                    return EncodeUserData(value);
            }
        }

        private JToken EncodeTable(LuaTable table, TraversalState state,
            HashSet<LuaTable> activeTables, int depth, string path)
        {
            if (depth >= MaxNestingDepth)
            {
                throw RbxError.BadArgument(
                    "remote payload table nesting exceeds CoreAI's "
                    + MaxNestingDepth + " level limit at " + path,
                    "flatten the table before firing or invoking the remote");
            }

            if (!activeTables.Add(table))
            {
                throw RbxError.BadArgument(
                    "remote payload contains a cyclic table at " + path,
                    "remove the cycle before firing or invoking the remote");
            }

            try
            {
                List<KeyValuePair<LuaValue, LuaValue>> pairs = new();
                bool hasNumericKeys = false;
                bool hasOtherKeys = false;
                foreach (KeyValuePair<LuaValue, LuaValue> pair in table)
                {
                    state.ConsumeEntries(1, path);
                    pairs.Add(pair);
                    if (IsArrayIndex(pair.Key, out _))
                    {
                        hasNumericKeys = true;
                    }
                    else
                    {
                        hasOtherKeys = true;
                    }
                }

                if (hasNumericKeys && hasOtherKeys)
                {
                    throw RbxError.BadArgument(
                        "remote payload contains mixed numeric and non-numeric table keys at " + path,
                        "send either a contiguous array or a string-keyed dictionary");
                }

                return hasNumericKeys
                    ? EncodeArrayTable(pairs, state, activeTables, depth, path)
                    : EncodeDictionaryTable(pairs, state, activeTables, depth, path);
            }
            finally
            {
                activeTables.Remove(table);
            }
        }

        private JToken EncodeArrayTable(List<KeyValuePair<LuaValue, LuaValue>> pairs,
            TraversalState state, HashSet<LuaTable> activeTables, int depth, string path)
        {
            JToken[] ordered = new JToken[pairs.Count];
            for (int index = 0; index < pairs.Count; index++)
            {
                KeyValuePair<LuaValue, LuaValue> pair = pairs[index];
                if (!IsArrayIndex(pair.Key, out int arrayIndex)
                    || arrayIndex < 1
                    || arrayIndex > pairs.Count
                    || ordered[arrayIndex - 1] != null)
                {
                    throw RbxError.BadArgument(
                        "remote array keys must be unique contiguous indices 1..N at " + path,
                        "remove nil holes and non-contiguous numeric indices");
                }

                JToken encoded = EncodeValue(pair.Value, state, activeTables,
                    depth + 1, path + "[" + arrayIndex + "]");
                if (encoded.Type == JTokenType.Null)
                {
                    throw RbxError.BadArgument(
                        "remote array contains a nil or non-replicating value at "
                        + path + "[" + arrayIndex + "]",
                        "remove nil holes and functions from arrays");
                }

                ordered[arrayIndex - 1] = encoded;
            }

            JArray values = new();
            for (int index = 0; index < ordered.Length; index++)
            {
                if (ordered[index] == null)
                {
                    throw RbxError.BadArgument(
                        "remote array has a nil hole at " + path + "[" + (index + 1) + "]",
                        "use contiguous indices starting at 1");
                }

                values.Add(ordered[index]);
            }

            return new JObject
            {
                [TypeKey] = "table",
                ["kind"] = "array",
                ["values"] = values
            };
        }

        private JToken EncodeDictionaryTable(List<KeyValuePair<LuaValue, LuaValue>> pairs,
            TraversalState state, HashSet<LuaTable> activeTables, int depth, string path)
        {
            JObject values = new();
            for (int index = 0; index < pairs.Count; index++)
            {
                KeyValuePair<LuaValue, LuaValue> pair = pairs[index];
                string key = StringifyKey(pair.Key);
                values[key] = EncodeValue(pair.Value, state, activeTables,
                    depth + 1, path + "." + key);
            }

            return new JObject
            {
                [TypeKey] = "table",
                ["kind"] = "dictionary",
                ["values"] = values
            };
        }

        private JToken EncodeUserData(LuaValue value)
        {
            if (TryGetInstance(value, out LuaCsRbxInstanceProxy proxy))
            {
                InstanceIdWireContract.EnsureWireSafe(proxy.Instance.Id);
                return new JObject
                {
                    [TypeKey] = "Instance",
                    ["id"] = proxy.Instance.Id.Value.ToString(CultureInfo.InvariantCulture)
                };
            }

            if (!value.TryRead(out LuaCsRbxValueBox box))
            {
                return JValue.CreateNull();
            }

            switch (box.Value)
            {
                case RbxVector3 vector3:
                    return TaggedFloats("Vector3", vector3.X, vector3.Y, vector3.Z);
                case RbxVector2 vector2:
                    return TaggedFloats("Vector2", vector2.X, vector2.Y);
                case RbxColor3 color3:
                    return TaggedFloats("Color3", color3.R, color3.G, color3.B);
                case RbxUDim udim:
                    return new JObject
                    {
                        [TypeKey] = "UDim",
                        ["scale"] = udim.Scale,
                        ["offset"] = udim.Offset
                    };
                case RbxUDim2 udim2:
                    return new JObject
                    {
                        [TypeKey] = "UDim2",
                        ["xScale"] = udim2.X.Scale,
                        ["xOffset"] = udim2.X.Offset,
                        ["yScale"] = udim2.Y.Scale,
                        ["yOffset"] = udim2.Y.Offset
                    };
                case RbxCFrame cframe:
                    return TaggedFloats("CFrame", cframe.GetComponents());
                case RbxEnumItem enumItem:
                    return new JObject
                    {
                        [TypeKey] = "EnumItem",
                        ["enum"] = enumItem.EnumType.Name,
                        ["name"] = enumItem.Name,
                        ["value"] = enumItem.Value
                    };
                default:
                    return JValue.CreateNull();
            }
        }

        private object DecodeValue(JToken token, TraversalState state,
            int depth, string path)
        {
            switch (token.Type)
            {
                case JTokenType.Null:
                case JTokenType.Undefined:
                    return null;
                case JTokenType.Boolean:
                    return token.Value<bool>();
                case JTokenType.Integer:
                case JTokenType.Float:
                    return token.Value<double>();
                case JTokenType.String:
                    return token.Value<string>();
                case JTokenType.Object:
                    return DecodeTagged((JObject)token, state, depth, path);
                default:
                    throw RbxError.BadArgument(
                        "remote payload contains unsupported JSON token " + token.Type
                        + " at " + path,
                        "send only Roblox remote-compatible values");
            }
        }

        private object DecodeTagged(JObject tagged, TraversalState state,
            int depth, string path)
        {
            string type = tagged.Value<string>(TypeKey);
            switch (type)
            {
                case "table":
                    return DecodeTable(tagged, state, depth, path);
                case "Instance":
                    return DecodeInstance(tagged, path);
                case "Vector3":
                    return new RbxVector3(
                        ReadFloat(tagged, "values", 0),
                        ReadFloat(tagged, "values", 1),
                        ReadFloat(tagged, "values", 2));
                case "Vector2":
                    return new RbxVector2(
                        ReadFloat(tagged, "values", 0),
                        ReadFloat(tagged, "values", 1));
                case "Color3":
                    return new RbxColor3(
                        ReadFloat(tagged, "values", 0),
                        ReadFloat(tagged, "values", 1),
                        ReadFloat(tagged, "values", 2));
                case "UDim":
                    return new RbxUDim(tagged.Value<float>("scale"),
                        tagged.Value<int>("offset"));
                case "UDim2":
                    return new RbxUDim2(
                        new RbxUDim(tagged.Value<float>("xScale"),
                            tagged.Value<int>("xOffset")),
                        new RbxUDim(tagged.Value<float>("yScale"),
                            tagged.Value<int>("yOffset")));
                case "CFrame":
                    return DecodeCFrame(tagged);
                case "EnumItem":
                    return DecodeEnumItem(tagged);
                default:
                    throw RbxError.BadArgument(
                        "remote payload contains unknown Rbx tag '" + type + "' at " + path,
                        "send payloads through the matching CoreAI Rbx runtime version");
            }
        }

        private object DecodeTable(JObject tagged, TraversalState state,
            int depth, string path)
        {
            if (depth >= MaxNestingDepth)
            {
                throw RbxError.BadArgument(
                    "remote payload table nesting exceeds CoreAI's "
                    + MaxNestingDepth + " level limit at " + path,
                    "decode a shallower remote payload");
            }

            string kind = tagged.Value<string>("kind");
            JToken values = tagged["values"];
            if (kind == "array" && values is JArray array)
            {
                state.ConsumeEntries(array.Count, path);
                List<object> decoded = new(array.Count);
                for (int index = 0; index < array.Count; index++)
                {
                    decoded.Add(DecodeValue(array[index], state, depth + 1,
                        path + "[" + (index + 1) + "]"));
                }

                return new LuaCsRbxNetworkTable(decoded);
            }

            if (kind == "dictionary" && values is JObject dictionary)
            {
                state.ConsumeEntries(dictionary.Count, path);
                List<KeyValuePair<string, object>> decoded = new();
                foreach (JProperty property in dictionary.Properties())
                {
                    decoded.Add(new KeyValuePair<string, object>(property.Name,
                        DecodeValue(property.Value, state, depth + 1,
                            path + "." + property.Name)));
                }

                return new LuaCsRbxNetworkTable(decoded);
            }

            throw RbxError.BadArgument(
                "remote table tag has an invalid kind at " + path,
                "send payloads through the Rbx remote marshaller");
        }

        private object DecodeInstance(JObject tagged, string path)
        {
            string text = tagged.Value<string>("id");
            if (!ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture,
                    out ulong rawId))
            {
                throw RbxError.BadArgument(
                    "remote Instance id is invalid at " + path,
                    "send registered server-assigned Instances only");
            }

            InstanceId id = new(rawId);
            if (_registry.TryGet(id, out RbxInstance instance))
            {
                return instance;
            }

            _log?.Invoke("Remote payload InstanceId " + rawId
                         + " is not visible in the receiving registry; decoded as nil.");
            return null;
        }

        private RbxCFrame DecodeCFrame(JObject tagged)
        {
            float[] values = ReadFloatArray(tagged, "values", 12);
            return new RbxCFrame(
                values[0], values[1], values[2],
                values[3], values[4], values[5],
                values[6], values[7], values[8],
                values[9], values[10], values[11]);
        }

        private object DecodeEnumItem(JObject tagged)
        {
            string enumName = tagged.Value<string>("enum");
            string itemName = tagged.Value<string>("name");
            if (_enums.TryGet(enumName, out RbxEnum enumType)
                && enumType.TryGetItem(itemName, out RbxEnumItem item))
            {
                return item;
            }

            _log?.Invoke("Remote payload enum item Enum." + enumName + "." + itemName
                         + " is not registered; decoded as nil.");
            return null;
        }

        private LuaValue BuildLuaTable(LuaCsRbxModContext context,
            LuaCsRbxNetworkTable portable)
        {
            LuaTable table = new();
            if (portable.IsArray)
            {
                for (int index = 0; index < portable.ArrayValues.Count; index++)
                {
                    table[index + 1] = ToLuaValue(context, portable.ArrayValues[index]);
                }
            }
            else
            {
                for (int index = 0; index < portable.DictionaryValues.Count; index++)
                {
                    KeyValuePair<string, object> pair = portable.DictionaryValues[index];
                    table[pair.Key] = ToLuaValue(context, pair.Value);
                }
            }

            return new LuaValue(table);
        }

        private static bool IsArrayIndex(LuaValue key, out int index)
        {
            if (key.Type != LuaValueType.Number)
            {
                index = 0;
                return false;
            }

            double number = key.Read<double>();
            if (number < 1d || number > int.MaxValue || number != Math.Truncate(number))
            {
                index = 0;
                return false;
            }

            index = (int)number;
            return true;
        }

        private static string StringifyKey(LuaValue key)
        {
            if (key.Type == LuaValueType.String)
            {
                return key.Read<string>();
            }

            if (TryGetInstance(key, out LuaCsRbxInstanceProxy proxy))
            {
                return proxy.Instance.Name;
            }

            if (key.TryRead(out LuaCsRbxValueBox box))
            {
                return box.Value?.ToString() ?? "nil";
            }

            return key.ToString();
        }

        private static JObject TaggedFloats(string type, params float[] values)
        {
            JArray array = new();
            for (int index = 0; index < values.Length; index++)
            {
                array.Add(values[index]);
            }

            return new JObject
            {
                [TypeKey] = type,
                ["values"] = array
            };
        }

        private static float ReadFloat(JObject tagged, string property, int index)
        {
            return ReadFloatArray(tagged, property, index + 1)[index];
        }

        private static float[] ReadFloatArray(JObject tagged, string property, int minimumCount)
        {
            if (!(tagged[property] is JArray array) || array.Count < minimumCount)
            {
                throw RbxError.BadArgument(
                    "remote Rbx datatype tag has invalid components",
                    "send payloads through the matching CoreAI Rbx runtime version");
            }

            float[] values = new float[array.Count];
            for (int index = 0; index < array.Count; index++)
            {
                values[index] = array[index].Value<float>();
            }

            return values;
        }
    }
}
