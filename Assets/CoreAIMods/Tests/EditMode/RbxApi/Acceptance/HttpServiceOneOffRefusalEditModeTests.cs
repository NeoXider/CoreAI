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

        [Test]
        public async Task OneOffExecuteLua_RefusedHostCall_PcallXpcallAndResumeGetOnlyTheRbxErrorLine()
        {
            using Harness harness = new();
            ActorContext actor = harness.Actor("errtext-actor");
            const string caught = @"
                local function connect() return workspace.ChildAdded:Connect(function() end) end
                local okP, errP = pcall(connect)
                local okX, errX = xpcall(connect, function(e) return e end)
                local okR, errR = coroutine.resume(coroutine.create(connect))
                return table.concat({ tostring(okP), type(errP), tostring(errP), tostring(okX), type(errX),
                    tostring(errX), tostring(okR), type(errR), tostring(errR) }, '||')";

            LuaTool.LuaResult protectedRun = await harness.Stack.ToolExecutor.ExecuteAsync(
                caught, actor, CancellationToken.None);
            LuaTool.LuaResult uncaughtRun = await harness.Stack.ToolExecutor.ExecuteAsync(
                "workspace.ChildAdded:Connect(function() end)", actor, CancellationToken.None);

            // WHY the uncaught run is the reference: its error is the existing observable the model and
            // auto-repair classify by code, and it never carried the host exception's ToString().
            Assert.IsFalse(uncaughtRun.Success, "an uncaught refusal must fail the execute_lua call");
            StringAssert.StartsWith(
                "CONTEXT_VIOLATION: Workspace.ChildAdded:Connect requires a persistent owning mod id | fix: ",
                uncaughtRun.Error, "the uncaught refusal must still read as its RbxError code");
            AssertIsOnlyTheErrorLine(uncaughtRun.Error);

            Assert.IsTrue(protectedRun.Success, protectedRun.Error);
            string[] parts = protectedRun.Output.Split(new[] { "||" }, StringSplitOptions.None);
            Assert.AreEqual(9, parts.Length, protectedRun.Output);
            string[] paths = { "pcall", "xpcall", "coroutine.resume" };
            for (int index = 0; index < paths.Length; index++)
            {
                Assert.AreEqual("false", parts[index * 3], paths[index] + " must report the failure");
                Assert.AreEqual("string", parts[index * 3 + 1], paths[index] + " must receive a string error");
                Assert.AreEqual(uncaughtRun.Error, parts[index * 3 + 2],
                    paths[index] + " must receive exactly the refusal line, nothing wrapped around it");
            }

            AssertIsOnlyTheErrorLine(protectedRun.Output);
        }

        [Test]
        public void LoadedMod_HostErrors_ReachPcallAndTheTaskThreadFaultAsTheProductionLine()
        {
            using Harness harness = new();
            harness.Stack.Runtime.LoadMod("errtext-mod", @"
local ok, err = pcall(function() return game:GetService('NoSuchService') end)
store_set('pcall', tostring(err))
task.spawn(function()
    game:GetService('NoSuchService')
end)");

            const string refusal = "UNKNOWN_SERVICE: NoSuchService is not a valid Service name | fix: ";
            string caught = harness.Store.Get("errtext-mod", "pcall");
            StringAssert.StartsWith("[mod:errtext-mod script:main.lua line:2] " + refusal, caught,
                "pcall must receive the production-prefixed line itself");
            AssertIsOnlyTheErrorLine(caught);

            IReadOnlyList<LuaModHandlerError> faults =
                harness.Stack.Runtime.GetRecentHandlerErrors("errtext-mod");
            Assert.AreEqual(1, faults.Count, "the failing task thread must be reported once");
            StringAssert.Contains("[mod:errtext-mod script:main.lua line:5] " + refusal, faults[0].Error,
                "the thread fault must carry the host's code and line, not the nil a protected resume "
                + "reads from an error built over an inner exception");
            AssertIsOnlyTheErrorLine(faults[0].Error);
        }

        /// <summary>
        /// Fails when <paramref name="text"/> carries anything of the host exception beyond its message:
        /// a CLR type name, a managed stack frame or an absolute source path.
        /// </summary>
        private static void AssertIsOnlyTheErrorLine(string text)
        {
            StringAssert.DoesNotContain("Exception", text, "no CLR exception type name may leak: " + text);
            StringAssert.DoesNotContain("   at ", text, "no managed stack frame may leak: " + text);
            StringAssert.DoesNotContain("/Assets/", text, "no source path may leak: " + text);
            StringAssert.DoesNotContain(":\\", text, "no Windows source path may leak: " + text);
            StringAssert.DoesNotContain("\n", text, "the error must stay one line: " + text);
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
