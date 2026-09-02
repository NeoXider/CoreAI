using System;
using System.Collections.Generic;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Authority;
using CoreAI.Infrastructure.Logging;
using CoreAI.Logging;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Networking;
using Lua;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode.RbxApi.Acceptance
{
    /// <summary>Production teardown and bounded actor-churn coverage.</summary>
    [TestFixture]
    public sealed class RungZeroDisconnectEditModeTests
    {
        private const LuaCapabilities Capabilities =
            LuaCapabilities.Read | LuaCapabilities.WorldEdit;

        [Test]
        public void ConnectDisconnect_UsesProductionSeam_IsIdempotentAndIsolated()
        {
            using ProductionHarness harness = new ProductionHarness();
            ActorContext actorA = harness.Actor("disconnect-a");
            ActorContext actorB = harness.Actor("disconnect-b");
            Dictionary<string, int> added = new(StringComparer.Ordinal);
            Dictionary<string, int> removing = new(StringComparer.Ordinal);
            RbxEnumItem removalReason = null;
            harness.Bindings.Players.PlayerAdded.Connect(
                (Action<object[]>)(arguments =>
                {
                    RbxPlayer player = (RbxPlayer)arguments[0];
                    Increment(added, player.NetworkActorId);
                }));
            harness.Bindings.Players.PlayerRemoving.Connect(
                (Action<object[]>)(arguments =>
                {
                    RbxPlayer player = (RbxPlayer)arguments[0];
                    removalReason = (RbxEnumItem)arguments[1];
                    Increment(removing, player.NetworkActorId);
                }));

            harness.Bindings.ConnectActor(actorA);
            harness.Bindings.ConnectActor(actorA);
            harness.Bindings.ConnectActor(actorB);
            harness.Bindings.Scheduler.Advance(0d);

            RbxRemoteEvent remote =
                (RbxRemoteEvent)harness.Registry.Create("RemoteEvent");
            remote.GetOnClientEvent(actorA.ActorId);
            remote.GetOnClientEvent(actorB.ActorId);
            harness.SendClientEvent(remote, actorA.ActorId);
            harness.SendClientEvent(remote, actorB.ActorId);
            harness.Stack.Runtime.LoadMod(actorA, "disconnect-mod-a", @"
                task.spawn(function()
                    task.wait(1000)
                end)", persistToStore: false);
            harness.Stack.Runtime.LoadMod(actorB, "disconnect-mod-b", @"
                task.spawn(function()
                    task.wait(1000)
                end)", persistToStore: false);
            int threadsBeforeDisconnect =
                harness.Bindings.Scheduler.LiveThreadCount;

            Assert.IsTrue(harness.Bindings.DisconnectActor(actorA));
            harness.Bindings.Scheduler.Advance(0d);

            Assert.AreEqual(1, added[actorA.ActorId]);
            Assert.AreEqual(1, removing[actorA.ActorId]);
            Assert.AreEqual("PlayerExitReason", removalReason.EnumType.Name);
            Assert.AreEqual("Unknown", removalReason.Name);
            Assert.IsFalse(harness.Bindings.Players.TryGetByActorId(
                actorA.ActorId, out _));
            Assert.IsTrue(harness.Bindings.Players.TryGetByActorId(
                actorB.ActorId, out _));
            CollectionAssert.DoesNotContain(
                harness.Bridge.ActorIds, actorA.ActorId);
            CollectionAssert.Contains(harness.Bridge.ActorIds, actorB.ActorId);
            Assert.AreEqual(1, remote.ClientSignalCount);
            Assert.IsFalse(remote.HasActor(actorA.ActorId));
            Assert.IsTrue(remote.HasActor(actorB.ActorId));
            Assert.AreEqual(1, harness.Bridge.RateWindowCount);
            Assert.Less(
                harness.Bindings.Scheduler.LiveThreadCount,
                threadsBeforeDisconnect);
            Assert.Greater(harness.Bindings.Scheduler.LiveThreadCount, 0);
            Assert.AreEqual(1, harness.ChatFactory.ReleaseCount);

            Assert.IsFalse(harness.Bindings.DisconnectActor(actorA));
            harness.Bindings.Scheduler.Advance(0d);
            Assert.AreEqual(1, removing[actorA.ActorId]);
            Assert.AreEqual(1, harness.ChatFactory.ReleaseCount);
            Assert.IsTrue(harness.Bindings.Players.TryGetByActorId(
                actorB.ActorId, out _));
        }

        [Test]
        public void RepeatedConnectDisconnect200Actors_LeavesNoRuntimeGrowth()
        {
            using ProductionHarness harness = new ProductionHarness();
            RbxRemoteEvent remote =
                (RbxRemoteEvent)harness.Registry.Create("RemoteEvent");

            for (int index = 0; index < 200; index++)
            {
                ActorContext actor = harness.Actor("churn-" + index);
                harness.Bindings.ConnectActor(actor);
                remote.GetOnClientEvent(actor.ActorId);
                harness.SendClientEvent(remote, actor.ActorId);
                Assert.IsTrue(harness.Bindings.DisconnectActor(actor));
            }

            harness.Bindings.Scheduler.Advance(0d);
            Assert.IsEmpty(harness.Bridge.ActorIds);
            Assert.AreEqual(0, harness.Bridge.RateWindowCount);
            Assert.AreEqual(0, remote.ClientSignalCount);
            Assert.AreEqual(0, harness.Bindings.Scheduler.LiveThreadCount);
            Assert.IsEmpty(harness.Bindings.Players.GetPlayers());
            Assert.AreEqual(200, harness.ChatFactory.ReleaseCount);
        }

        private static void Increment(Dictionary<string, int> counts, string actorId)
        {
            counts[actorId] = counts.TryGetValue(actorId, out int count)
                ? count + 1
                : 1;
        }

        private sealed class ProductionHarness : IDisposable
        {
            public ProductionHarness()
            {
                Registry = new InstanceRegistry(
                    worldAclVersion: InstanceRegistry.CurrentWorldAclVersion,
                    worldId: "disconnect-world");
                RbxDataModel game = DataModelBootstrap.CreateGame(Registry);
                Bridge = new NullNetworkBridge();
                Bindings = new LuaCsRbxApiBindings(
                    Registry, game, networkBridge: Bridge);
                ChatFactory = new RecordingChatFactory();
                Bindings.AttachChatFactory(ChatFactory);
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

            public NullNetworkBridge Bridge { get; }

            public LuaCsRbxApiBindings Bindings { get; }

            public RecordingChatFactory ChatFactory { get; }

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

            public void SendClientEvent(RbxRemoteEvent remote, string actorId)
            {
                LuaCsRbxNetworkCodec codec = new(
                    Registry, RbxEnumRegistry.CreateWithBuiltins(), null);
                byte[] payload = codec.EncodeArguments(new List<LuaValue>());
                remote.FireServer(Bridge, actorId, payload);
            }

            public void Dispose()
            {
                Bindings.Dispose();
            }
        }

        private sealed class RecordingChatFactory : IInGameLlmChatServiceFactory
        {
            public int ReleaseCount { get; private set; }

            public IInGameLlmChatService Resolve(ActorContext actorContext)
            {
                return null;
            }

            public bool ReleaseActor(ActorContext actorContext)
            {
                ReleaseCount++;
                return true;
            }
        }

        private sealed class MemoryStore : ILuaModStore
        {
            private readonly Dictionary<(string ModId, string Key), string> _values = new();

            public string Get(string modId, string key)
            {
                return _values.TryGetValue(
                    (modId, key), out string value) ? value : "";
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
                    if (string.Equals(key.ModId, modId,
                            StringComparison.Ordinal))
                    {
                        removed.Add(key);
                    }
                }

                for (int index = 0; index < removed.Count; index++)
                {
                    _values.Remove(removed[index]);
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
