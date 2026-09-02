using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Authority;
using CoreAI.Infrastructure.Logging;
using CoreAI.Logging;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Networking;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.Acceptance
{
    /// <summary>
    /// G11 browser probe (2026-09-02): the AI's one-off <c>execute_lua</c> chunk has no scheduler
    /// signal-wait bridge, so a refused <c>HttpService:GetAsync</c> surfaced the bridge error instead of
    /// the host's refusal. Synchronous refusals (policy, safety, rate) must reach every execution
    /// context as the configured refusal message; the async transport path still needs a mod thread.
    /// </summary>
    [TestFixture]
    public sealed class HttpServiceOneOffRefusalEditModeTests
    {
        private const LuaCapabilities Capabilities =
            LuaCapabilities.Read | LuaCapabilities.WorldEdit;

        [Test]
        public async Task OneOffExecuteLua_RefusedGetAsync_RaisesTheHostRefusal_NotTheWaitBridgeError()
        {
            using Harness harness = new();
            ActorContext actor = harness.Actor("http-probe-actor");
            const string code = @"
                local http = game:GetService('HttpService')
                local ok, err = pcall(function() return http:GetAsync('http://127.0.0.1:9/probe') end)
                return tostring(ok) .. '|' .. tostring(err)";

            LuaTool.LuaResult result = await harness.Stack.ToolExecutor.ExecuteAsync(
                code, actor, CancellationToken.None);

            Assert.IsTrue(result.Success, result.Error);
            StringAssert.StartsWith("false|", result.Output);
            StringAssert.Contains("HttpService policy refused actor 'http-probe-actor'", result.Output);
            StringAssert.DoesNotContain("Wait bridge is unavailable", result.Output);
        }

        [Test]
        public async Task OneOffExecuteLua_RefusedPostAndRequestAsync_RaiseTheHostRefusal()
        {
            using Harness harness = new();
            ActorContext actor = harness.Actor("http-probe-actor");
            const string code = @"
                local http = game:GetService('HttpService')
                local okPost, errPost = pcall(function() return http:PostAsync('http://127.0.0.1:9/p', '{}') end)
                local okReq, errReq = pcall(function() return http:RequestAsync({ Url = 'http://127.0.0.1:9/r', Method = 'GET' }) end)
                return tostring(okPost) .. '|' .. tostring(errPost) .. '||' .. tostring(okReq) .. '|' .. tostring(errReq)";

            LuaTool.LuaResult result = await harness.Stack.ToolExecutor.ExecuteAsync(
                code, actor, CancellationToken.None);

            Assert.IsTrue(result.Success, result.Error);
            string[] halves = result.Output.Split(new[] { "||" }, StringSplitOptions.None);
            Assert.AreEqual(2, halves.Length, result.Output);
            foreach (string half in halves)
            {
                StringAssert.StartsWith("false|", half);
                StringAssert.Contains("HttpService policy refused actor 'http-probe-actor'", half);
                StringAssert.DoesNotContain("Wait bridge is unavailable", half);
            }
        }

        private sealed class Harness : IDisposable
        {
            public Harness()
            {
                Registry = new InstanceRegistry(
                    worldAclVersion: InstanceRegistry.CurrentWorldAclVersion,
                    worldId: "http-one-off-world");
                RbxDataModel game = DataModelBootstrap.CreateGame(Registry);
                Bindings = new LuaCsRbxApiBindings(
                    Registry, game, networkBridge: new NullNetworkBridge());
                Stack = LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
                {
                    Logger = new SilentGameLogger(),
                    ModStore = new MemoryStore(),
                    Capabilities = Capabilities,
                    OneOffCapabilities = Capabilities,
                    RbxApi = Bindings
                });
            }

            public InstanceRegistry Registry { get; }

            public LuaCsRbxApiBindings Bindings { get; }

            public LuaCsModStack Stack { get; }

            public ActorContext Actor(string actorId)
            {
                return new LocalActorIdentityProvider(
                        actorId,
                        "session-" + actorId,
                        Registry.WorldId,
                        ActorGrantSet.None,
                        AgentMemoryScope.Empty)
                    .GetActorContext(BuiltInAgentRoleIds.Programmer);
            }

            public void Dispose()
            {
                Bindings.Dispose();
            }
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
                foreach ((string ModId, string Key) key in _values.Keys)
                {
                    if (key.ModId == modId)
                    {
                        keys.Add(key);
                    }
                }

                foreach ((string ModId, string Key) key in keys)
                {
                    _values.Remove(key);
                }
            }
        }

        private sealed class SilentGameLogger : IGameLogger
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
    }
}
