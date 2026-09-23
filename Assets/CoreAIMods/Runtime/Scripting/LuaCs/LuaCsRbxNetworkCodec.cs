using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Replication;
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

        /// <summary>One step below the root in a diagnostic path: <c>.key</c> when a key is set, <c>[index]</c> otherwise.</summary>
        private readonly struct PathSegment
        {
            public PathSegment(string key, int index)
            {
                Key = key;
                Index = index;
            }

            public string Key { get; }

            public int Index { get; }
        }

        /// <summary>
        /// Per-call traversal bookkeeping: the aggregate entry budget, the location of the value being
        /// visited, and on the client path the sender whose view decides which Instances resolve.
        /// </summary>
        /// <remarks>
        /// WHY the location is a segment stack rendered only inside an error: building
        /// <c>path + "." + key</c> for every value made the cost of one payload its path length times its
        /// sibling count, so a single 64 KiB packet with a 30,000-character key over 4,400 siblings
        /// allocated about 268 MB on the server's main thread. The stack costs one struct per nesting
        /// level; the text an error reports is the same text the eager concatenation produced.
        /// </remarks>
        private sealed class TraversalState
        {
            private readonly List<PathSegment> _segments = new();
            private int _aggregateEntries;

            public TraversalState()
            {
            }

            public TraversalState(string clientSenderActorId, IReplicationFilter clientVisibility)
            {
                FromClient = true;
                ClientSenderActorId = clientSenderActorId;
                ClientVisibility = clientVisibility;
            }

            public bool FromClient { get; }

            public string ClientSenderActorId { get; }

            public IReplicationFilter ClientVisibility { get; }

            public int UnresolvedInstances { get; private set; }

            public ulong FirstUnresolvedInstanceId { get; private set; }

            public int UnresolvedEnumItems { get; private set; }

            public string FirstUnresolvedEnumName { get; private set; }

            public string FirstUnresolvedEnumItemName { get; private set; }

            public void ConsumeEntries(int count)
            {
                if (count < 0 || count > MaxAggregateEntries - _aggregateEntries)
                {
                    throw RbxError.BadArgument(
                        "remote payload exceeds CoreAI's " + MaxAggregateEntries
                        + " aggregate entry limit at " + DescribePath(),
                        "send fewer total arguments and table entries");
                }

                _aggregateEntries += count;
            }

            public void PushIndex(int index)
            {
                _segments.Add(new PathSegment(null, index));
            }

            public void PushKey(string key)
            {
                _segments.Add(new PathSegment(key ?? "", 0));
            }

            public void Pop()
            {
                _segments.RemoveAt(_segments.Count - 1);
            }

            public string DescribePath()
            {
                StringBuilder builder = new("$");
                for (int index = 0; index < _segments.Count; index++)
                {
                    PathSegment segment = _segments[index];
                    if (segment.Key != null)
                    {
                        builder.Append('.').Append(segment.Key);
                    }
                    else
                    {
                        builder.Append('[').Append(segment.Index).Append(']');
                    }
                }

                return builder.ToString();
            }

            public void RecordUnresolvedInstance(ulong rawId)
            {
                if (UnresolvedInstances == 0)
                {
                    FirstUnresolvedInstanceId = rawId;
                }

                UnresolvedInstances++;
            }

            public void RecordUnresolvedEnumItem(string enumName, string itemName)
            {
                if (UnresolvedEnumItems == 0)
                {
                    FirstUnresolvedEnumName = enumName;
                    FirstUnresolvedEnumItemName = itemName;
                }

                UnresolvedEnumItems++;
            }
        }

        /// <summary>
        /// Remembers a replication filter's answers for the duration of one decode.
        /// </summary>
        /// <remarks>
        /// WHY: <see cref="GuardedReplicationFilter"/> asks its inner filter about every ancestor, and
        /// the default filter walks to the root for each, so a payload naming N distinct instances d
        /// levels deep costs N x d^2 filter steps: about 170 ms for 2,000 references 60 levels down,
        /// paid by the server for one client packet. Decoding is synchronous and never mutates the tree,
        /// so an answer about a node cannot change within one payload; remembering it per node makes
        /// the cost N x d while the rule itself stays exactly the one replication applies.
        /// </remarks>
        private sealed class DecodeScopedVisibility : IReplicationFilter
        {
            private readonly IReplicationFilter _inner;
            private readonly Dictionary<InstanceId, bool> _answers = new();

            public DecodeScopedVisibility(IReplicationFilter inner)
            {
                _inner = inner;
            }

            public bool IsVisibleTo(string recipientActorId, RbxInstance instance)
            {
                if (instance == null)
                {
                    return _inner.IsVisibleTo(recipientActorId, null);
                }

                if (!_answers.TryGetValue(instance.Id, out bool visible))
                {
                    visible = _inner.IsVisibleTo(recipientActorId, instance);
                    _answers[instance.Id] = visible;
                }

                return visible;
            }

            public bool IsMemberVisibleTo(string recipientActorId, RbxInstance instance, string member)
            {
                return _inner.IsMemberVisibleTo(recipientActorId, instance, member);
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
        private readonly IReplicationFilter _clientVisibilityRule;
        private long _hiddenClientInstanceReferences;
        private long _hiddenClientReferencePayloads;

        /// <summary>
        /// Creates the codec over the receiving world. <paramref name="clientVisibility"/> is the
        /// replication filter that decides what a client may see; null means the default one. It is
        /// always applied under <see cref="GuardedReplicationFilter"/>'s floor, as replication applies it.
        /// </summary>
        public LuaCsRbxNetworkCodec(InstanceRegistry registry, RbxEnumRegistry enums,
            Action<string> log, IReplicationFilter clientVisibility = null)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _enums = enums ?? throw new ArgumentNullException(nameof(enums));
            _log = log;
            _clientVisibilityRule = clientVisibility is GuardedReplicationFilter guarded
                ? guarded.Inner
                : clientVisibility ?? DefaultReplicationFilter.Instance;
        }

        /// <summary>
        /// Instance references in client payloads that decoded as nil because the sender could not
        /// see them (unknown, destroyed, detached, server-only or another player's container).
        /// </summary>
        internal long HiddenClientInstanceReferences => _hiddenClientInstanceReferences;

        /// <summary>Client payloads that carried at least one such reference.</summary>
        internal long HiddenClientReferencePayloads => _hiddenClientReferencePayloads;

        /// <summary>Frozen byte cap for remote payloads; payloads larger than this are refused with PAYLOAD_TOO_LARGE before materializing the whole string.</summary>
        public const int MaxPayloadBytes = 65536;

        public byte[] EncodeArguments(IReadOnlyList<LuaValue> arguments)
        {
            JArray root = new();
            TraversalState state = new();
            HashSet<LuaTable> activeTables = new(new LuaTableReferenceComparer());
            int count = arguments?.Count ?? 0;
            state.ConsumeEntries(count);
            for (int index = 0; index < count; index++)
            {
                state.PushIndex(index);
                root.Add(EncodeValue(arguments[index], state, activeTables, 0));
                state.Pop();
            }

            string json = WriteEnvelope(root);
            byte[] bytes = Utf8.GetBytes(json);
            if (bytes.Length > MaxPayloadBytes)
            {
                throw new RbxError(RbxErrorCode.PayloadTooLarge,
                    "remote payload exceeds " + MaxPayloadBytes + " bytes (" + bytes.Length + " bytes)",
                    "send a smaller payload or split the request");
            }
            return bytes;
        }

        /// <summary>
        /// Decodes a payload from a trusted sender: the server's own FireClient/FireAllClients and
        /// InvokeClient arguments arriving at a client, an InvokeServer answer, or a host-local call.
        /// Every registered Instance id resolves.
        /// </summary>
        public object[] DecodeArguments(byte[] payload)
        {
            return Decode(payload, new TraversalState());
        }

        /// <summary>
        /// Decodes a payload the server received from a client: OnServerEvent and OnServerInvoke
        /// arguments, and the values an InvokeClient call returns. An Instance id resolves only when
        /// <paramref name="senderActorId"/> may see that instance under the replication filter; any
        /// other id (unknown, destroyed, detached, server-only, another player's Backpack or
        /// PlayerGui) decodes as nil.
        /// </summary>
        /// <remarks>
        /// WHY nil and not an error: Roblox's remote-events guide ("Non-replicated instances") says a
        /// value that is only visible to the sender "passes nil instead", and server handlers written
        /// for Roblox check for nil. WHY the replication filter decides: ids are sequential, so
        /// without it a modified client enumerating 1..N handed the server's own handlers a live
        /// ServerStorage object; the client can only legitimately name what replication showed it.
        /// </remarks>
        public object[] DecodeClientArguments(byte[] payload, string senderActorId)
        {
            GuardedReplicationFilter visibility = new(
                new DecodeScopedVisibility(_clientVisibilityRule), _registry);
            return Decode(payload, new TraversalState(senderActorId, visibility));
        }

        private object[] Decode(byte[] payload, TraversalState state)
        {
            byte[] checkedPayload = payload ?? Array.Empty<byte>();
            if (checkedPayload.Length > MaxPayloadBytes)
            {
                throw new RbxError(RbxErrorCode.PayloadTooLarge,
                    "remote payload exceeds " + MaxPayloadBytes + " bytes (" + checkedPayload.Length + " bytes)",
                    "send a smaller payload or split the request");
            }
            string json = Utf8.GetString(checkedPayload);
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
            state.ConsumeEntries(array.Count);
            for (int index = 0; index < array.Count; index++)
            {
                state.PushIndex(index);
                arguments[index] = DecodeValue(array[index], state, 0);
                state.Pop();
            }

            ReportUnresolvedValues(state);
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
            HashSet<LuaTable> activeTables, int depth)
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
                        value.Read<LuaTable>(), state, activeTables, depth);
                default:
                    return EncodeUserData(value);
            }
        }

        private JToken EncodeTable(LuaTable table, TraversalState state,
            HashSet<LuaTable> activeTables, int depth)
        {
            if (depth >= MaxNestingDepth)
            {
                throw RbxError.BadArgument(
                    "remote payload table nesting exceeds CoreAI's "
                    + MaxNestingDepth + " level limit at " + state.DescribePath(),
                    "flatten the table before firing or invoking the remote");
            }

            if (!activeTables.Add(table))
            {
                throw RbxError.BadArgument(
                    "remote payload contains a cyclic table at " + state.DescribePath(),
                    "remove the cycle before firing or invoking the remote");
            }

            try
            {
                List<KeyValuePair<LuaValue, LuaValue>> pairs = new();
                bool hasNumericKeys = false;
                bool hasOtherKeys = false;
                foreach (KeyValuePair<LuaValue, LuaValue> pair in table)
                {
                    state.ConsumeEntries(1);
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
                        "remote payload contains mixed numeric and non-numeric table keys at "
                        + state.DescribePath(),
                        "send either a contiguous array or a string-keyed dictionary");
                }

                return hasNumericKeys
                    ? EncodeArrayTable(pairs, state, activeTables, depth)
                    : EncodeDictionaryTable(pairs, state, activeTables, depth);
            }
            finally
            {
                activeTables.Remove(table);
            }
        }

        private JToken EncodeArrayTable(List<KeyValuePair<LuaValue, LuaValue>> pairs,
            TraversalState state, HashSet<LuaTable> activeTables, int depth)
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
                        "remote array keys must be unique contiguous indices 1..N at "
                        + state.DescribePath(),
                        "remove nil holes and non-contiguous numeric indices");
                }

                state.PushIndex(arrayIndex);
                JToken encoded = EncodeValue(pair.Value, state, activeTables, depth + 1);
                if (encoded.Type == JTokenType.Null)
                {
                    throw RbxError.BadArgument(
                        "remote array contains a nil or non-replicating value at "
                        + state.DescribePath(),
                        "remove nil holes and functions from arrays");
                }

                state.Pop();
                ordered[arrayIndex - 1] = encoded;
            }

            JArray values = new();
            for (int index = 0; index < ordered.Length; index++)
            {
                if (ordered[index] == null)
                {
                    throw RbxError.BadArgument(
                        "remote array has a nil hole at " + state.DescribePath()
                        + "[" + (index + 1) + "]",
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
            TraversalState state, HashSet<LuaTable> activeTables, int depth)
        {
            JObject values = new();
            for (int index = 0; index < pairs.Count; index++)
            {
                KeyValuePair<LuaValue, LuaValue> pair = pairs[index];
                string key = StringifyKey(pair.Key);
                state.PushKey(key);
                values[key] = EncodeValue(pair.Value, state, activeTables, depth + 1);
                state.Pop();
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

        private object DecodeValue(JToken token, TraversalState state, int depth)
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
                    return DecodeTagged((JObject)token, state, depth);
                default:
                    throw RbxError.BadArgument(
                        "remote payload contains unsupported JSON token " + token.Type
                        + " at " + state.DescribePath(),
                        "send only Roblox remote-compatible values");
            }
        }

        private object DecodeTagged(JObject tagged, TraversalState state, int depth)
        {
            string type = tagged.Value<string>(TypeKey);
            switch (type)
            {
                case "table":
                    return DecodeTable(tagged, state, depth);
                case "Instance":
                    return DecodeInstance(tagged, state);
                case "Vector3":
                {
                    float[] components = ReadFloatArray(tagged, "values", 3, state);
                    return new RbxVector3(components[0], components[1], components[2]);
                }
                case "Vector2":
                {
                    float[] components = ReadFloatArray(tagged, "values", 2, state);
                    return new RbxVector2(components[0], components[1]);
                }
                case "Color3":
                {
                    float[] components = ReadFloatArray(tagged, "values", 3, state);
                    return new RbxColor3(components[0], components[1], components[2]);
                }
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
                    return DecodeCFrame(tagged, state);
                case "EnumItem":
                    return DecodeEnumItem(tagged, state);
                default:
                    throw RbxError.BadArgument(
                        "remote payload contains unknown Rbx tag '" + type + "' at "
                        + state.DescribePath(),
                        "send payloads through the matching CoreAI Rbx runtime version");
            }
        }

        private object DecodeTable(JObject tagged, TraversalState state, int depth)
        {
            if (depth >= MaxNestingDepth)
            {
                throw RbxError.BadArgument(
                    "remote payload table nesting exceeds CoreAI's "
                    + MaxNestingDepth + " level limit at " + state.DescribePath(),
                    "decode a shallower remote payload");
            }

            string kind = tagged.Value<string>("kind");
            JToken values = tagged["values"];
            if (kind == "array" && values is JArray array)
            {
                state.ConsumeEntries(array.Count);
                List<object> decoded = new(array.Count);
                for (int index = 0; index < array.Count; index++)
                {
                    state.PushIndex(index + 1);
                    decoded.Add(DecodeValue(array[index], state, depth + 1));
                    state.Pop();
                }

                return new LuaCsRbxNetworkTable(decoded);
            }

            if (kind == "dictionary" && values is JObject dictionary)
            {
                state.ConsumeEntries(dictionary.Count);
                List<KeyValuePair<string, object>> decoded = new(dictionary.Count);
                foreach (JProperty property in dictionary.Properties())
                {
                    state.PushKey(property.Name);
                    object value = DecodeValue(property.Value, state, depth + 1);
                    state.Pop();
                    decoded.Add(new KeyValuePair<string, object>(property.Name, value));
                }

                return new LuaCsRbxNetworkTable(decoded);
            }

            throw RbxError.BadArgument(
                "remote table tag has an invalid kind at " + state.DescribePath(),
                "send payloads through the Rbx remote marshaller");
        }

        private object DecodeInstance(JObject tagged, TraversalState state)
        {
            string text = tagged.Value<string>("id");
            if (!ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture,
                    out ulong rawId))
            {
                throw RbxError.BadArgument(
                    "remote Instance id is invalid at " + state.DescribePath(),
                    "send registered server-assigned Instances only");
            }

            InstanceId id = new(rawId);
            if (_registry.TryGet(id, out RbxInstance instance)
                && (!state.FromClient
                    || state.ClientVisibility.IsVisibleTo(state.ClientSenderActorId, instance)))
            {
                return instance;
            }

            state.RecordUnresolvedInstance(rawId);
            return null;
        }

        private void ReportUnresolvedValues(TraversalState state)
        {
            int enumItems = state.UnresolvedEnumItems;
            if (enumItems > 0)
            {
                _log?.Invoke("Remote payload enum item Enum." + state.FirstUnresolvedEnumName + "."
                             + state.FirstUnresolvedEnumItemName + " is not registered; decoded as nil"
                             + CountSuffix(enumItems, "enum items") + ".");
            }

            int count = state.UnresolvedInstances;
            if (count == 0)
            {
                return;
            }

            if (!state.FromClient)
            {
                _log?.Invoke("Remote payload InstanceId " + state.FirstUnresolvedInstanceId
                             + " is not visible in the receiving registry; decoded as nil"
                             + CountSuffix(count, "Instance references") + ".");
                return;
            }

            _hiddenClientInstanceReferences += count;
            _hiddenClientReferencePayloads++;
            // WHY powers of two: the payloads are the sender's to repeat at will, so one line per
            // payload would let a client write the server's log at its own packet rate. Logging the
            // 1st, 2nd, 4th, 8th... occurrence keeps the first report immediate, needs no clock, and
            // bounds a flood of N payloads to about log2(N) lines; the counters keep the exact totals.
            if ((_hiddenClientReferencePayloads & (_hiddenClientReferencePayloads - 1)) == 0)
            {
                _log?.Invoke("Remote payload from actor '" + state.ClientSenderActorId
                             + "' named InstanceId " + state.FirstUnresolvedInstanceId
                             + ", which the sender cannot see; decoded as nil"
                             + CountSuffix(count, "Instance references")
                             + ". Client payloads decoded this way so far: "
                             + _hiddenClientReferencePayloads + ".");
            }
        }

        private static string CountSuffix(int count, string what)
        {
            return count > 1 ? " (" + count + " such " + what + " in this payload)" : "";
        }

        private RbxCFrame DecodeCFrame(JObject tagged, TraversalState state)
        {
            float[] values = ReadFloatArray(tagged, "values", 12, state);
            return new RbxCFrame(
                values[0], values[1], values[2],
                values[3], values[4], values[5],
                values[6], values[7], values[8],
                values[9], values[10], values[11]);
        }

        private object DecodeEnumItem(JObject tagged, TraversalState state)
        {
            string enumName = tagged.Value<string>("enum");
            string itemName = tagged.Value<string>("name");
            if (_enums.TryGet(enumName, out RbxEnum enumType)
                && enumType.TryGetItem(itemName, out RbxEnumItem item))
            {
                return item;
            }

            state.RecordUnresolvedEnumItem(enumName, itemName);
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

        /// <summary>
        /// Reads a datatype's component array once, counting every component against the aggregate
        /// entry budget like any other decoded value.
        /// </summary>
        private static float[] ReadFloatArray(JObject tagged, string property, int minimumCount,
            TraversalState state)
        {
            if (!(tagged[property] is JArray array) || array.Count < minimumCount)
            {
                throw RbxError.BadArgument(
                    "remote Rbx datatype tag has invalid components",
                    "send payloads through the matching CoreAI Rbx runtime version");
            }

            state.ConsumeEntries(array.Count);
            float[] values = new float[array.Count];
            for (int index = 0; index < array.Count; index++)
            {
                values[index] = array[index].Value<float>();
            }

            return values;
        }

        /// <summary>Renders the argument envelope exactly as <c>JToken.ToString(Formatting.None)</c> does, except for non-finite numbers.</summary>
        /// <remarks>
        /// WHY bare <c>NaN</c>/<c>Infinity</c>/<c>-Infinity</c> tokens: Roblox remotes carry numbers as
        /// numbers, so <c>0/0</c> and <c>math.huge</c> must arrive as numbers. Json.NET's default
        /// writes them as the STRINGS "NaN" and "Infinity", which a handler then fails to do arithmetic
        /// on. The decoder's reader (FloatParseHandling.Double) has always parsed the bare tokens back
        /// into doubles, so this spelling is readable by every receiver already deployed, where a new
        /// <c>$rbx</c> tag would make an older receiver refuse the whole payload as an unknown tag. It
        /// is also the spelling HttpService:JSONEncode (LuaCsRbxJson) uses for the same values.
        /// </remarks>
        private static string WriteEnvelope(JToken root)
        {
            using StringWriter writer = new(CultureInfo.InvariantCulture);
            using JsonTextWriter json = new(writer)
            {
                Formatting = Formatting.None,
                FloatFormatHandling = FloatFormatHandling.Symbol
            };
            root.WriteTo(json);
            json.Flush();
            return writer.ToString();
        }
    }
}
