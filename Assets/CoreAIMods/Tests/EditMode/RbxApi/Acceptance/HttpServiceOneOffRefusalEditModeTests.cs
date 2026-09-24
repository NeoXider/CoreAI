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
    /// The same ownerless surface must refuse signal connections loudly (M2-03).
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

        [Test]
        public async Task OneOffExecuteLua_SignalConnections_AreRefusedAndLaterFramesKeepRunningMods()
        {
            using Harness harness = new();
            ActorContext actor = harness.Actor("connect-probe-actor");
            RbxInstance target = harness.Registry.Create(
                "Folder", accessScope: InstanceAccessScope.SharedWritable);
            target.Name = "OneOffTarget";
            target.Parent = harness.Registry.WorldRoot;
            const string code = @"
                local function refusal(call)
                    local ok, err = pcall(call)
                    return tostring(ok) .. '|' .. tostring(err)
                end
                local results = {
                    refusal(function() return workspace.ChildAdded:Connect(function() end) end),
                    refusal(function() return workspace.ChildAdded:Once(function() end) end),
                    refusal(function() return workspace.ChildAdded:ConnectParallel(function() end) end),
                    refusal(function() return workspace.ChildAdded:Wait() end)
                }
                local target = workspace:FindFirstChild('OneOffTarget')
                target:SetAttribute('TouchedByOneOff', true)
                target.Name = 'OneOffRenamed'
                return table.concat(results, '||')";

            LuaTool.LuaResult result = await harness.Stack.ToolExecutor.ExecuteAsync(
                code, actor, CancellationToken.None);

            Assert.IsTrue(result.Success, result.Error);
            string[] refusals = result.Output.Split(new[] { "||" }, StringSplitOptions.None);
            Assert.AreEqual(4, refusals.Length, result.Output);
            string[] members = { "Connect", "Once", "ConnectParallel", "Wait" };
            for (int index = 0; index < refusals.Length; index++)
            {
                string refusal = refusals[index];
                StringAssert.StartsWith("false|", refusal,
                    members[index] + " on the ownerless surface must fail loudly: " + refusal);
                StringAssert.Contains("CONTEXT_VIOLATION", refusal, members[index]);
                StringAssert.Contains(members[index] + " requires a persistent owning mod id", refusal);
                StringAssert.Contains("ownerless one-off executor", refusal,
                    "the refusal must use the wording task.* uses on the same surface");
            }

            Assert.AreEqual(true, target.GetAttribute("TouchedByOneOff"),
                "the one-off surface still calls methods");
            Assert.AreEqual("OneOffRenamed", target.Name, "the one-off surface still writes properties");

            // WHY the mod half: before the refusal, the one-off's untracked connection threw
            // CONTEXT_VIOLATION out of the scheduler frame on every later ChildAdded, so no mod's
            // handler ran again until the world was reloaded. The name filter ignores the one-off
            // actor's auto-loaded character, which joins Workspace on the same Advance.
            harness.Stack.Runtime.LoadMod("connect-probe-mod", @"
                workspace.ChildAdded:Connect(function(child)
                    if child.Name == 'ModMarker' then
                        store_set('added', child.Name)
                    end
                end)
                local marker = Instance.new('Folder')
                marker.Name = 'ModMarker'
                marker.Parent = workspace");

            Assert.DoesNotThrow(() => harness.Bindings.Scheduler.Advance(0d));
            Assert.AreEqual("ModMarker", harness.Store.Get("connect-probe-mod", "added"),
                "a loaded mod keeps connecting and firing after the one-off was refused");
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
                Store = new MemoryStore();
                Stack = LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
                {
                    Logger = new SilentGameLogger(),
                    ModStore = Store,
                    Capabilities = Capabilities,
                    OneOffCapabilities = Capabilities,
                    RbxApi = Bindings
                });
            }

            public InstanceRegistry Registry { get; }

            public MemoryStore Store { get; }

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
