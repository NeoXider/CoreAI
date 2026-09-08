using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Infrastructure.Logging;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Sandbox.LuaCs;
using Lua;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.LuaBindings
{
    /// <summary>
    /// Closes MVP2 §5.2.9 item 12 through the REAL mod runtime (real Lua, not direct C# calls):
    /// round-trips <c>HttpService:JSONEncode</c>/<c>JSONDecode</c> against the contract fixtures the
    /// mirror pins (arrays, dictionaries, nesting, the empty table, booleans, nil/null, negative and
    /// fractional numbers, unicode, escaping), and then differentially compares
    /// <see cref="LuaCsRbxJson"/> (HttpService) against <see cref="LuaCsRbxNetworkCodec"/> (remotes) on
    /// every fixture.
    /// WHY: the criterion as written also claims the two are "the same component (reference equality of
    /// the serializer instance)". They are not, and reading both encoders end to end shows they cannot be
    /// made to be without an observable behavior change: the remote codec wraps every table (and every
    /// Rbx datatype/Instance) in an <c>$rbx</c> envelope so a receiving VM can rebuild non-JSON Lua
    /// values, frames its root as an argument LIST rather than a single value, throws on table shapes
    /// (mixed keys, sparse arrays) that the JSON encoder tolerates, and formats numbers differently
    /// (Json.NET always renders a decimal point for floats). None of that is something HTTP JSON sent to
    /// a third-party service may carry. So this file does NOT unify them — it pins the agreement that
    /// does exist (finite non-integer number formatting, string escaping) and the divergence that must
    /// stay (envelope shape, integer formatting, NaN/Infinity spelling, key-shape strictness) as an
    /// explicit, regression-tested contract instead of an unproven or false claim.
    /// </summary>
    [TestFixture]
    public sealed class RbxJsonContractEditModeTests
    {
        private SynchronizationContext _savedContext;

        /// <summary>Same sync-over-async hazard as LuaCsModRuntimeEditModeTests: detach Unity's
        /// SynchronizationContext so VM continuations complete on the thread pool.</summary>
        [SetUp]
        public void DetachSynchronizationContext()
        {
            _savedContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
        }

        [TearDown]
        public void RestoreSynchronizationContext()
        {
            SynchronizationContext.SetSynchronizationContext(_savedContext);
        }

        private sealed class MemoryStore : ILuaModStore
        {
            private readonly Dictionary<(string ModId, string Key), string> _values = new();

            public string Get(string modId, string key)
            {
                return _values.TryGetValue((modId, key), out string value) ? value : "";
            }

            public void Set(string modId, string key, string value)
            {
                if (value == null)
                {
                    _values.Remove((modId, key));
                    return;
                }

                _values[(modId, key)] = value;
            }

            public void Clear(string modId)
            {
                List<(string ModId, string Key)> keys = new();
                foreach ((string storedModId, string key) in _values.Keys)
                {
                    if (storedModId == modId)
                    {
                        keys.Add((storedModId, key));
                    }
                }

                foreach ((string ModId, string Key) key in keys)
                {
                    _values.Remove(key);
                }
            }
        }

        private sealed class FakeGameLogger : IGameLogger
        {
            public void LogDebug(GameLogFeature feature, string message, UnityEngine.Object context = null)
            {
            }

            public void LogInfo(GameLogFeature feature, string message, UnityEngine.Object context = null)
            {
            }

            public void LogWarning(GameLogFeature feature, string message, UnityEngine.Object context = null)
            {
            }

            public void LogError(GameLogFeature feature, string message, UnityEngine.Object context = null)
            {
            }
        }

        private static LuaCsModStack BuildStack(MemoryStore store)
        {
            LuaCsRbxApiBindings roblox = new();
            return LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
            {
                Logger = new FakeGameLogger(),
                ModStore = store,
                Capabilities = LuaCapabilities.All,
                OneOffCapabilities = LuaCapabilities.All,
                RbxApi = roblox
            });
        }

        /// <summary>
        /// Builds a stack that additionally exposes <c>network_encode</c>/<c>network_argument_count</c> —
        /// test-only Lua globals over a standalone <see cref="LuaCsRbxNetworkCodec"/> instance.
        /// WHY: the remote codec has no <c>JSONEncode</c>-shaped Lua entry point of its own (it only runs
        /// implicitly inside RemoteEvent Fire/Invoke); these globals give the differential tests a
        /// same-shaped call so a real-Lua-built fixture can be fed through both encoders fairly.
        /// <c>network_encode</c> strips the one-element array framing <see cref="LuaCsRbxNetworkCodec.EncodeArguments"/>
        /// always produces (it encodes an argument LIST, not a single value) so what remains is exactly
        /// the per-value encoding, comparable against <c>HttpService:JSONEncode</c>'s bare output.
        /// </summary>
        private static LuaCsModStack BuildStackWithNetworkCodec(MemoryStore store)
        {
            LuaCsRbxApiBindings roblox = new();
            LuaCsRbxNetworkCodec codec = new(
                new InstanceRegistry(), RbxEnumRegistry.CreateWithBuiltins(), null);

            return LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
            {
                Logger = new FakeGameLogger(),
                ModStore = store,
                Capabilities = LuaCapabilities.All,
                OneOffCapabilities = LuaCapabilities.All,
                RbxApi = roblox,
                AdditionalGameplayBindings = (registry, _) =>
                {
                    registry.RegisterCallback("network_encode", (ctx, _) =>
                    {
                        LuaValue value = ctx.GetArgument(0);
                        byte[] bytes = codec.EncodeArguments(new[] { value });
                        string wrapped = Encoding.UTF8.GetString(bytes);
                        string inner = wrapped.Substring(1, wrapped.Length - 2);
                        return new ValueTask<int>(ctx.Return(inner));
                    });
                    registry.RegisterCallback("network_argument_count", (ctx, _) =>
                    {
                        string json = ctx.GetArgument(0).Read<string>();
                        byte[] bytes = Encoding.UTF8.GetBytes(json);
                        object[] decoded = codec.DecodeArguments(bytes);
                        return new ValueTask<int>(ctx.Return(new LuaValue((double)decoded.Length)));
                    });
                }
            });
        }

        // ---------------------------------------------------------------------------------------
        // Round-trip contract fixtures (HttpService only) — fail without a working JSONEncode/Decode.
        // ---------------------------------------------------------------------------------------

        [Test]
        public void RoundTrip_Array_EncodesAndDecodesInOrder()
        {
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(store);

            stack.Runtime.LoadMod("m", @"
                local http = game:GetService('HttpService')
                local json = http:JSONEncode({10, 20, 30})
                local back = http:JSONDecode(json)
                store_set('json', json)
                store_set('v1', tostring(back[1]))
                store_set('v2', tostring(back[2]))
                store_set('v3', tostring(back[3]))");

            Assert.AreEqual("[10,20,30]", store.Get("m", "json"));
            Assert.AreEqual("10", store.Get("m", "v1"));
            Assert.AreEqual("20", store.Get("m", "v2"));
            Assert.AreEqual("30", store.Get("m", "v3"));
        }

        [Test]
        public void RoundTrip_StringKeyedDictionary_SortsKeysOrdinallyAndDecodesBack()
        {
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(store);

            stack.Runtime.LoadMod("m", @"
                local http = game:GetService('HttpService')
                local json = http:JSONEncode({name = 'Alice', age = 30})
                local back = http:JSONDecode(json)
                store_set('json', json)
                store_set('name', tostring(back.name))
                store_set('age', tostring(back.age))");

            // WHY: OURS — the mirror does not pin key order; LuaCsRbxJson sorts string keys ordinally
            // (documented on the class), so 'age' < 'name' deterministically.
            Assert.AreEqual("{\"age\":30,\"name\":\"Alice\"}", store.Get("m", "json"));
            Assert.AreEqual("Alice", store.Get("m", "name"));
            Assert.AreEqual("30", store.Get("m", "age"));
        }

        [Test]
        public void RoundTrip_NestedTables_PreservesShapeThroughEncodeDecode()
        {
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(store);

            stack.Runtime.LoadMod("m", @"
                local http = game:GetService('HttpService')
                local fixture = { a = {1, 2}, list = { {x = 1}, {x = 2} } }
                local back = http:JSONDecode(http:JSONEncode(fixture))
                store_set('a1', tostring(back.a[1]))
                store_set('a2', tostring(back.a[2]))
                store_set('list1x', tostring(back.list[1].x))
                store_set('list2x', tostring(back.list[2].x))");

            Assert.AreEqual("1", store.Get("m", "a1"));
            Assert.AreEqual("2", store.Get("m", "a2"));
            Assert.AreEqual("1", store.Get("m", "list1x"));
            Assert.AreEqual("2", store.Get("m", "list2x"));
        }

        [Test]
        public void RoundTrip_EmptyTable_EncodesAsEmptyJsonArray()
        {
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(store);

            // WHY: mirror pins this exactly — "An empty Luau table ({}) generates an empty JSON array
            // ([])." — the classic {} vs [] ambiguity is resolved here, not left implicit.
            stack.Runtime.LoadMod("m", @"
                local http = game:GetService('HttpService')
                store_set('json', http:JSONEncode({}))");

            Assert.AreEqual("[]", store.Get("m", "json"));
        }

        [Test]
        public void RoundTrip_EmptyJsonObject_DecodesToEmptyTable()
        {
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(store);

            // WHY: mirror pins this exactly — "An empty JSON object generates an empty Luau table ({})."
            stack.Runtime.LoadMod("m", @"
                local http = game:GetService('HttpService')
                local back = http:JSONDecode('{}')
                store_set('is_empty', tostring(next(back) == nil))");

            Assert.AreEqual("true", store.Get("m", "is_empty"));
        }

        [Test]
        public void RoundTrip_Booleans_EncodeAsBareTokensAndDecodeBack()
        {
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(store);

            stack.Runtime.LoadMod("m", @"
                local http = game:GetService('HttpService')
                local json = http:JSONEncode({true, false})
                local back = http:JSONDecode(json)
                store_set('json', json)
                store_set('v1', tostring(back[1]))
                store_set('v2', tostring(back[2]))");

            Assert.AreEqual("[true,false]", store.Get("m", "json"));
            Assert.AreEqual("true", store.Get("m", "v1"));
            Assert.AreEqual("false", store.Get("m", "v2"));
        }

        [Test]
        public void RoundTrip_TopLevelNil_EncodesToNullAndBackToNil()
        {
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(store);

            stack.Runtime.LoadMod("m", @"
                local http = game:GetService('HttpService')
                local json = http:JSONEncode(nil)
                local back = http:JSONDecode(json)
                store_set('json', json)
                store_set('back_is_nil', tostring(back == nil))");

            Assert.AreEqual("null", store.Get("m", "json"));
            Assert.AreEqual("true", store.Get("m", "back_is_nil"));
        }

        [Test]
        public void RoundTrip_JsonNullInsideArray_DecodesAsAbsentHoleNotStoredNil()
        {
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(store);

            // WHY: OURS — a Lua table cannot hold a stored nil value; the class doc says decode drops a
            // null entry rather than inventing a sentinel. Index 2 must therefore be absent, not merely
            // "equal to nil" as a distinct stored marker.
            stack.Runtime.LoadMod("m", @"
                local http = game:GetService('HttpService')
                local back = http:JSONDecode('[1,null,3]')
                store_set('v1', tostring(back[1]))
                store_set('v2_is_nil', tostring(back[2] == nil))
                store_set('v3', tostring(back[3]))");

            Assert.AreEqual("1", store.Get("m", "v1"));
            Assert.AreEqual("true", store.Get("m", "v2_is_nil"));
            Assert.AreEqual("3", store.Get("m", "v3"));
        }

        [Test]
        public void RoundTrip_NegativeAndFractionalNumbers_EncodeAndDecodeExactly()
        {
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(store);

            stack.Runtime.LoadMod("m", @"
                local http = game:GetService('HttpService')
                local json = http:JSONEncode({-3.5, 0.1, -100})
                local back = http:JSONDecode(json)
                store_set('json', json)
                store_set('v1', tostring(back[1]))
                store_set('v2', tostring(back[2]))
                store_set('v3', tostring(back[3]))");

            Assert.AreEqual("[-3.5,0.1,-100]", store.Get("m", "json"));
            Assert.AreEqual("-3.5", store.Get("m", "v1"));
            Assert.AreEqual("0.1", store.Get("m", "v2"));
            Assert.AreEqual("-100", store.Get("m", "v3"));
        }

        [Test]
        public void RoundTrip_UnicodeStrings_SurviveEncodeDecodeUnescaped()
        {
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(store);

            stack.Runtime.LoadMod("m", @"
                local http = game:GetService('HttpService')
                local s = 'unicode: caf\195\169 \228\184\173\230\150\135 \240\159\152\128'
                local json = http:JSONEncode({text = s})
                local back = http:JSONDecode(json)
                store_set('json', json)
                store_set('roundtrip_ok', tostring(back.text == s))");

            // WHY: Newtonsoft's default string escaping does not \u-escape non-ASCII characters, so the
            // raw UTF-8 bytes (café, 中文, the emoji U+1F600) appear literally in the JSON text.
            StringAssert.Contains("café 中文 😀", store.Get("m", "json"));
            Assert.AreEqual("true", store.Get("m", "roundtrip_ok"));
        }

        [Test]
        public void RoundTrip_Escaping_QuoteBackslashNewlineTab()
        {
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(store);

            // WHY: built via string.char to avoid ambiguous nested-quote escaping in the embedded Lua
            // source; the resulting runtime string is a"b\c<LF>d<TAB>e.
            stack.Runtime.LoadMod("m", @"
                local http = game:GetService('HttpService')
                local s = 'a' .. string.char(34) .. 'b' .. string.char(92)
                    .. 'c' .. string.char(10) .. 'd' .. string.char(9) .. 'e'
                local json = http:JSONEncode(s)
                local back = http:JSONDecode(json)
                store_set('json', json)
                store_set('roundtrip_ok', tostring(back == s))");

            string json = store.Get("m", "json");
            StringAssert.Contains("\\\"", json);
            StringAssert.Contains("\\\\", json);
            StringAssert.Contains("\\n", json);
            StringAssert.Contains("\\t", json);
            Assert.AreEqual("true", store.Get("m", "roundtrip_ok"));
        }

        // ---------------------------------------------------------------------------------------
        // Differential fixtures: HttpService (LuaCsRbxJson) vs the remote codec
        // (LuaCsRbxNetworkCodec), same real-Lua-built value through both. Pins agreement where it
        // exists and pins the (unavoidable) divergence everywhere else — see the class doc.
        // ---------------------------------------------------------------------------------------

        [Test]
        public void Differential_EmptyTable_HttpBareArray_NetworkTaggedDictionaryEnvelope()
        {
            MemoryStore store = new();
            LuaCsModStack stack = BuildStackWithNetworkCodec(store);

            stack.Runtime.LoadMod("m", @"
                local http = game:GetService('HttpService')
                store_set('http_json', http:JSONEncode({}))
                store_set('net_json', network_encode({}))");

            Assert.AreEqual("[]", store.Get("m", "http_json"));
            // WHY: the network codec cannot emit a bare [] for an empty table — it must record that this
            // was a Lua table (not a JSON array literal an external caller sent) so the receiving VM
            // rebuilds a table rather than treating the value as already-decoded JSON. An empty table has
            // no numeric keys, so it takes the dictionary branch.
            Assert.AreEqual("{\"$rbx\":\"table\",\"kind\":\"dictionary\",\"values\":{}}",
                store.Get("m", "net_json"));
        }

        [Test]
        public void Differential_PlainArray_NetworkWrapsInRbxEnvelope_HttpDoesNot()
        {
            MemoryStore store = new();
            LuaCsModStack stack = BuildStackWithNetworkCodec(store);

            stack.Runtime.LoadMod("m", @"
                local http = game:GetService('HttpService')
                local arr = {1, 2, 3}
                store_set('http_json', http:JSONEncode(arr))
                store_set('net_json', network_encode(arr))");

            Assert.AreEqual("[1,2,3]", store.Get("m", "http_json"));
            Assert.AreEqual(
                "{\"$rbx\":\"table\",\"kind\":\"array\",\"values\":[1.0,2.0,3.0]}",
                store.Get("m", "net_json"));
        }

        [Test]
        public void Differential_SingleKeyDictionary_NetworkWrapsInRbxEnvelope_HttpDoesNot()
        {
            MemoryStore store = new();
            LuaCsModStack stack = BuildStackWithNetworkCodec(store);

            stack.Runtime.LoadMod("m", @"
                local http = game:GetService('HttpService')
                local dict = {name = 'Alice'}
                store_set('http_json', http:JSONEncode(dict))
                store_set('net_json', network_encode(dict))");

            Assert.AreEqual("{\"name\":\"Alice\"}", store.Get("m", "http_json"));
            Assert.AreEqual(
                "{\"$rbx\":\"table\",\"kind\":\"dictionary\",\"values\":{\"name\":\"Alice\"}}",
                store.Get("m", "net_json"));
        }

        [Test]
        public void Differential_MixedNumericAndStringKeys_HttpSilentlyDropsStrings_NetworkThrows()
        {
            MemoryStore store = new();
            LuaCsModStack stack = BuildStackWithNetworkCodec(store);

            stack.Runtime.LoadMod("m", @"
                local http = game:GetService('HttpService')
                local mixed = {10, 20, extra = 'dropped'}
                store_set('http_json', http:JSONEncode(mixed))
                local netOk, netErr = pcall(function() return network_encode(mixed) end)
                store_set('net_ok', tostring(netOk))
                store_set('net_err', tostring(netErr))");

            // WHY: matches the mirror ("array takes priority, string keys are ignored") — real Roblox
            // behavior, not a CoreAI deviation.
            Assert.AreEqual("[10,20]", store.Get("m", "http_json"));
            // WHY: OURS — the remote codec refuses instead of silently dropping data, because a dropped
            // field crossing the network is a much worse failure mode (a client silently missing state)
            // than refusing the call outright.
            Assert.AreEqual("false", store.Get("m", "net_ok"));
            StringAssert.Contains(
                "mixed numeric and non-numeric table keys", store.Get("m", "net_err"));
        }

        [Test]
        public void Differential_SparseArray_HttpNullPadsHoles_NetworkThrows()
        {
            MemoryStore store = new();
            LuaCsModStack stack = BuildStackWithNetworkCodec(store);

            stack.Runtime.LoadMod("m", @"
                local http = game:GetService('HttpService')
                local sparse = {}
                sparse[1] = 1
                sparse[3] = 3
                store_set('http_json', http:JSONEncode(sparse))
                local netOk, netErr = pcall(function() return network_encode(sparse) end)
                store_set('net_ok', tostring(netOk))
                store_set('net_err', tostring(netErr))");

            // WHY: OURS — the JSON encoder null-pads a numeric hole (documented on the class) so later
            // indices are preserved.
            Assert.AreEqual("[1,null,3]", store.Get("m", "http_json"));
            // WHY: OURS — the remote codec refuses a non-contiguous array outright rather than inventing
            // a null hole, since a silently-inserted null crossing the network is observably different
            // from "index 2 was never set" once it reaches the other side.
            Assert.AreEqual("false", store.Get("m", "net_ok"));
            StringAssert.Contains(
                "remote array keys must be unique contiguous indices 1..N", store.Get("m", "net_err"));
        }

        [Test]
        public void Differential_WholeNumber_HttpOmitsDecimalPoint_NetworkAlwaysAppendsIt()
        {
            MemoryStore store = new();
            LuaCsModStack stack = BuildStackWithNetworkCodec(store);

            stack.Runtime.LoadMod("m", @"
                local http = game:GetService('HttpService')
                store_set('http_json', http:JSONEncode(12))
                store_set('net_json', network_encode(12))");

            // WHY: OURS — LuaCsRbxJson formats doubles with ToString(""R"") (round-trip, integer-looking
            // for whole numbers); Newtonsoft's JValue(double) always renders a decimal point for a float
            // token so numeric JSON can be told apart from JSON integers. Every whole-number Lua value
            // therefore encodes differently between the two paths. Verified empirically against the
            // project's vendored Newtonsoft.Json before pinning this string.
            Assert.AreEqual("12", store.Get("m", "http_json"));
            Assert.AreEqual("12.0", store.Get("m", "net_json"));
        }

        [Test]
        public void Differential_FractionalNumber_BothEncodersAgree()
        {
            MemoryStore store = new();
            LuaCsModStack stack = BuildStackWithNetworkCodec(store);

            stack.Runtime.LoadMod("m", @"
                local http = game:GetService('HttpService')
                store_set('http_json', http:JSONEncode(-3.5))
                store_set('net_json', network_encode(-3.5))");

            // WHY: the ".0"-suffix divergence above is specific to WHOLE numbers; once a decimal digit is
            // already present, both formatters agree.
            Assert.AreEqual(store.Get("m", "http_json"), store.Get("m", "net_json"));
            Assert.AreEqual("-3.5", store.Get("m", "http_json"));
        }

        [Test]
        public void Differential_NaNAndInfinity_HttpBareTokens_NetworkQuotedStrings()
        {
            MemoryStore store = new();
            LuaCsModStack stack = BuildStackWithNetworkCodec(store);

            stack.Runtime.LoadMod("m", @"
                local http = game:GetService('HttpService')
                store_set('http_nan', http:JSONEncode(0/0))
                store_set('net_nan', network_encode(0/0))
                store_set('http_inf', http:JSONEncode(math.huge))
                store_set('net_inf', network_encode(math.huge))");

            // WHY: mirror says JSONEncode "allows values such as inf and nan which are not valid JSON".
            // LuaCsRbxJson honors that literally (bare, unquoted tokens). The network codec never chose
            // to support that extension; Json.NET's default FloatFormatHandling renders a non-finite
            // double as a JSON STRING, so a NaN/Infinity value crossing the wire silently becomes a Lua
            // string "NaN"/"Infinity" on decode, not a number. That is a real, separate finding worth
            // flagging even though this file does not change it (no fixture round-trips inf/nan through
            // the remote path in production today).
            Assert.AreEqual("NaN", store.Get("m", "http_nan"));
            Assert.AreEqual("\"NaN\"", store.Get("m", "net_nan"));
            Assert.AreEqual("Infinity", store.Get("m", "http_inf"));
            Assert.AreEqual("\"Infinity\"", store.Get("m", "net_inf"));
        }

        [Test]
        public void Differential_UnicodeAndEscaping_BothEncodersAgree()
        {
            MemoryStore store = new();
            LuaCsModStack stack = BuildStackWithNetworkCodec(store);

            stack.Runtime.LoadMod("m", @"
                local http = game:GetService('HttpService')
                local s = 'caf\195\169 ' .. string.char(34) .. 'quote' .. string.char(34)
                    .. string.char(92) .. string.char(10) .. string.char(9)
                store_set('http_json', http:JSONEncode(s))
                store_set('net_json', network_encode(s))");

            // WHY: both paths delegate string quoting to Newtonsoft (JsonConvert.ToString / JValue(string))
            // with the same default StringEscapeHandling, so unicode passthrough and control-character
            // escaping agree exactly — this is the one place table-shape and number-formatting rules do
            // not apply.
            Assert.AreEqual(store.Get("m", "http_json"), store.Get("m", "net_json"));
        }

        // ---------------------------------------------------------------------------------------
        // Cross-decode: "a value encoded by one decodes identically through the other" — it does not,
        // because the two wire shapes (bare JSON value vs $rbx-tagged argument-list envelope) are
        // fundamentally incompatible, not merely differently formatted.
        // ---------------------------------------------------------------------------------------

        [Test]
        public void CrossDecode_NetworkEnvelope_ThroughHttpJsonDecode_YieldsRawWrapperNotOriginalValue()
        {
            MemoryStore store = new();
            LuaCsModStack stack = BuildStackWithNetworkCodec(store);

            stack.Runtime.LoadMod("m", @"
                local http = game:GetService('HttpService')
                local netJson = network_encode({1, 2, 3})
                local decoded = http:JSONDecode(netJson)
                store_set('tag', tostring(decoded['$rbx']))
                store_set('kind', tostring(decoded.kind))
                store_set('values1', tostring(decoded.values[1]))
                store_set('is_flat_array', tostring(decoded[1] == nil))");

            // WHY: decoding the remote wire format through JSONDecode does NOT reconstruct {1,2,3} — it
            // exposes the raw $rbx envelope as an ordinary table, because JSONDecode has no concept of
            // the tag. The two decoders are not interchangeable even for a value one of them produced.
            Assert.AreEqual("table", store.Get("m", "tag"));
            Assert.AreEqual("array", store.Get("m", "kind"));
            Assert.AreEqual("1", store.Get("m", "values1"));
            Assert.AreEqual("true", store.Get("m", "is_flat_array"));
        }

        [Test]
        public void CrossDecode_HttpArrayJson_ThroughNetworkDecoder_MisinterpretsElementsAsSeparateArguments()
        {
            MemoryStore store = new();
            LuaCsModStack stack = BuildStackWithNetworkCodec(store);

            stack.Runtime.LoadMod("m", @"
                local http = game:GetService('HttpService')
                local httpJson = http:JSONEncode({1, 2, 3})
                local count = network_argument_count(httpJson)
                store_set('count', tostring(count))");

            // WHY: the network codec's root is an ARGUMENT LIST, not a value. Fed the HTTP encoder's
            // bare array "[1,2,3]", it decodes THREE top-level arguments (1, 2, 3) instead of one
            // array-valued argument — the intended single value is lost entirely, not merely
            // reformatted. This is why the two roots can never be treated as one shared JSON contract.
            Assert.AreEqual("3", store.Get("m", "count"));
        }

        [Test]
        public void CrossDecode_HttpDictionaryJson_ThroughNetworkDecoder_FailsOutright()
        {
            MemoryStore store = new();
            LuaCsModStack stack = BuildStackWithNetworkCodec(store);

            stack.Runtime.LoadMod("m", @"
                local http = game:GetService('HttpService')
                local httpJson = http:JSONEncode({name = 'Alice'})
                local ok, err = pcall(function() return network_argument_count(httpJson) end)
                store_set('ok', tostring(ok))
                store_set('err', tostring(err))");

            // WHY: an HTTP-encoded dictionary's JSON root is a plain object, but the network decoder
            // demands the root be an argument ARRAY — it refuses outright rather than misreading it.
            Assert.AreEqual("false", store.Get("m", "ok"));
            StringAssert.Contains("remote payload root must be an argument array", store.Get("m", "err"));
        }
    }
}
