using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Infrastructure.Logging;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Networking;
using CoreAI.Mods.Rbx.Instances.Replication;
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
    /// does exist (finite non-integer number formatting, NaN/Infinity spelling, string escaping) and the
    /// divergence that must stay (envelope shape, integer formatting, key-shape strictness) as an
    /// explicit, regression-tested contract instead of an unproven or false claim.
    /// The last two sections pin the remote codec's own wire contract (non-finite numbers, decode
    /// cost bounded by the payload rather than by path length times sibling count, exact diagnostic
    /// paths) and the Roblox rule that an Instance a client names in a remote payload resolves on the
    /// server only when that client can see it.
    /// </summary>
    [TestFixture]
    public sealed class RbxJsonContractEditModeTests
    {
        private const string SenderActorId = "actor-loopback";

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

            // WHY the expected text is rebuilt from bytes instead of written as a literal: a Lua
            // string is a BYTE string, and the mod above builds this one out of raw UTF-8 bytes
            // (\195\169 is one e-acute). Crossing back into C# gives one char per byte, so
            // comparing it against a UTF-16 literal compares two different things and fails on
            // text that is in fact intact. Decoding the same bytes as Latin-1 produces exactly the
            // view the boundary yields.
            string json = store.Get("m", "json");
            // Encoding.Latin1 is absent from this profile, so the byte-per-char view is built here.
            byte[] utf8 = System.Text.Encoding.UTF8.GetBytes("caf\u00E9 \u4E2D\u6587 \uD83D\uDE00");
            char[] byteView = new char[utf8.Length];
            for (int index = 0; index < utf8.Length; index++)
            {
                byteView[index] = (char)utf8[index];
            }

            StringAssert.Contains(new string(byteView), json);

            // WHY these two together: the first says the encoder passed the bytes through instead
            // of escaping them as \uXXXX (the mirror's behaviour), the second says nothing was lost
            // or re-encoded on the way back — the only guarantee a mod author can build on.
            StringAssert.DoesNotContain("\\u", json);
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
        public void Differential_NaNAndInfinity_BothEncodersEmitBareNumberTokens()
        {
            MemoryStore store = new();
            LuaCsModStack stack = BuildStackWithNetworkCodec(store);

            stack.Runtime.LoadMod("m", @"
                local http = game:GetService('HttpService')
                store_set('http_nan', http:JSONEncode(0/0))
                store_set('net_nan', network_encode(0/0))
                store_set('http_inf', http:JSONEncode(math.huge))
                store_set('net_inf', network_encode(math.huge))
                store_set('http_ninf', http:JSONEncode(-math.huge))
                store_set('net_ninf', network_encode(-math.huge))");

            // WHY: mirror says JSONEncode "allows values such as inf and nan which are not valid JSON".
            // LuaCsRbxJson honors that literally (bare, unquoted tokens). Roblox remotes carry these
            // values as numbers too, so the network codec spells them the same way instead of
            // Json.NET's default quoted "NaN"/"Infinity", which used to reach the receiving handler as
            // Lua STRINGS and break its arithmetic.
            Assert.AreEqual("NaN", store.Get("m", "http_nan"));
            Assert.AreEqual("NaN", store.Get("m", "net_nan"));
            Assert.AreEqual("Infinity", store.Get("m", "http_inf"));
            Assert.AreEqual("Infinity", store.Get("m", "net_inf"));
            Assert.AreEqual("-Infinity", store.Get("m", "http_ninf"));
            Assert.AreEqual("-Infinity", store.Get("m", "net_ninf"));
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

        // ---- Remote codec wire contract: non-finite numbers, bounded decode cost, diagnostic paths ----

        [Test]
        public void NetworkCodec_NaNAndInfinity_CrossTheWireAsNumbersInBothDirections()
        {
            LuaCsRbxNetworkCodec codec = NewStandaloneCodec();
            LuaTable nested = new();
            nested[1] = new LuaValue(double.NegativeInfinity);
            nested[2] = new LuaValue(0.5d);

            byte[] wire = codec.EncodeArguments(new[]
            {
                new LuaValue(double.NaN), new LuaValue(double.PositiveInfinity),
                new LuaValue(double.NegativeInfinity), new LuaValue(nested)
            });

            Assert.AreEqual(
                "[NaN,Infinity,-Infinity,{\"$rbx\":\"table\",\"kind\":\"array\",\"values\":[-Infinity,0.5]}]",
                Encoding.UTF8.GetString(wire));

            object[] decoded = codec.DecodeArguments(wire);

            Assert.IsInstanceOf<double>(decoded[0]);
            Assert.IsTrue(double.IsNaN((double)decoded[0]));
            Assert.IsInstanceOf<double>(decoded[1]);
            Assert.IsTrue(double.IsPositiveInfinity((double)decoded[1]));
            Assert.IsInstanceOf<double>(decoded[2]);
            Assert.IsTrue(double.IsNegativeInfinity((double)decoded[2]));
            LuaCsRbxNetworkTable table = (LuaCsRbxNetworkTable)decoded[3];
            Assert.IsTrue(double.IsNegativeInfinity((double)table.ArrayValues[0]));
            Assert.AreEqual(0.5d, (double)table.ArrayValues[1]);
        }

        [Test]
        public void NetworkCodec_QuotedNaNFromAnOlderSender_StillDecodesAsTheStringItAlwaysWas()
        {
            LuaCsRbxNetworkCodec codec = NewStandaloneCodec();

            object[] decoded = codec.DecodeArguments(
                Encoding.UTF8.GetBytes("[\"NaN\",\"Infinity\",NaN]"));

            // WHY: the decoder did not change, so a payload an older sender wrote reads exactly as it
            // did, and a genuine Lua string "NaN" is never coerced into a number.
            Assert.AreEqual("NaN", decoded[0]);
            Assert.AreEqual("Infinity", decoded[1]);
            Assert.IsTrue(double.IsNaN((double)decoded[2]));
        }

        [Test]
        public void NetworkCodec_MaxSizePayloadWithALongKeyOverManySiblings_DecodesWithBoundedAllocation()
        {
            LuaCsRbxNetworkCodec codec = NewStandaloneCodec();
            string longKey = new('k', 30_000);
            string prefix = "[{\"$rbx\":\"table\",\"kind\":\"dictionary\",\"values\":{\"" + longKey
                            + "\":{\"$rbx\":\"table\",\"kind\":\"dictionary\",\"values\":{";
            const string suffix = "}}}}]";
            byte[] payload = FillSiblingsUpToTheCap(prefix, suffix, out int siblings);

            codec.DecodeArguments(payload);
            long before = GC.GetAllocatedBytesForCurrentThread();
            object[] decoded = codec.DecodeArguments(payload);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            LuaCsRbxNetworkTable outer = (LuaCsRbxNetworkTable)decoded[0];
            Assert.AreEqual(longKey, outer.DictionaryValues[0].Key);
            LuaCsRbxNetworkTable inner = (LuaCsRbxNetworkTable)outer.DictionaryValues[0].Value;
            Assert.AreEqual(siblings, inner.DictionaryValues.Count);
            Assert.Greater(siblings, 4_000);
            AssertBoundedAllocation(allocated, "decoding a 30,000-character key over "
                                               + siblings + " siblings");
        }

        [Test]
        public void NetworkCodec_MaxSizePayloadWithLongKeysAtEveryNestingLevel_DecodesWithBoundedAllocation()
        {
            LuaCsRbxNetworkCodec codec = NewStandaloneCodec();
            const int levels = LuaCsRbxNetworkCodec.MaxNestingDepth - 1;
            string key = new('d', 900);
            StringBuilder open = new("[");
            StringBuilder close = new();
            for (int level = 0; level < levels; level++)
            {
                open.Append("{\"$rbx\":\"table\",\"kind\":\"dictionary\",\"values\":{\"")
                    .Append(key).Append(level).Append("\":");
                close.Append("}}");
            }

            open.Append("{\"$rbx\":\"table\",\"kind\":\"dictionary\",\"values\":{");
            byte[] payload = FillSiblingsUpToTheCap(
                open.ToString(), "}}" + close + "]", out int siblings);

            codec.DecodeArguments(payload);
            long before = GC.GetAllocatedBytesForCurrentThread();
            object[] decoded = codec.DecodeArguments(payload);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            LuaCsRbxNetworkTable cursor = (LuaCsRbxNetworkTable)decoded[0];
            for (int level = 0; level < levels; level++)
            {
                Assert.AreEqual(key + level, cursor.DictionaryValues[0].Key);
                cursor = (LuaCsRbxNetworkTable)cursor.DictionaryValues[0].Value;
            }

            Assert.AreEqual(siblings, cursor.DictionaryValues.Count);
            Assert.Greater(siblings, 500);
            AssertBoundedAllocation(allocated, "decoding " + levels
                                               + " levels of 900-character keys over "
                                               + siblings + " siblings");
        }

        [Test]
        public void NetworkCodec_EncodingALongKeyOverManySiblings_AllocatesBoundedMemory()
        {
            LuaCsRbxNetworkCodec codec = NewStandaloneCodec();
            LuaTable inner = new();
            for (int index = 0; index < 3_000; index++)
            {
                inner[index.ToString("x")] = new LuaValue(0d);
            }

            LuaTable root = new();
            root[new string('e', 20_000)] = new LuaValue(inner);
            LuaValue[] arguments = { new LuaValue(root) };

            codec.EncodeArguments(arguments);
            long before = GC.GetAllocatedBytesForCurrentThread();
            byte[] wire = codec.EncodeArguments(arguments);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.Greater(wire.Length, 40_000);
            AssertBoundedAllocation(allocated,
                "encoding a 20,000-character key over 3000 siblings");
        }

        [Test]
        public void NetworkCodec_DecodeErrorDeepInsideAStructure_ReportsTheExactPathText()
        {
            LuaCsRbxNetworkCodec codec = NewStandaloneCodec();

            RbxError unknownTag = Assert.Throws<RbxError>(() => codec.DecodeArguments(
                Encoding.UTF8.GetBytes(
                    "[1,{\"$rbx\":\"table\",\"kind\":\"dictionary\",\"values\":{\"outer\":"
                    + "{\"$rbx\":\"table\",\"kind\":\"array\",\"values\":[1,"
                    + "{\"$rbx\":\"table\",\"kind\":\"dictionary\",\"values\":{\"leaf\":"
                    + "{\"$rbx\":\"Bogus\"}}}]}}}]")));
            RbxError badId = Assert.Throws<RbxError>(() => codec.DecodeArguments(
                Encoding.UTF8.GetBytes(
                    "[0,0,{\"$rbx\":\"table\",\"kind\":\"array\",\"values\":["
                    + "{\"$rbx\":\"table\",\"kind\":\"dictionary\",\"values\":{\"b\":"
                    + "{\"$rbx\":\"Instance\",\"id\":\"x\"}}}]}]")));
            RbxError rawArray = Assert.Throws<RbxError>(() => codec.DecodeArguments(
                Encoding.UTF8.GetBytes(
                    "[{\"$rbx\":\"table\",\"kind\":\"array\",\"values\":[true,[1]]}]")));
            RbxError badKind = Assert.Throws<RbxError>(() => codec.DecodeArguments(
                Encoding.UTF8.GetBytes(
                    "[{\"$rbx\":\"table\",\"kind\":\"dictionary\",\"values\":{\"a\":"
                    + "{\"$rbx\":\"table\",\"kind\":\"weird\",\"values\":[]}}}]")));
            StringBuilder deep = new("[");
            StringBuilder expectedDepthPath = new("$[0]");
            for (int level = 0; level <= LuaCsRbxNetworkCodec.MaxNestingDepth; level++)
            {
                deep.Append("{\"$rbx\":\"table\",\"kind\":\"array\",\"values\":[");
            }

            deep.Append('1');
            for (int level = 0; level <= LuaCsRbxNetworkCodec.MaxNestingDepth; level++)
            {
                deep.Append("]}");
            }

            for (int level = 0; level < LuaCsRbxNetworkCodec.MaxNestingDepth; level++)
            {
                expectedDepthPath.Append("[1]");
            }

            RbxError tooDeep = Assert.Throws<RbxError>(() => codec.DecodeArguments(
                Encoding.UTF8.GetBytes(deep.Append(']').ToString())));

            Assert.AreEqual(
                "remote payload contains unknown Rbx tag 'Bogus' at $[1].outer[2].leaf",
                unknownTag.RawMessage);
            Assert.AreEqual("remote Instance id is invalid at $[2][1].b", badId.RawMessage);
            Assert.AreEqual("remote payload contains unsupported JSON token Array at $[0][2]",
                rawArray.RawMessage);
            Assert.AreEqual("remote table tag has an invalid kind at $[0].a", badKind.RawMessage);
            Assert.AreEqual(
                "remote payload table nesting exceeds CoreAI's 64 level limit at "
                + expectedDepthPath, tooDeep.RawMessage);
            Assert.AreEqual(RbxErrorCode.BadArgument, unknownTag.Code);
        }

        [Test]
        public void NetworkCodec_EncodeErrorDeepInsideAStructure_ReportsTheExactPathText()
        {
            LuaCsRbxNetworkCodec codec = NewStandaloneCodec();
            LuaTable mixed = new();
            mixed[1] = new LuaValue(1d);
            mixed["x"] = new LuaValue(2d);
            LuaTable holder = new();
            holder["k"] = new LuaValue(mixed);
            LuaTable array = new();
            array[1] = new LuaValue(holder);
            LuaTable cycle = new();
            LuaTable cycleInner = new();
            cycle["self"] = new LuaValue(cycleInner);
            cycleInner["back"] = new LuaValue(cycle);
            LuaTable sparse = new();
            sparse[1] = new LuaValue(1d);
            sparse[3] = new LuaValue(3d);
            LuaTable sparseHolder = new();
            sparseHolder["s"] = new LuaValue(sparse);
            LuaTable budget = new();
            for (int index = 0; index <= LuaCsRbxNetworkCodec.MaxAggregateEntries; index++)
            {
                budget["e" + index] = new LuaValue(true);
            }

            LuaTable budgetHolder = new();
            budgetHolder["big"] = new LuaValue(budget);

            RbxError mixedError = Assert.Throws<RbxError>(() => codec.EncodeArguments(
                new[] { new LuaValue(7d), new LuaValue(array) }));
            RbxError cycleError = Assert.Throws<RbxError>(() => codec.EncodeArguments(
                new[] { new LuaValue(cycle) }));
            RbxError sparseError = Assert.Throws<RbxError>(() => codec.EncodeArguments(
                new[] { new LuaValue(sparseHolder) }));
            RbxError budgetError = Assert.Throws<RbxError>(() => codec.EncodeArguments(
                new[] { new LuaValue(budgetHolder) }));

            Assert.AreEqual(
                "remote payload contains mixed numeric and non-numeric table keys at $[1][1].k",
                mixedError.RawMessage);
            Assert.AreEqual("remote payload contains a cyclic table at $[0].self.back",
                cycleError.RawMessage);
            Assert.AreEqual(
                "remote array keys must be unique contiguous indices 1..N at $[0].s",
                sparseError.RawMessage);
            Assert.AreEqual(
                "remote payload exceeds CoreAI's 100000 aggregate entry limit at $[0].big",
                budgetError.RawMessage);
        }

        [Test]
        public void NetworkCodec_UnresolvedValues_AreLoggedOncePerPayloadNotOncePerValue()
        {
            List<string> log = new();
            LuaCsRbxNetworkCodec codec = new(
                new InstanceRegistry(), RbxEnumRegistry.CreateWithBuiltins(), log.Add);
            const string bogusEnum = "{\"$rbx\":\"EnumItem\",\"enum\":\"Bogus\",\"name\":\"A\",\"value\":0}";

            object[] many = codec.DecodeArguments(Encoding.UTF8.GetBytes(
                "[{\"$rbx\":\"Instance\",\"id\":\"987654321\"},{\"$rbx\":\"Instance\",\"id\":\"987654322\"},"
                + bogusEnum + "," + bogusEnum + "," + bogusEnum + "]"));

            Assert.AreEqual(5, many.Length);
            for (int index = 0; index < many.Length; index++)
            {
                Assert.IsNull(many[index]);
            }

            CollectionAssert.AreEqual(new[]
            {
                "Remote payload enum item Enum.Bogus.A is not registered; decoded as nil"
                + " (3 such enum items in this payload).",
                "Remote payload InstanceId 987654321 is not visible in the receiving registry;"
                + " decoded as nil (2 such Instance references in this payload)."
            }, log);

            log.Clear();
            codec.DecodeArguments(Encoding.UTF8.GetBytes(
                "[{\"$rbx\":\"Instance\",\"id\":\"987654321\"}," + bogusEnum + "]"));

            CollectionAssert.AreEqual(new[]
            {
                "Remote payload enum item Enum.Bogus.A is not registered; decoded as nil.",
                "Remote payload InstanceId 987654321 is not visible in the receiving registry;"
                + " decoded as nil."
            }, log);
        }

        /// <summary>
        /// WHY 8 MB: the same payload cost about 268 MB (flat key) and 86 MB (nested keys) while every
        /// nested value built its own diagnostic path string; the lazy path leaves roughly the cost
        /// of parsing the JSON itself, about 2.5 MB. WHY 0 passes: Unity's Mono runtime answers 0
        /// from GetAllocatedBytesForCurrentThread, so the bound is enforced where the counter exists
        /// (CoreCLR) and the structural assertions above still run everywhere.
        /// </summary>
        private static void AssertBoundedAllocation(long allocated, string operation)
        {
            const long limit = 8L * 1024 * 1024;
            Assert.IsTrue(allocated == 0 || allocated < limit,
                operation + " allocated " + allocated + " bytes; the bound is " + limit);
        }

        private static byte[] FillSiblingsUpToTheCap(string prefix, string suffix, out int siblings)
        {
            StringBuilder entries = new();
            int budget = LuaCsRbxNetworkCodec.MaxPayloadBytes
                         - Encoding.UTF8.GetByteCount(prefix) - Encoding.UTF8.GetByteCount(suffix);
            siblings = 0;
            while (true)
            {
                string entry = (siblings == 0 ? "" : ",") + "\"" + siblings.ToString("x") + "\":0";
                if (entries.Length + entry.Length > budget)
                {
                    break;
                }

                entries.Append(entry);
                siblings++;
            }

            byte[] payload = Encoding.UTF8.GetBytes(prefix + entries + suffix);
            Assert.LessOrEqual(payload.Length, LuaCsRbxNetworkCodec.MaxPayloadBytes);
            return payload;
        }

        private static LuaCsRbxNetworkCodec NewStandaloneCodec()
        {
            return new LuaCsRbxNetworkCodec(
                new InstanceRegistry(), RbxEnumRegistry.CreateWithBuiltins(), null);
        }

        // ---- Remote codec: Instance references a client sends to the server -------------------
        // WHY: such a reference resolves on the server only when the sending client can see the
        // instance (Roblox remote-events guide, "Non-replicated instances").

        [Test]
        public void ClientPayload_NamingAServerStorageChild_DecodesAsNilOnTheServer()
        {
            ClientCodecWorld world = new();
            RbxInstance serverStorage = world.Game.GetService("ServerStorage");
            RbxInstance vault = world.CreatePart("AdminVault", serverStorage);
            RbxInstance scripts = world.Game.GetService("ServerScriptService");
            RbxInstance secretScriptFolder = world.CreatePart("Secrets", scripts);

            object[] decoded = world.Codec.DecodeClientArguments(
                InstancePayload(vault, secretScriptFolder, vault), SenderActorId);

            // WHY: remote-events guide, "Non-replicated instances": a value only the sender side can
            // see "passes nil instead". A client has no legitimate way to name a ServerStorage or
            // ServerScriptService object, so the server must never hand its handler the live one.
            Assert.AreEqual(3, decoded.Length);
            Assert.IsNull(decoded[0]);
            Assert.IsNull(decoded[1]);
            Assert.IsNull(decoded[2]);
            Assert.AreEqual(3L, world.Codec.HiddenClientInstanceReferences);
            Assert.AreEqual(1L, world.Codec.HiddenClientReferencePayloads);
            Assert.AreEqual(1, world.Log.Count,
                "hidden references are reported once per payload, not once per value");
            StringAssert.Contains("actor '" + SenderActorId + "'", world.Log[0]);
            StringAssert.Contains(
                "InstanceId " + vault.Id.Value.ToString(CultureInfo.InvariantCulture),
                world.Log[0]);
            StringAssert.Contains("cannot see", world.Log[0]);
        }

        [Test]
        public void ClientPayload_NamingServerOnlyInstanceInsideATable_DecodesThatEntryAsNil()
        {
            ClientCodecWorld world = new();
            RbxInstance vault = world.CreatePart("AdminVault", world.Game.GetService("ServerStorage"));
            RbxInstance visible = world.CreatePart("Crate", world.Game.GetService("Workspace"));
            string json = "[{\"$rbx\":\"table\",\"kind\":\"dictionary\",\"values\":{"
                          + "\"template\":" + InstanceTag(vault) + ","
                          + "\"target\":" + InstanceTag(visible) + "}}]";

            object[] decoded = world.Codec.DecodeClientArguments(
                Encoding.UTF8.GetBytes(json), SenderActorId);

            LuaCsRbxNetworkTable table = (LuaCsRbxNetworkTable)decoded[0];
            Assert.AreEqual(2, table.DictionaryValues.Count);
            Assert.AreEqual("template", table.DictionaryValues[0].Key);
            Assert.IsNull(table.DictionaryValues[0].Value);
            Assert.AreEqual("target", table.DictionaryValues[1].Key);
            Assert.AreSame(visible, table.DictionaryValues[1].Value);
            Assert.AreEqual(1L, world.Codec.HiddenClientInstanceReferences);
        }

        [Test]
        public void ClientPayload_NamingReplicatedInstances_ResolvesThoseInstances()
        {
            ClientCodecWorld world = new();
            RbxInstance workspacePart = world.CreatePart(
                "Crate", world.Game.GetService("Workspace"));
            RbxInstance template = world.CreatePart(
                "SwordTemplate", world.Game.GetService("ReplicatedStorage"));

            object[] decoded = world.Codec.DecodeClientArguments(
                InstancePayload(workspacePart, template), SenderActorId);

            Assert.AreSame(workspacePart, decoded[0]);
            Assert.AreSame(template, decoded[1]);
            Assert.AreEqual(0L, world.Codec.HiddenClientInstanceReferences);
            Assert.AreEqual(0L, world.Codec.HiddenClientReferencePayloads);
            Assert.IsEmpty(world.Log);
        }

        [Test]
        public void ClientPayload_NamingAnotherPlayersBackpack_DecodesAsNil_ButTheSendersOwnResolves()
        {
            ClientCodecWorld world = new();
            RbxPlayers players = (RbxPlayers)world.Game.GetService("Players");
            RbxPlayer sender = players.EnsureActor(world.Registry, SenderActorId);
            RbxPlayer other = players.EnsureActor(world.Registry, "actor-other");
            RbxInstance ownTool = world.CreatePart("OwnTool", sender.FindFirstChild("Backpack"));
            RbxInstance otherTool = world.CreatePart("OtherTool", other.FindFirstChild("Backpack"));
            RbxInstance otherGui = other.FindFirstChild("PlayerGui");

            object[] decoded = world.Codec.DecodeClientArguments(
                InstancePayload(ownTool, otherTool, otherGui, other), SenderActorId);

            Assert.AreSame(ownTool, decoded[0]);
            Assert.IsNull(decoded[1], "another player's Backpack is replicated to its owner only");
            Assert.IsNull(decoded[2], "another player's PlayerGui is replicated to its owner only");
            Assert.AreSame(other, decoded[3], "Player objects replicate to every client");
            Assert.AreEqual(2L, world.Codec.HiddenClientInstanceReferences);
        }

        [Test]
        public void ClientPayload_NamingUnknownDestroyedOrDetachedIds_DecodesAsNil()
        {
            ClientCodecWorld world = new();
            RbxInstance destroyed = world.CreatePart("Gone", world.Game.GetService("Workspace"));
            InstanceId destroyedId = destroyed.Id;
            destroyed.Destroy();
            RbxInstance detached = world.Registry.Create("Part");
            string json = "[{\"$rbx\":\"Instance\",\"id\":\"987654321\"},"
                          + "{\"$rbx\":\"Instance\",\"id\":\""
                          + destroyedId.Value.ToString(CultureInfo.InvariantCulture) + "\"},"
                          + InstanceTag(detached) + "]";

            object[] decoded = world.Codec.DecodeClientArguments(
                Encoding.UTF8.GetBytes(json), SenderActorId);

            Assert.IsNull(decoded[0], "an id the server never issued");
            Assert.IsNull(decoded[1], "an instance destroyed before the payload arrived");
            Assert.IsNull(decoded[2], "a subtree with no DataModel ancestor replicates to nobody");
            Assert.AreEqual(3L, world.Codec.HiddenClientInstanceReferences);
            Assert.AreEqual(1, world.Log.Count);
        }

        [Test]
        public void TrustedPayload_ServerToClientAndHostLocalPath_StillResolvesEveryRegisteredInstance()
        {
            ClientCodecWorld world = new();
            RbxInstance vault = world.CreatePart("AdminVault", world.Game.GetService("ServerStorage"));
            RbxInstance detached = world.Registry.Create("Part");

            object[] decoded = world.Codec.DecodeArguments(InstancePayload(vault, detached));

            Assert.AreSame(vault, decoded[0]);
            Assert.AreSame(detached, decoded[1]);
            Assert.AreEqual(0L, world.Codec.HiddenClientInstanceReferences);
            Assert.IsEmpty(world.Log);
        }

        [Test]
        public void ClientPayloadsWithHiddenReferences_AreCountedExactly_AndLoggedAtPowersOfTwo()
        {
            ClientCodecWorld world = new();
            RbxInstance vault = world.CreatePart("AdminVault", world.Game.GetService("ServerStorage"));
            byte[] payload = InstancePayload(vault);

            for (int index = 0; index < 1000; index++)
            {
                Assert.IsNull(world.Codec.DecodeClientArguments(payload, SenderActorId)[0]);
            }

            Assert.AreEqual(1000L, world.Codec.HiddenClientInstanceReferences);
            Assert.AreEqual(1000L, world.Codec.HiddenClientReferencePayloads);
            // WHY 10: payloads 1, 2, 4, ..., 512 are logged; a client repeating the payload at its own
            // packet rate must not be able to write the server's log at that rate.
            Assert.AreEqual(10, world.Log.Count);
            StringAssert.EndsWith("so far: 512.", world.Log[9]);
        }

        [Test]
        public void ClientPayload_VisibilityIsDecidedAfreshForEveryPayload()
        {
            ClientCodecWorld world = new();
            RbxInstance part = world.CreatePart("Crate", world.Game.GetService("Workspace"));
            byte[] payload = InstancePayload(part, part);

            object[] whileReplicated = world.Codec.DecodeClientArguments(payload, SenderActorId);
            part.Parent = world.Game.GetService("ServerStorage");
            object[] afterMovingServerSide = world.Codec.DecodeClientArguments(payload, SenderActorId);

            Assert.AreSame(part, whileReplicated[0]);
            Assert.AreSame(part, whileReplicated[1]);
            Assert.IsNull(afterMovingServerSide[0]);
            Assert.IsNull(afterMovingServerSide[1]);
        }

        [Test]
        public void ClientPayload_AsksTheReplicationFilterAtMostOncePerNode()
        {
            CountingReplicationFilter filter = new(allowEverything: false);
            ClientCodecWorld world = new(filter);
            RbxInstance cursor = world.Game.GetService("Workspace");
            const int depth = 20;
            for (int level = 0; level < depth; level++)
            {
                RbxInstance folder = world.Registry.Create("Folder");
                folder.Parent = cursor;
                cursor = folder;
            }

            const int leaves = 50;
            RbxInstance[] parts = new RbxInstance[leaves];
            for (int index = 0; index < leaves; index++)
            {
                parts[index] = world.CreatePart("Leaf" + index, cursor);
            }

            object[] decoded = world.Codec.DecodeClientArguments(InstancePayload(parts), SenderActorId);

            for (int index = 0; index < leaves; index++)
            {
                Assert.AreSame(parts[index], decoded[index]);
            }

            // WHY: the guard asks the game filter about every ancestor of every reference, so without
            // a per-payload answer cache 50 leaves under 22 shared ancestors cost 50 x 23 = 1150 calls,
            // and a 64 KiB payload of deep references costs the server N x depth^2. Each distinct node
            // (50 leaves + 20 folders + Workspace + the DataModel) needs one answer.
            int distinctNodes = leaves + depth + 2;
            Assert.Greater(filter.Calls, 0);
            Assert.LessOrEqual(filter.Calls, distinctNodes);
        }

        [Test]
        public void ClientPayload_APermissiveGameFilterCannotLiftTheReplicationFloor()
        {
            CountingReplicationFilter filter = new(allowEverything: true);
            ClientCodecWorld world = new(filter);
            RbxInstance vault = world.CreatePart("AdminVault", world.Game.GetService("ServerStorage"));
            RbxPlayers players = (RbxPlayers)world.Game.GetService("Players");
            players.EnsureActor(world.Registry, SenderActorId);
            RbxPlayer other = players.EnsureActor(world.Registry, "actor-other");
            RbxInstance otherTool = world.CreatePart("OtherTool", other.FindFirstChild("Backpack"));
            RbxInstance detached = world.Registry.Create("Part");

            object[] decoded = world.Codec.DecodeClientArguments(
                InstancePayload(vault, otherTool, detached), SenderActorId);

            Assert.IsNull(decoded[0], "ServerStorage is below the floor whatever the game filter says");
            Assert.IsNull(decoded[1], "another player's Backpack is below the floor");
            Assert.AreSame(detached, decoded[2],
                "above the floor the game's filter decides, and this one allows everything");
        }

        private sealed class CountingReplicationFilter : IReplicationFilter
        {
            private readonly bool _allowEverything;

            public CountingReplicationFilter(bool allowEverything)
            {
                _allowEverything = allowEverything;
            }

            public int Calls { get; private set; }

            public bool IsVisibleTo(string recipientActorId, RbxInstance instance)
            {
                Calls++;
                return _allowEverything
                       || DefaultReplicationFilter.Instance.IsVisibleTo(recipientActorId, instance);
            }
        }

        private sealed class ClientCodecWorld
        {
            public ClientCodecWorld(IReplicationFilter clientVisibility = null)
            {
                Registry = new InstanceRegistry();
                Game = DataModelBootstrap.CreateGame(Registry);
                Codec = new LuaCsRbxNetworkCodec(
                    Registry, RbxEnumRegistry.CreateWithBuiltins(), Log.Add, clientVisibility);
            }

            public InstanceRegistry Registry { get; }

            public RbxDataModel Game { get; }

            public List<string> Log { get; } = new();

            public LuaCsRbxNetworkCodec Codec { get; }

            public RbxInstance CreatePart(string name, RbxInstance parent)
            {
                Assert.IsNotNull(parent, "the parent container must exist for " + name);
                RbxInstance part = Registry.Create("Part");
                part.Name = name;
                part.Parent = parent;
                return part;
            }
        }

        private static string InstanceTag(RbxInstance instance)
        {
            return "{\"$rbx\":\"Instance\",\"id\":\""
                   + instance.Id.Value.ToString(CultureInfo.InvariantCulture) + "\"}";
        }

        private static byte[] InstancePayload(params RbxInstance[] instances)
        {
            StringBuilder json = new("[");
            for (int index = 0; index < instances.Length; index++)
            {
                if (index > 0)
                {
                    json.Append(',');
                }

                json.Append(InstanceTag(instances[index]));
            }

            return Encoding.UTF8.GetBytes(json.Append(']').ToString());
        }
    }
}
