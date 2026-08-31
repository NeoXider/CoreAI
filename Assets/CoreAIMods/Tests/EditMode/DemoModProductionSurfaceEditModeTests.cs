using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Authority;
using CoreAI.Infrastructure.Logging;
using CoreAI.Messaging;
using CoreAI.Mods.Rbx.Binding;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Scripting;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Regression for the shipped demo mods that called the withheld <c>coreai_world_*</c> build
    /// APIs and threw the stub error under the production composition
    /// (<see cref="LuaCsModStackOptions.RegisterWorldEditBuildBindings"/> = false, as
    /// CoreAiModsInstaller configures it). The ported mods — the Wave Director file mod and the
    /// FullAccess Tetris embedded in LuaPlatformExampleController — are loaded through
    /// <see cref="LuaCsModRuntimeFactory"/> in exactly that production configuration and driven
    /// headlessly over the Rbx API, proving they run without routing a single world command.
    /// Lua-side failures are caught via the non-generic <see cref="Assert.Catch(TestDelegate)"/>
    /// and surfaced through the runtime's load/quarantine state.
    /// </summary>
    public sealed class DemoModProductionSurfaceEditModeTests
    {
        private SynchronizationContext _savedContext;

        /// <summary>See <see cref="LuaCsModRuntimeEditModeTests"/>: the runtime blocks on its async
        /// VM, so the Unity main-thread SynchronizationContext must be detached to avoid deadlocks.</summary>
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

                foreach ((string storedModId, string key) in keys)
                {
                    _values.Remove((storedModId, key));
                }
            }
        }

        private sealed class FakeCommandSink : IAiGameCommandSink
        {
            public readonly List<ApplyAiGameCommand> Commands = new();

            public void Publish(ApplyAiGameCommand command)
            {
                Commands.Add(command);
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

        /// <summary>Builds the stack exactly as the production composition does: the Rbx surface
        /// registered, the coreai_world_* build bindings withheld.</summary>
        private static LuaCsModStack BuildProductionStack(
            LuaCsRbxApiBindings rbxApi, FakeCommandSink sink, ILuaModStore store,
            IRbxHttpRequestPolicy httpPolicy = null,
            IRbxHttpTransport httpTransport = null,
            IRbxHttpDestinationResolver httpResolver = null,
            int httpRequestsPerWindow = LuaCsRbxHttpServiceAdapter.DefaultRequestsPerWindow,
            double httpRateWindowSeconds = LuaCsRbxHttpServiceAdapter.DefaultRateWindowSeconds,
            Func<double> monotonicClock = null)
        {
            return LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
            {
                Logger = new FakeGameLogger(),
                CommandSink = sink,
                ModStore = store,
                Capabilities = LuaCapabilities.All,
                OneOffCapabilities = LuaCapabilities.All,
                RbxApi = rbxApi,
                RegisterWorldEditBuildBindings = false,
                RbxHttpPolicy = httpPolicy,
                RbxHttpTransport = httpTransport,
                RbxHttpResolver = httpResolver,
                RbxHttpRequestsPerWindow = httpRequestsPerWindow,
                RbxHttpRateWindowSeconds = httpRateWindowSeconds,
                RbxMonotonicClock = monotonicClock
            });
        }

        private sealed class PassThroughHttpRequestPolicy : IRbxHttpRequestPolicy
        {
            public readonly List<string> ActorIds = new();

            public bool IsEnabled => true;

            public bool TryAuthorize(string actorId, RbxHttpRequest requested,
                out RbxHttpRequest approved, out string refusalReason)
            {
                ActorIds.Add(actorId);
                approved = requested;
                refusalReason = null;
                return true;
            }
        }

        private sealed class RecordingHttpResolver : IRbxHttpDestinationResolver
        {
            private readonly IReadOnlyList<IPAddress> _addresses;

            public RecordingHttpResolver(params string[] addresses)
            {
                List<IPAddress> parsed = new();
                foreach (string address in addresses)
                {
                    parsed.Add(IPAddress.Parse(address));
                }

                _addresses = parsed;
            }

            public int CallCount { get; private set; }

            public string LastHost { get; private set; }

            public Task<IReadOnlyList<IPAddress>> ResolveAsync(string host,
                CancellationToken cancellationToken)
            {
                CallCount++;
                LastHost = host;
                return Task.FromResult(_addresses);
            }
        }

        private sealed class RecordingHttpTransport : IRbxHttpTransport
        {
            private readonly RbxHttpResponse _response;

            public RecordingHttpTransport(int statusCode = 200,
                string statusMessage = "OK", string body = "safe-response")
            {
                _response = new RbxHttpResponse(statusCode, statusMessage, body);
            }

            public readonly List<RbxValidatedHttpDestination> Destinations = new();

            public Task<RbxHttpResponse> SendAsync(RbxValidatedHttpDestination destination,
                CancellationToken cancellationToken)
            {
                Destinations.Add(destination);
                return Task.FromResult(_response);
            }
        }

        private static ActorContext HostileActor(string actorId)
        {
            return new LocalActorIdentityProvider(
                    actorId, "session-" + actorId, "", ActorGrantSet.None,
                    AgentMemoryScope.Empty)
                .GetActorContext(BuiltInAgentRoleIds.Programmer);
        }

        private static string HostileExpressionSource(string expression)
        {
            return @"
                local http = game:GetService('HttpService')
                hooks_on('http_attack', function()
                    task.spawn(function()
                        local ok, value = pcall(function()
                            return " + expression + @"
                        end)
                        store_set('ok', tostring(ok))
                        store_set('value', ok and 'unexpected-success' or tostring(value))
                        store_set('done', 'yes')
                    end)
                end)";
        }

        private static void LoadHostileMod(LuaCsModStack stack, string modId,
            string actorId, string source)
        {
            stack.Runtime.LoadMod(HostileActor(actorId), modId, source,
                LuaCapabilities.Read, persistToStore: false);
        }

        private static void RunHostileExpression(LuaCsModStack stack, MemoryStore store,
            string expression, string modId = "hostile", string actorId = "hostile-actor")
        {
            LoadHostileMod(stack, modId, actorId, HostileExpressionSource(expression));
            stack.Runtime.EmitEvent("http_attack", "");
            TickUntilComplete(stack, store, modId);
        }

        private static void TickUntilComplete(LuaCsModStack stack, MemoryStore store,
            params string[] modIds)
        {
            for (int attempt = 0; attempt < 256; attempt++)
            {
                bool complete = true;
                foreach (string modId in modIds)
                {
                    if (store.Get(modId, "done") != "yes")
                    {
                        complete = false;
                        break;
                    }
                }

                if (complete)
                {
                    return;
                }

                stack.Runtime.Tick(0d);
                stack.GameplayBindings.RbxApi.Scheduler.Advance(0d);
                Thread.Yield();
            }

            Assert.Fail("Hostile mod did not complete through the production scheduler.");
        }

        private static void AssertHostileRefused(MemoryStore store, string expectedMessage,
            string modId = "hostile")
        {
            Assert.AreEqual("false", store.Get(modId, "ok"));
            StringAssert.Contains(expectedMessage, store.Get(modId, "value"));
        }

        private static RbxInstance Workspace(LuaCsRbxApiBindings rbxApi)
        {
            return rbxApi.Game.FindFirstChildOfClass("Workspace");
        }

        private static string WaveDirectorSource()
        {
            string path = Path.Combine(Application.dataPath, "CoreAI.Demos/LuaMods/WaveDirectorMod.lua.txt");
            Assert.IsTrue(File.Exists(path), "Shipped demo mod is missing: " + path);
            return File.ReadAllText(path);
        }

        private static string TetrisSource()
        {
            Type controller = Type.GetType("CoreAI.Demos.LuaPlatformExampleController, CoreAI.Demos");
            if (controller == null)
            {
                Assert.Ignore("The CoreAI.Demos assembly is not available to this test run.");
            }

            FieldInfo field = controller.GetField(
                "TetrisSource", BindingFlags.NonPublic | BindingFlags.Static);
            if (field == null)
            {
                Assert.Ignore("COREAI_LUA is disabled; the demo compiles its no-Lua stub.");
            }

            return (string)field.GetValue(null);
        }

        private static void AssertNoWithheldBuildApiCall(string source, string modLabel)
        {
            foreach (string api in LuaCsWorldRuntimeBindings.BuildApiNames)
            {
                StringAssert.DoesNotContain(api, source,
                    modLabel + " must not call the withheld build API " + api + " in production.");
            }

            foreach (string api in LuaCsComponentRuntimeBindings.BuildApiNames)
            {
                StringAssert.DoesNotContain(api, source,
                    modLabel + " must not call the withheld build API " + api + " in production.");
            }
        }

        [TestCase("relative/path", "absolute URL")]
        [TestCase("http://api.example.test/path", "only absolute HTTPS URLs")]
        public void HostileMod_NonAbsoluteHttpsUrl_IsDenied(
            string url, string expectedMessage)
        {
            LuaCsRbxApiBindings rbxApi = new();
            MemoryStore store = new();
            PassThroughHttpRequestPolicy policy = new();
            RecordingHttpTransport transport = new();
            RecordingHttpResolver resolver = new("93.184.216.34");
            LuaCsModStack stack = BuildProductionStack(
                rbxApi, new FakeCommandSink(), store, policy, transport, resolver);

            RunHostileExpression(stack, store,
                "http:RequestAsync({ Url = '" + url + "', Method = 'GET' })");

            AssertHostileRefused(store, expectedMessage);
            Assert.AreEqual(0, transport.Destinations.Count);
        }

        [Test]
        public void HostileMod_UrlUserInfo_IsDenied()
        {
            LuaCsRbxApiBindings rbxApi = new();
            MemoryStore store = new();
            PassThroughHttpRequestPolicy policy = new();
            RecordingHttpTransport transport = new();
            LuaCsModStack stack = BuildProductionStack(
                rbxApi, new FakeCommandSink(), store, policy, transport,
                new RecordingHttpResolver("93.184.216.34"));

            RunHostileExpression(stack, store,
                "http:RequestAsync({ Url = 'https://user:pass@api.example.test/', Method = 'GET' })");

            AssertHostileRefused(store, "URL user-info credentials are forbidden");
            Assert.AreEqual(0, transport.Destinations.Count);
        }

        [TestCase("GET")]
        [TestCase("HEAD")]
        public void HostileMod_GetOrHeadBody_IsDenied(string method)
        {
            LuaCsRbxApiBindings rbxApi = new();
            MemoryStore store = new();
            PassThroughHttpRequestPolicy policy = new();
            RecordingHttpTransport transport = new();
            LuaCsModStack stack = BuildProductionStack(
                rbxApi, new FakeCommandSink(), store, policy, transport,
                new RecordingHttpResolver("93.184.216.34"));

            RunHostileExpression(stack, store,
                "http:RequestAsync({ Url = 'https://api.example.test/', Method = '"
                + method + "', Body = 'smuggled' })");

            AssertHostileRefused(store, method + " requests cannot contain a body");
            Assert.AreEqual(0, transport.Destinations.Count);
        }

        [TestCase("Bad Header")]
        [TestCase("X:Injected")]
        [TestCase("Authorization ")]
        public void HostileMod_NonTokenOrNearMissHeaderName_IsDenied(string headerName)
        {
            LuaCsRbxApiBindings rbxApi = new();
            MemoryStore store = new();
            PassThroughHttpRequestPolicy policy = new();
            RecordingHttpTransport transport = new();
            LuaCsModStack stack = BuildProductionStack(
                rbxApi, new FakeCommandSink(), store, policy, transport,
                new RecordingHttpResolver("93.184.216.34"));

            RunHostileExpression(stack, store,
                "http:RequestAsync({ Url = 'https://api.example.test/', Method = 'GET', "
                + "Headers = { ['" + headerName + "'] = 'value' } })");

            AssertHostileRefused(store, "untrimmed RFC tokens");
            Assert.AreEqual(0, transport.Destinations.Count);
        }

        [Test]
        public void HostileMod_HeaderCrlf_IsDenied()
        {
            LuaCsRbxApiBindings rbxApi = new();
            MemoryStore store = new();
            PassThroughHttpRequestPolicy policy = new();
            RecordingHttpTransport transport = new();
            LuaCsModStack stack = BuildProductionStack(
                rbxApi, new FakeCommandSink(), store, policy, transport,
                new RecordingHttpResolver("93.184.216.34"));

            RunHostileExpression(stack, store,
                "http:RequestAsync({ Url = 'https://api.example.test/', Method = 'GET', "
                + "Headers = { ['X-Test'] = 'safe\\r\\nInjected: yes' } })");

            AssertHostileRefused(store, "cannot contain line breaks");
            Assert.AreEqual(0, transport.Destinations.Count);
        }

        [TestCase("Authorization")]
        [TestCase("Proxy-Authorization")]
        [TestCase("Cookie")]
        [TestCase("Set-Cookie")]
        [TestCase("Host")]
        [TestCase("Content-Length")]
        [TestCase("Connection")]
        [TestCase("Transfer-Encoding")]
        [TestCase("Upgrade")]
        [TestCase("X-Api-Key")]
        [TestCase("Api-Key")]
        public void HostileMod_CredentialOrTransportHeader_IsDenied(string headerName)
        {
            LuaCsRbxApiBindings rbxApi = new();
            MemoryStore store = new();
            PassThroughHttpRequestPolicy policy = new();
            RecordingHttpTransport transport = new();
            LuaCsModStack stack = BuildProductionStack(
                rbxApi, new FakeCommandSink(), store, policy, transport,
                new RecordingHttpResolver("93.184.216.34"));

            RunHostileExpression(stack, store,
                "http:RequestAsync({ Url = 'https://api.example.test/', Method = 'GET', "
                + "Headers = { ['" + headerName + "'] = 'secret' } })");

            AssertHostileRefused(store, "credential-bearing or transport-controlled");
            Assert.AreEqual(0, transport.Destinations.Count);
        }

        [TestCase("0.1.2.3", "0.0.0.0/8")]
        [TestCase("10.1.2.3", "10.0.0.0/8")]
        [TestCase("100.64.0.1", "100.64.0.0/10")]
        [TestCase("127.1.2.3", "127.0.0.0/8")]
        [TestCase("169.254.1.1", "169.254.0.0/16")]
        [TestCase("172.16.1.1", "172.16.0.0/12")]
        [TestCase("192.0.0.1", "192.0.0.0/24")]
        [TestCase("192.0.2.1", "192.0.2.0/24")]
        [TestCase("192.31.196.1", "192.31.196.0/24")]
        [TestCase("192.52.193.1", "192.52.193.0/24")]
        [TestCase("192.88.99.1", "192.88.99.0/24")]
        [TestCase("192.168.1.1", "192.168.0.0/16")]
        [TestCase("192.175.48.1", "192.175.48.0/24")]
        [TestCase("198.18.0.1", "198.18.0.0/15")]
        [TestCase("198.51.100.1", "198.51.100.0/24")]
        [TestCase("203.0.113.1", "203.0.113.0/24")]
        [TestCase("224.0.0.1", "224.0.0.0/4")]
        [TestCase("240.0.0.1", "240.0.0.0/4")]
        [TestCase("::2", "::/96")]
        [TestCase("::ffff:0:1", "::ffff:0:0/96")]
        [TestCase("::ffff:0:0:1", "::ffff:0:0:0/96")]
        [TestCase("64:ff9b::c000:201", "64:ff9b::/96 NAT64")]
        [TestCase("64:ff9b:1::c000:201", "64:ff9b:1::/48 local-use NAT64")]
        [TestCase("100::1", "100::/64")]
        [TestCase("2001:1::1", "2001::/23")]
        [TestCase("2001:db8::1", "2001:db8::/32")]
        [TestCase("2002:c000:201::1", "2002::/16")]
        [TestCase("2620:4f:8000::1", "2620:4f:8000::/48")]
        [TestCase("3fff::1", "3fff::/20")]
        [TestCase("5f00::1", "5f00::/16")]
        [TestCase("fc00::1", "fc00::/7")]
        [TestCase("fe80::1", "fe80::/10")]
        [TestCase("fec0::1", "fec0::/10")]
        [TestCase("ff00::1", "ff00::/8")]
        public void HostileMod_EachIanaSpecialLiteral_IsDenied(
            string literalAddress, string ianaRange)
        {
            LuaCsRbxApiBindings rbxApi = new();
            MemoryStore store = new();
            PassThroughHttpRequestPolicy policy = new();
            RecordingHttpTransport transport = new();
            RecordingHttpResolver resolver = new("93.184.216.34");
            LuaCsModStack stack = BuildProductionStack(
                rbxApi, new FakeCommandSink(), store, policy, transport, resolver);
            string host = literalAddress.IndexOf(':') >= 0
                ? "[" + literalAddress + "]"
                : literalAddress;

            RunHostileExpression(stack, store,
                "http:RequestAsync({ Url = 'https://" + host + "/', Method = 'GET' })");

            AssertHostileRefused(store, "special hosts");
            Assert.AreEqual(0, resolver.CallCount,
                ianaRange + " must be recognized as a literal without DNS.");
            Assert.AreEqual(0, transport.Destinations.Count,
                ianaRange + " must never reach the transport.");
        }

        [Test]
        public void HostileMod_AllowlistRejectsUnlistedOrigin()
        {
            LuaCsRbxApiBindings rbxApi = new();
            MemoryStore store = new();
            RbxAllowlistHttpRequestPolicy policy = new(
                new[] { "https://allowed.example.test" });
            RecordingHttpResolver resolver = new("93.184.216.34");
            RecordingHttpTransport transport = new();
            LuaCsModStack stack = BuildProductionStack(
                rbxApi, new FakeCommandSink(), store, policy, transport, resolver);

            RunHostileExpression(stack, store,
                "http:GetAsync('https://unlisted.example.test/data')");

            AssertHostileRefused(store, "not on the host allowlist");
            Assert.AreEqual(0, resolver.CallCount);
            Assert.AreEqual(0, transport.Destinations.Count);
        }

        [Test]
        public void HostileMod_ProductionDefaultsWithoutPolicyOrTransport_DenyLoudly()
        {
            LuaCsRbxApiBindings rbxApi = new();
            MemoryStore store = new();
            LuaCsModStack stack = BuildProductionStack(
                rbxApi, new FakeCommandSink(), store);

            RunHostileExpression(stack, store,
                "http:GetAsync('https://api.example.test/data')");

            AssertHostileRefused(store, "did not explicitly authorize the request");
        }

        [Test]
        public void HostileMod_ProductionDefaultResolver_RefusesDomain()
        {
            LuaCsRbxApiBindings rbxApi = new();
            MemoryStore store = new();
            PassThroughHttpRequestPolicy policy = new();
            RecordingHttpTransport transport = new();
            LuaCsModStack stack = BuildProductionStack(
                rbxApi, new FakeCommandSink(), store, policy, transport);

            RunHostileExpression(stack, store,
                "http:GetAsync('https://api.example.test/data')");

            AssertHostileRefused(store, "no DNS resolver for mod HTTP");
            Assert.AreEqual(0, transport.Destinations.Count);
        }

        [Test]
        public void HostileMod_ProductionDefaultTransport_RefusesValidatedDestination()
        {
            LuaCsRbxApiBindings rbxApi = new();
            MemoryStore store = new();
            PassThroughHttpRequestPolicy policy = new();
            RecordingHttpResolver resolver = new("93.184.216.34");
            LuaCsModStack stack = BuildProductionStack(
                rbxApi, new FakeCommandSink(), store, policy, null, resolver);

            RunHostileExpression(stack, store,
                "http:GetAsync('https://api.example.test/data')");

            AssertHostileRefused(store, "no outbound HTTP transport");
            Assert.AreEqual(1, resolver.CallCount);
        }

        [Test]
        public void HostileMods_RateLimitUsesTrustedPerActorIdentity_NotLuaInput()
        {
            LuaCsRbxApiBindings rbxApi = new();
            MemoryStore store = new();
            PassThroughHttpRequestPolicy policy = new();
            RecordingHttpResolver resolver = new("93.184.216.34");
            RecordingHttpTransport transport = new();
            LuaCsModStack stack = BuildProductionStack(
                rbxApi, new FakeCommandSink(), store, policy, transport, resolver,
                httpRequestsPerWindow: 1, httpRateWindowSeconds: 60d,
                monotonicClock: () => 10d);
            string request = "http:RequestAsync({ Url = 'https://api.example.test/data', "
                             + "Method = 'GET', ActorId = 'forged-victim' })";
            string actorASource = @"
                local http = game:GetService('HttpService')
                hooks_on('http_attack', function()
                    task.spawn(function()
                        local firstOk = pcall(function() return " + request + @" end)
                        local secondOk, secondValue = pcall(function() return " + request + @" end)
                        store_set('first_ok', tostring(firstOk))
                        store_set('second_ok', tostring(secondOk))
                        store_set('second_value', tostring(secondValue))
                        store_set('done', 'yes')
                    end)
                end)";
            string actorBSource = HostileExpressionSource(request);
            LoadHostileMod(stack, "actor_a_mod", "actor-a", actorASource);
            LoadHostileMod(stack, "actor_b_mod", "actor-b", actorBSource);

            stack.Runtime.EmitEvent("http_attack", "");
            TickUntilComplete(stack, store, "actor_a_mod", "actor_b_mod");

            Assert.AreEqual("true", store.Get("actor_a_mod", "first_ok"));
            Assert.AreEqual("false", store.Get("actor_a_mod", "second_ok"));
            StringAssert.Contains("rate limit refused actor 'actor-a'",
                store.Get("actor_a_mod", "second_value"));
            Assert.AreEqual("true", store.Get("actor_b_mod", "ok"));
            CollectionAssert.AreEquivalent(
                new[] { "actor-a", "actor-a", "actor-b" }, policy.ActorIds);
            CollectionAssert.DoesNotContain(policy.ActorIds, "forged-victim");
            Assert.AreEqual(2, transport.Destinations.Count);
        }

        [Test]
        public void HostileMod_JsonAggregateBounds_AreSymmetricAndLoud()
        {
            LuaCsRbxApiBindings rbxApi = new();
            MemoryStore store = new();
            LuaCsModStack stack = BuildProductionStack(
                rbxApi, new FakeCommandSink(), store);
            string source = @"
                local http = game:GetService('HttpService')
                hooks_on('http_attack', function()
                    local entries = {}
                    for index = 1, 100001 do
                        entries[tostring(index)] = 0
                    end
                    local encodeOk, encodeValue = pcall(function()
                        return http:JSONEncode(entries)
                    end)
                    local oversizedJson = '[' .. string.rep('0,', 100000) .. '0]'
                    local decodeOk, decodeValue = pcall(function()
                        return http:JSONDecode(oversizedJson)
                    end)
                    store_set('encode_ok', tostring(encodeOk))
                    store_set('encode_value', encodeOk and 'unexpected-success' or tostring(encodeValue))
                    store_set('decode_ok', tostring(decodeOk))
                    store_set('decode_value', decodeOk and 'unexpected-success' or tostring(decodeValue))
                    store_set('done', 'yes')
                end)";

            LoadHostileMod(stack, "hostile_json", "hostile-actor", source);
            stack.Runtime.EmitEvent("http_attack", "");
            TickUntilComplete(stack, store, "hostile_json");

            Assert.AreEqual("false", store.Get("hostile_json", "encode_ok"));
            Assert.AreEqual("false", store.Get("hostile_json", "decode_ok"));
            StringAssert.Contains("100000 aggregate entry limit",
                store.Get("hostile_json", "encode_value"));
            StringAssert.Contains("100000 aggregate entry limit",
                store.Get("hostile_json", "decode_value"));
        }

        [Test]
        public void HostileMod_JsonInputAndUtf16OutputCaps_RefuseLoudly()
        {
            LuaCsRbxApiBindings rbxApi = new();
            MemoryStore store = new();
            LuaCsModStack stack = BuildProductionStack(
                rbxApi, new FakeCommandSink(), store);
            string source = @"
                local http = game:GetService('HttpService')
                hooks_on('http_attack', function()
                    local encodeOk, encodeValue = pcall(function()
                        return http:JSONEncode(string.rep('x', 1000000))
                    end)
                    local decodeOk, decodeValue = pcall(function()
                        return http:JSONDecode(string.rep('0', 1000000) .. '0')
                    end)
                    store_set('encode_ok', tostring(encodeOk))
                    store_set('encode_value', tostring(encodeValue))
                    store_set('decode_ok', tostring(decodeOk))
                    store_set('decode_value', tostring(decodeValue))
                    store_set('done', 'yes')
                end)";

            LoadHostileMod(stack, "hostile_json_caps", "hostile-actor", source);
            stack.Runtime.EmitEvent("http_attack", "");
            TickUntilComplete(stack, store, "hostile_json_caps");

            Assert.AreEqual("false", store.Get("hostile_json_caps", "encode_ok"));
            Assert.AreEqual("false", store.Get("hostile_json_caps", "decode_ok"));
            StringAssert.Contains("1000000 UTF-16 code-unit limit",
                store.Get("hostile_json_caps", "encode_value"));
            StringAssert.Contains("1000000 character limit",
                store.Get("hostile_json_caps", "decode_value"));
        }

        [Test]
        public void HostileMod_ResolverValidatesEveryAddressBeforeTypedTransport()
        {
            LuaCsRbxApiBindings rbxApi = new();
            MemoryStore store = new();
            PassThroughHttpRequestPolicy policy = new();
            RecordingHttpResolver resolver = new("93.184.216.34", "127.0.0.1");
            RecordingHttpTransport transport = new();
            LuaCsModStack stack = BuildProductionStack(
                rbxApi, new FakeCommandSink(), store, policy, transport, resolver);

            RunHostileExpression(stack, store,
                "http:GetAsync('https://api.example.test/data')");

            AssertHostileRefused(store, "resolved destination is local, private, or special");
            Assert.AreEqual(1, resolver.CallCount);
            Assert.AreEqual("api.example.test", resolver.LastHost);
            Assert.AreEqual(0, transport.Destinations.Count);
        }

        [Test]
        public void HostileMod_ValidRfcToken_ReachesValidatedDestinationContract()
        {
            LuaCsRbxApiBindings rbxApi = new();
            MemoryStore store = new();
            RbxAllowlistHttpRequestPolicy policy = new(
                new[] { "https://api.example.test" });
            RecordingHttpResolver resolver = new("93.184.216.34", "93.184.216.35");
            RecordingHttpTransport transport = new();
            LuaCsModStack stack = BuildProductionStack(
                rbxApi, new FakeCommandSink(), store, policy, transport, resolver);
            string tokenName = "X!#$%&'*+-.^_`|~";

            RunHostileExpression(stack, store,
                "http:RequestAsync({ Url = 'https://api.example.test/data', Method = 'POST', "
                + "Body = 'payload', Headers = { [\"" + tokenName + "\"] = 'safe' } })");

            Assert.AreEqual("true", store.Get("hostile", "ok"));
            Assert.AreEqual(1, transport.Destinations.Count);
            RbxValidatedHttpDestination destination = transport.Destinations[0];
            Assert.AreEqual(IPAddress.Parse("93.184.216.34"), destination.Address);
            Assert.AreEqual("api.example.test", destination.Request.Uri.DnsSafeHost);
            Assert.AreEqual("safe", destination.Request.Headers[tokenName]);
            Assert.AreEqual(1, resolver.CallCount);
        }

        [Test]
        public void HostileMod_RedirectResponse_IsDeniedAfterOneExchange()
        {
            LuaCsRbxApiBindings rbxApi = new();
            MemoryStore store = new();
            PassThroughHttpRequestPolicy policy = new();
            RecordingHttpResolver resolver = new("93.184.216.34");
            RecordingHttpTransport transport = new(302, "Found", "redirect body");
            LuaCsModStack stack = BuildProductionStack(
                rbxApi, new FakeCommandSink(), store, policy, transport, resolver);

            RunHostileExpression(stack, store,
                "http:RequestAsync({ Url = 'https://api.example.test/data', Method = 'GET' })");

            AssertHostileRefused(store, "HTTP redirects are forbidden");
            Assert.AreEqual(1, resolver.CallCount);
            Assert.AreEqual(1, transport.Destinations.Count);
        }

        [Test]
        public void WaveDirector_SourceAvoidsWithheldBuildApis_KeepsReadTierExists()
        {
            string source = WaveDirectorSource();
            AssertNoWithheldBuildApiCall(source, "WaveDirectorMod.lua.txt");
            StringAssert.Contains("coreai_world_exists", source,
                "The Boss guard is a Read-tier API and must stay in the mod.");
        }

        [Test]
        public void WaveDirector_LoadsAndRuns_UnderProductionComposition()
        {
            LuaCsRbxApiBindings rbxApi = new();
            FakeCommandSink sink = new();
            LuaCsModStack stack = BuildProductionStack(rbxApi, sink, new MemoryStore());

            stack.Runtime.LoadMod("wave_director", WaveDirectorSource(),
                LuaCapabilities.Read | LuaCapabilities.WorldEdit, persistToStore: false);
            Assert.IsTrue(stack.Runtime.IsLoaded("wave_director"));

            stack.Runtime.EmitEvent("wave_started", "1");
            stack.Runtime.Tick(0);

            RbxInstance workspace = Workspace(rbxApi);
            Assert.IsNotNull(workspace.FindFirstChild("wave1_enemy1"),
                "Wave 1 must spawn its first enemy as an Rbx part.");
            Assert.IsNotNull(workspace.FindFirstChild("wave1_enemy3"),
                "Wave 1 spawns 2 + 1 = 3 enemies.");
            Assert.IsNull(workspace.FindFirstChild("wave1_enemy4"));
            Assert.AreEqual(0, sink.Commands.Count,
                "The ported mod must never route a coreai_world_* command.");

            // WHY: no scene Boss exists yet, so the recolor timer must stay a no-op.
            stack.Runtime.Tick(4.0);
            Assert.IsNull(workspace.FindFirstChild("Boss"),
                "Without a scene Boss the recolor half stays inert.");

            GameObject sceneBoss = new("Boss");
            try
            {
                stack.Runtime.Tick(4.0);
                RbxInstance overlay = workspace.FindFirstChild("Boss");
                Assert.IsNotNull(overlay,
                    "With a scene Boss present the mod lays down its Rbx overlay part.");
                Assert.IsTrue(overlay.IsA("BasePart"));
                Assert.IsTrue(rbxApi.PartSink.TryGetPartProperties(overlay.Id, out PartProperties props),
                    "The overlay recolor must reach the part-property sink.");
                Assert.AreEqual(RbxColor3.FromHex("#ffaa00"), props.Color,
                    "Wave 1 picks colors[(1 % 4) + 1] = #ffaa00.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(sceneBoss);
            }

            Assert.IsTrue(stack.Runtime.IsLoaded("wave_director"),
                "No handler may have errored into quarantine.");
            Assert.AreEqual(0, sink.Commands.Count);
        }

        [Test]
        public void Tetris_SourceAvoidsWithheldBuildApis()
        {
            AssertNoWithheldBuildApiCall(TetrisSource(), "LuaPlatformExampleController.TetrisSource");
        }

        [Test]
        public void Tetris_LoadsAndPlaysItself_UnderProductionComposition()
        {
            LuaCsRbxApiBindings rbxApi = new();
            FakeCommandSink sink = new();
            LuaCsModStack stack = BuildProductionStack(rbxApi, sink, new MemoryStore());

            stack.Runtime.LoadMod("tetris3d", TetrisSource(),
                LuaCapabilities.All, persistToStore: false);
            Assert.IsTrue(stack.Runtime.IsLoaded("tetris3d"));

            RbxInstance workspace = Workspace(rbxApi);
            RbxInstance root = workspace.FindFirstChild("TetrisRoot_g1");
            Assert.IsNotNull(root, "The playfield root folder must be built on load.");
            Assert.IsNotNull(root.FindFirstChild("tz1_wl1"), "Left wall of row 1 is missing.");
            Assert.IsNotNull(root.FindFirstChild("tz1_wr14"), "Right wall of row 14 is missing.");
            Assert.IsNotNull(root.FindFirstChild("tz1_wf1"), "Floor is missing.");
            Assert.IsNotNull(root.FindFirstChild("tz1_a1"), "Active-piece cube 1 is missing.");

            // Autopilot gravity: 14 falls to land the first piece; 120 x 0.1 s ticks cover it.
            for (int i = 0; i < 120; i++)
            {
                stack.Runtime.Tick(0.1);
            }

            bool lockedCube = false;
            foreach (RbxInstance child in root.GetChildren())
            {
                if (child.Name.StartsWith("tz1_c", StringComparison.Ordinal))
                {
                    lockedCube = true;
                    break;
                }
            }

            Assert.IsTrue(lockedCube, "The autopilot must lock at least one piece onto the board.");
            Assert.IsTrue(stack.Runtime.IsLoaded("tetris3d"),
                "No handler may have errored into quarantine.");
            Assert.AreEqual(0, sink.Commands.Count,
                "The ported mod must never route a coreai_world_* command.");
        }
    }
}
