using System;
using System.Collections.Generic;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Authority;
using CoreAI.Infrastructure.Logging;
using CoreAI.Logging;
using CoreAI.Mods.Rbx.Instances;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode.RbxApi.Acceptance
{
    /// <summary>Production-path coverage for server-generated mutation envelopes.</summary>
    [TestFixture]
    public sealed class RungZeroEnvelopeProductionPathsEditModeTests
    {
        private const LuaCapabilities ProductionCapabilities =
            LuaCapabilities.Read | LuaCapabilities.WorldEdit | LuaCapabilities.LogicOverride;

        [Test]
        public void LoadMod_MainChunkMutation_InAclWorld_IsEnveloped()
        {
            using ProductionHarness harness = new ProductionHarness();
            ActorContext actor = harness.Actor("main-actor");
            int before = harness.Registry.RetainedMutationOperationCount;

            harness.Stack.Runtime.LoadMod(actor, "main-envelope", @"
                workspace.Name = 'MainEnvelopeWorkspace'", persistToStore: false);

            Assert.AreEqual("MainEnvelopeWorkspace", harness.Registry.WorldRoot.Name);
            Assert.Greater(harness.Registry.RetainedMutationOperationCount, before);
        }

        [Test]
        public void Scheduler_ResumedTaskMutation_InAclWorld_IsEnveloped()
        {
            using ProductionHarness harness = new ProductionHarness();
            ActorContext actor = harness.Actor("task-actor");
            harness.Stack.Runtime.LoadMod(actor, "task-envelope", @"
                local marker = Instance.new('Folder')
                marker.Name = 'TaskBefore'
                marker.Parent = workspace
                task.spawn(function()
                    task.wait()
                    marker.Name = 'TaskAfter'
                end)", persistToStore: false);
            int beforeResume = harness.Registry.RetainedMutationOperationCount;

            harness.Bindings.Scheduler.Advance(0d);

            Assert.IsNotNull(harness.Registry.WorldRoot.FindFirstChild("TaskAfter"));
            Assert.Greater(harness.Registry.RetainedMutationOperationCount, beforeResume);
        }

        [Test]
        public void Heartbeat_HandlerMutation_InAclWorld_IsEnveloped()
        {
            using ProductionHarness harness = new ProductionHarness();
            ActorContext actor = harness.Actor("heartbeat-actor");
            harness.Stack.Runtime.LoadMod(actor, "heartbeat-envelope", @"
                local marker = Instance.new('Folder')
                marker.Name = 'HeartbeatBefore'
                marker.Parent = workspace
                game:GetService('RunService').Heartbeat:Connect(function()
                    marker.Name = 'HeartbeatAfter'
                end)", persistToStore: false);
            int beforeDispatch = harness.Registry.RetainedMutationOperationCount;

            harness.Bindings.PumpHeartbeat(0.016f);
            harness.Bindings.Scheduler.Advance(0d);

            Assert.IsNotNull(harness.Registry.WorldRoot.FindFirstChild("HeartbeatAfter"));
            Assert.Greater(harness.Registry.RetainedMutationOperationCount, beforeDispatch);
        }

        [Test]
        public void RemoteEvent_HandlerMutation_InAclWorld_IsEnveloped()
        {
            using ProductionHarness harness = new ProductionHarness();
            ActorContext host = CoreAI.Composition.CoreServicesInstaller
                .DefaultLocalHostIdentityProvider
                .GetActorContext(BuiltInAgentRoleIds.Programmer);
            harness.Stack.Runtime.LoadMod(host, "remote-server-envelope", @"
                local marker = Instance.new('Folder')
                marker.Name = 'RemoteBefore'
                marker.Parent = workspace
                local remote = Instance.new('RemoteEvent')
                remote.Name = 'EnvelopeRemote'
                remote.Parent = workspace
                remote.OnServerEvent:Connect(function()
                    marker.Name = 'RemoteAfter'
                end)", persistToStore: false);
            ActorContext client = harness.Actor("remote-client-actor");
            harness.Stack.Runtime.LoadMod(client, "remote-client-envelope", @"
                workspace.EnvelopeRemote:FireServer()", persistToStore: false);
            int beforeDispatch = harness.Registry.RetainedMutationOperationCount;

            harness.Bindings.Scheduler.Advance(0d);

            Assert.IsNotNull(harness.Registry.WorldRoot.FindFirstChild("RemoteAfter"));
            Assert.Greater(harness.Registry.RetainedMutationOperationCount, beforeDispatch);
        }

        [Test]
        public void CrossModExportMutation_UsesCalleeActorEnvelope()
        {
            using ProductionHarness harness = new ProductionHarness();
            ActorContext callee = harness.Actor("callee-actor");
            harness.Stack.Runtime.LoadMod(callee, "callee-envelope", @"
                local marker = Instance.new('Folder')
                marker.Name = 'CrossBefore'
                marker.Parent = workspace
                mods_export('mutate', function()
                    marker.Name = 'CrossAfter'
                    return marker.Name
                end)", persistToStore: false);
            ActorContext caller = harness.Actor("caller-actor");
            int before = harness.Registry.RetainedMutationOperationCount;

            harness.Stack.Runtime.LoadMod(caller, "caller-envelope", @"
                assert(mods_call('callee-envelope', 'mutate') == 'CrossAfter')",
                persistToStore: false);

            Assert.IsNotNull(harness.Registry.WorldRoot.FindFirstChild("CrossAfter"));
            Assert.Greater(harness.Registry.RetainedMutationOperationCount, before);
        }

        [Test]
        public void BareRegistryDestroy_WithoutEnvelope_InAclWorld_IsRefused()
        {
            InstanceRegistry registry = new InstanceRegistry(
                worldAclVersion: InstanceRegistry.CurrentWorldAclVersion,
                worldId: "bare-registry-world");
            RbxInstance owned = registry.Create(
                "Folder", ownerActorId: "bare-actor", accessScope: InstanceAccessScope.Owned);

            RbxError error = Assert.Throws<RbxError>(() =>
                registry.DestroyInstance(owned, "bare-actor", false, "bare-registry-world"));

            StringAssert.Contains("actor 'bare-actor'", error.RawMessage);
            StringAssert.Contains("envelope", error.RawMessage.ToLowerInvariant());
            Assert.IsFalse(owned.IsDestroyed);
        }

        private sealed class ProductionHarness : IDisposable
        {
            public ProductionHarness()
            {
                Registry = new InstanceRegistry(
                    worldAclVersion: InstanceRegistry.CurrentWorldAclVersion,
                    worldId: "production-envelope-world");
                RbxDataModel game = DataModelBootstrap.CreateGame(Registry);
                Bindings = new LuaCsRbxApiBindings(Registry, game);
                Stack = LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
                {
                    Logger = new SilentGameLogger(),
                    ModStore = new MemoryStore(),
                    Capabilities = ProductionCapabilities,
                    OneOffCapabilities = ProductionCapabilities,
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
                List<(string ModId, string Key)> removed = new();
                foreach ((string ModId, string Key) key in _values.Keys)
                {
                    if (string.Equals(key.ModId, modId, StringComparison.Ordinal))
                    {
                        removed.Add(key);
                    }
                }

                foreach ((string ModId, string Key) key in removed)
                {
                    _values.Remove(key);
                }
            }
        }

        private sealed class SilentGameLogger : IGameLogger
        {
            public void LogDebug(GameLogFeature feature, string message,
                UnityEngine.Object context = null)
            {
            }

            public void LogInfo(GameLogFeature feature, string message,
                UnityEngine.Object context = null)
            {
            }

            public void LogWarning(GameLogFeature feature, string message,
                UnityEngine.Object context = null)
            {
            }

            public void LogError(GameLogFeature feature, string message,
                UnityEngine.Object context = null)
            {
            }
        }
    }
}
