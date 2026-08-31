using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using CoreAI.Mods.Rbx.Instances;
using Lua;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using static CoreAI.Ai.LuaCs.LuaCsRbxLua;

namespace CoreAI.Ai.LuaCs
{
    /// <summary>
    /// Shared Rbx table/JSON contract used by HttpService and future persistence surfaces. The offline
    /// mirror defines numeric-array priority for mixed tables, ignored string entries in that case,
    /// empty-table-to-array, non-finite numbers, and cyclic-table errors. OURS where the mirror is
    /// silent: sparse numeric tables emit JSON null holes; unsupported values/keys raise; nesting is
    /// capped at 64 tables; decode input and encode output are each capped at 1,000,000 UTF-16 code
    /// units; encode/decode are capped at 100,000 aggregate container entries or array slots; dictionary
    /// keys emit in ordinal order. The mirror documents buffer encoding through 50 MiB, but this bridge
    /// exposes no buffer value type and rejects unsupported values instead of claiming that compatibility.
    /// </summary>
    internal sealed class LuaCsRbxJson
    {
        internal const int MaxNestingDepth = 64;
        internal const int MaxInputCharacters = 1_000_000;
        internal const int MaxEncodedCharacters = 1_000_000;
        internal const int MaxAggregateEntries = 100_000;
        internal const int MaxArrayIndex = MaxAggregateEntries;

        private sealed class EncodeState
        {
            private readonly StringBuilder _output = new(256);
            private int _aggregateEntries;

            public string GetResult()
            {
                return _output.ToString();
            }

            public void ConsumeEntries(int count, string path)
            {
                if (count < 0 || count > MaxAggregateEntries - _aggregateEntries)
                {
                    throw RbxError.BadArgument(
                        "HttpService:JSONEncode exceeds CoreAI's "
                        + MaxAggregateEntries + " aggregate entry limit at " + path,
                        "encode fewer total object entries and array slots");
                }

                _aggregateEntries += count;
            }

            public void Append(char value, string path)
            {
                EnsureOutputCapacity(1, path);
                _output.Append(value);
            }

            public void Append(string value, string path)
            {
                int length = value?.Length ?? 0;
                EnsureOutputCapacity(length, path);
                _output.Append(value);
            }

            public void AppendJsonString(string value, string path)
            {
                int sourceLength = value?.Length ?? 0;
                EnsureOutputCapacity(sourceLength, path);
                string encoded = JsonConvert.ToString(value);
                Append(encoded, path);
            }

            private void EnsureOutputCapacity(int additionalCharacters, string path)
            {
                if (additionalCharacters < 0
                    || additionalCharacters > MaxEncodedCharacters - _output.Length)
                {
                    throw RbxError.BadArgument(
                        "HttpService:JSONEncode output exceeds CoreAI's "
                        + MaxEncodedCharacters + " UTF-16 code-unit limit at " + path,
                        "encode a smaller value");
                }
            }
        }

        private sealed class DecodeState
        {
            private int _aggregateEntries;

            public void ConsumeEntries(int count, string path)
            {
                if (count < 0 || count > MaxAggregateEntries - _aggregateEntries)
                {
                    throw RbxError.BadArgument(
                        "HttpService:JSONDecode exceeds CoreAI's "
                        + MaxAggregateEntries + " aggregate entry limit at " + path,
                        "decode fewer total object entries and array slots");
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

        public string Encode(LuaValue input)
        {
            EncodeState state = new();
            HashSet<LuaTable> activeTables = new(new LuaTableReferenceComparer());
            EncodeValue(input, state, activeTables, 0, "$");
            return state.GetResult();
        }

        public LuaValue Decode(string input)
        {
            if (input == null)
            {
                throw RbxError.BadArgument(
                    "HttpService:JSONDecode expects a string",
                    "pass a valid JSON string");
            }

            if (input.Length > MaxInputCharacters)
            {
                throw RbxError.BadArgument(
                    "JSON input exceeds CoreAI's " + MaxInputCharacters + " character limit",
                    "decode a smaller JSON document");
            }

            try
            {
                using StringReader stringReader = new(input);
                using JsonTextReader reader = new(stringReader)
                {
                    DateParseHandling = DateParseHandling.None,
                    FloatParseHandling = FloatParseHandling.Double,
                    MaxDepth = MaxNestingDepth
                };
                JToken token = JToken.ReadFrom(reader);
                if (reader.Read())
                {
                    throw new JsonReaderException("Additional text follows the JSON value.");
                }

                DecodeState state = new();
                return DecodeValue(token, state, 0, "$");
            }
            catch (RbxError)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw RbxError.BadArgument(
                    "HttpService:JSONDecode received invalid JSON: " + ex.Message,
                    "pass one valid JSON value within the CoreAI depth and size limits");
            }
        }

        private static void EncodeValue(LuaValue value, EncodeState state,
            HashSet<LuaTable> activeTables, int depth, string path)
        {
            switch (value.Type)
            {
                case LuaValueType.Nil:
                    state.Append("null", path);
                    return;
                case LuaValueType.Boolean:
                    state.Append(value.Read<bool>() ? "true" : "false", path);
                    return;
                case LuaValueType.Number:
                    EncodeNumber(value.Read<double>(), state, path);
                    return;
                case LuaValueType.String:
                    state.AppendJsonString(value.Read<string>(), path);
                    return;
                case LuaValueType.Table:
                    EncodeTable(value.Read<LuaTable>(), state, activeTables, depth, path);
                    return;
                default:
                    // WHY: OURS: the mirror does not pin error-vs-null for unsupported values;
                    // failing loudly prevents accidental data loss in stores and request bodies.
                    throw RbxError.BadArgument(
                        "HttpService:JSONEncode cannot encode " + Describe(value)
                        + " at " + path,
                        "encode only nil, boolean, number, string, and supported tables");
            }
        }

        private static void EncodeNumber(double number, EncodeState state, string path)
        {
            if (double.IsNaN(number))
            {
                state.Append("NaN", path);
                return;
            }

            if (double.IsPositiveInfinity(number))
            {
                state.Append("Infinity", path);
                return;
            }

            if (double.IsNegativeInfinity(number))
            {
                state.Append("-Infinity", path);
                return;
            }

            state.Append(number.ToString("R", CultureInfo.InvariantCulture), path);
        }

        private static void EncodeTable(LuaTable table, EncodeState state,
            HashSet<LuaTable> activeTables, int depth, string path)
        {
            if (depth >= MaxNestingDepth)
            {
                // WHY: OURS: the mirror specifies no depth; a fixed bound prevents a single host
                // call from consuming an unbounded native stack in player and WebGL builds.
                throw RbxError.BadArgument(
                    "HttpService:JSONEncode table nesting exceeds CoreAI's "
                    + MaxNestingDepth + " level limit at " + path,
                    "flatten the table before encoding it");
            }

            if (!activeTables.Add(table))
            {
                throw RbxError.BadArgument(
                    "HttpService:JSONEncode found a cyclic table at " + path,
                    "remove the cycle before encoding");
            }

            try
            {
                Dictionary<int, LuaValue> numericValues = new();
                List<KeyValuePair<string, LuaValue>> stringValues = new();
                bool hasNumericKeys = false;
                foreach (KeyValuePair<LuaValue, LuaValue> pair in table)
                {
                    state.ConsumeEntries(1, path);
                    if (pair.Key.Type == LuaValueType.Number)
                    {
                        int index = ReadArrayIndex(pair.Key, path);
                        numericValues.Add(index, pair.Value);
                        hasNumericKeys = true;
                    }
                    else if (pair.Key.Type == LuaValueType.String)
                    {
                        stringValues.Add(new KeyValuePair<string, LuaValue>(
                            pair.Key.Read<string>(), pair.Value));
                    }
                    else
                    {
                        // WHY: OURS: only string/number keys are documented; rejecting every other
                        // key type is deterministic and does not silently stringify identities.
                        throw RbxError.BadArgument(
                            "HttpService:JSONEncode table key at " + path
                            + " must be a string or positive integer",
                            "use a string-keyed dictionary or positive-integer array");
                    }
                }

                if (hasNumericKeys)
                {
                    EncodeArray(numericValues, state, activeTables, depth, path);
                    return;
                }

                if (stringValues.Count == 0)
                {
                    state.Append("[]", path);
                    return;
                }

                EncodeObject(stringValues, state, activeTables, depth, path);
            }
            finally
            {
                activeTables.Remove(table);
            }
        }

        private static int ReadArrayIndex(LuaValue key, string path)
        {
            double number = key.Read<double>();
            if (double.IsNaN(number) || double.IsInfinity(number)
                || number < 1d || number > MaxArrayIndex || number != Math.Truncate(number))
            {
                throw RbxError.BadArgument(
                    "HttpService:JSONEncode numeric key at " + path
                    + " must be an integer in 1.." + MaxArrayIndex,
                    "use compact positive-integer array indices");
            }

            return (int)number;
        }

        private static void EncodeArray(Dictionary<int, LuaValue> numericValues,
            EncodeState state, HashSet<LuaTable> activeTables, int depth, string path)
        {
            int highestIndex = 0;
            foreach (int index in numericValues.Keys)
            {
                highestIndex = Math.Max(highestIndex, index);
            }

            state.ConsumeEntries(highestIndex - numericValues.Count, path);
            state.Append('[', path);
            for (int index = 1; index <= highestIndex; index++)
            {
                if (index > 1)
                {
                    state.Append(',', path);
                }

                if (numericValues.TryGetValue(index, out LuaValue item))
                {
                    EncodeValue(item, state, activeTables, depth + 1,
                        path + "[" + index + "]");
                }
                else
                {
                    // WHY: OURS: the mirror only says to avoid nil holes; emitting null preserves
                    // every later numeric index and decodes back to the corresponding Lua hole.
                    state.Append("null", path + "[" + index + "]");
                }
            }

            state.Append(']', path);
        }

        private static void EncodeObject(List<KeyValuePair<string, LuaValue>> values,
            EncodeState state, HashSet<LuaTable> activeTables, int depth, string path)
        {
            values.Sort((KeyValuePair<string, LuaValue> left,
                KeyValuePair<string, LuaValue> right) =>
                string.CompareOrdinal(left.Key, right.Key));
            state.Append('{', path);
            for (int index = 0; index < values.Count; index++)
            {
                if (index > 0)
                {
                    state.Append(',', path);
                }

                KeyValuePair<string, LuaValue> pair = values[index];
                state.AppendJsonString(pair.Key, path);
                state.Append(':', path);
                EncodeValue(pair.Value, state, activeTables, depth + 1,
                    path + "." + pair.Key);
            }

            state.Append('}', path);
        }

        private static LuaValue DecodeValue(JToken token, DecodeState state,
            int depth, string path)
        {
            if (token == null || token.Type == JTokenType.Null
                              || token.Type == JTokenType.Undefined)
            {
                return LuaValue.Nil;
            }

            switch (token.Type)
            {
                case JTokenType.Boolean:
                    return new LuaValue(token.Value<bool>());
                case JTokenType.Integer:
                case JTokenType.Float:
                    return new LuaValue(token.Value<double>());
                case JTokenType.String:
                    return new LuaValue(token.Value<string>());
                case JTokenType.Array:
                    return DecodeArray((JArray)token, state, depth, path);
                case JTokenType.Object:
                    return DecodeObject((JObject)token, state, depth, path);
                default:
                    throw RbxError.BadArgument(
                        "HttpService:JSONDecode does not support JSON token "
                        + token.Type + " at " + path,
                        "decode JSON null, boolean, number, string, array, or object values");
            }
        }

        private static LuaValue DecodeArray(JArray array, DecodeState state,
            int depth, string path)
        {
            EnsureDecodeDepth(depth, path);
            state.ConsumeEntries(array.Count, path);
            LuaTable table = new();
            for (int index = 0; index < array.Count; index++)
            {
                LuaValue value = DecodeValue(array[index], state, depth + 1,
                    path + "[" + (index + 1) + "]");
                if (value.Type != LuaValueType.Nil)
                {
                    table[index + 1] = value;
                }
            }

            return new LuaValue(table);
        }

        private static LuaValue DecodeObject(JObject obj, DecodeState state,
            int depth, string path)
        {
            EnsureDecodeDepth(depth, path);
            state.ConsumeEntries(obj.Count, path);
            LuaTable table = new();
            foreach (JProperty property in obj.Properties())
            {
                LuaValue value = DecodeValue(property.Value, state, depth + 1,
                    path + "." + property.Name);
                if (value.Type != LuaValueType.Nil)
                {
                    table[property.Name] = value;
                }
            }

            return new LuaValue(table);
        }

        private static void EnsureDecodeDepth(int depth, string path)
        {
            if (depth >= MaxNestingDepth)
            {
                throw RbxError.BadArgument(
                    "HttpService:JSONDecode nesting exceeds CoreAI's "
                    + MaxNestingDepth + " level limit at " + path,
                    "decode a shallower JSON document");
            }
        }
    }
}
