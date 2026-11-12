using System;
using System.Collections.Generic;
using System.Threading;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Infrastructure.Logging;
using CoreAI.Mods.Rbx.Binding;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Networking;
using CoreAI.Mods.WorldPackages;
using CoreAI.Tests.EditMode.RbxApi.Acceptance;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.ScriptsLua
{
    /// <summary>
    /// MVP4 step C5 (SC-16 and the SC-9 amendment), in process: the server captures the world for one joining client
    /// (<see cref="RbxClientJoinPayload.CaptureForRecipient"/>), which keeps only client and shared sources and nothing
    /// under ServerScriptService or ServerStorage; the client stages it into a replica registry
    /// (<see cref="RbxClientWorldComposition"/>), mints no Player, and starts its client scripts once the replica holds
    /// the local player. A payload with a server or legacy source is refused. Every rule has its negative twin.
    /// </summary>
    [TestFixture]
    public sealed class ClientCompositionEditModeTests
    {
        private const string ServerHeader = "-- @coreai context: server\n";
        private const string ClientHeader = "-- @coreai context: client\n";
        private const string SharedHeader = "-- @coreai context: shared\n";
        private const string Recipient = "player-1";
        private const string Hud = ClientHeader
                                   + "local util = require(game:GetService('ReplicatedStorage').util)\n"
                                   + "script:SetAttribute('side', tostring(game:GetService('RunService'):IsClient()) "
                                   + ".. '|' .. util.name)";

        private static readonly DateTime CapturedAtUtc = new(2026, 11, 12, 18, 0, 0, DateTimeKind.Utc);

        private SynchronizationContext _savedContext;
        private ScriptWorldPackageEditModeTests.World _server;

        [SetUp]
        public void SetUp()
        {
            _savedContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
            _server = new ScriptWorldPackageEditModeTests.World();
        }

        [TearDown]
        public void TearDown()
        {
            _server?.Dispose();
            _server = null;
            SynchronizationContext.SetSynchronizationContext(_savedContext);
        }

        private RbxWorldPackageCaptureContext CaptureContext()
        {
            return new RbxWorldPackageCaptureContext(_server.Registry, _server.Roblox.Game, _server.Roblox.PartSink,
                WorldPackageTestWorlds.NewSettingsFor(_server.Registry.WorldId), null, _server.Sources, CapturedAtUtc);
        }

        /// <summary>Every kind of source: legacy, server, a server script inside a Workspace part, client, shared, server-only shared.</summary>
        private void BuildMixedWorld()
        {
            Part("Gate", _server.Registry.WorldRoot);
            _server.Load("legacy", "local x = 1");
            _server.Load("door", ServerHeader + "local x = 1");
            _server.Load("gate", ServerHeader + "-- @coreai parent: Workspace/Gate\nlocal x = 1");
            _server.Load("util", SharedHeader + "return { name = 'util' }");
            _server.Load("secret", SharedHeader + "-- @coreai parent: ServerStorage\nreturn {}");
            _server.Load("hud", Hud);
        }

        private RbxInstance Part(string name, RbxInstance parent)
        {
            RbxInstance part = _server.Registry.Create("Part");
            part.Name = name;
            PartProperties properties = PartProperties.CreateDefault();
            _server.Roblox.PartSink.SetPartProperties(part.Id, in properties);
            part.Parent = parent;
            return part;
        }

        private static List<string> ModIds(RbxWorldPackagePayload payload)
        {
            List<string> ids = new();
            foreach (RbxWorldModSource mod in payload.Mods)
            {
                ids.Add(mod.Manifest.Id);
            }

            ids.Sort(StringComparer.Ordinal);
            return ids;
        }

        private static InstanceSnapshot Node(RbxWorldPackagePayload payload, string name)
        {
            foreach (InstanceSnapshot node in payload.Tree.Instances)
            {
                if (node.Name == name)
                {
                    return node;
                }
            }

            return null;
        }

        private static RbxClientWorldComposition Join(RbxWorldPackagePayload payload)
        {
            return RbxClientWorldComposition.Join(payload, new RbxClientWorldCompositionOptions
            {
                LocalActorId = Recipient,
                NetworkBridge = new TopologyBridge(RbxNetworkTopology.Client),
                Logger = new SilentGameLogger()
            });
        }

        // ---- the join snapshot -----------------------------------------------------------------------

        [Test]
        public void Mvp4_JoinSnapshot_CarriesNoServerOrLegacySource()
        {
            BuildMixedWorld();

            RbxWorldPackagePayload full = RbxWorldPackageSerializer.Capture(CaptureContext());
            RbxWorldPackagePayload join = RbxClientJoinPayload.CaptureForRecipient(CaptureContext(), Recipient);

            CollectionAssert.IsSubsetOf(new[] { "door", "gate", "legacy", "secret" }, ModIds(full));
            CollectionAssert.AreEqual(new[] { "hud", "util" }, ModIds(join));
            Assert.IsNull(Node(join, "door"), "nothing under ServerScriptService reaches a client");
            Assert.IsNull(Node(join, "secret"), "nothing under ServerStorage reaches a client");
            Assert.IsNotNull(Node(join, "ServerStorage"), "the service stays, as the empty shell a client has");
            InstanceSnapshot gate = Node(join, "gate");
            Assert.IsNotNull(gate, "a server script inside a Workspace part is visible to every client");
            Assert.AreEqual("", gate.Script.SourceId, "it reaches the client unbound, without its source");
            Assert.AreEqual("gate", Node(full, "gate").Script.SourceId, "the server's own capture is unchanged");
            CollectionAssert.IsEmpty(join.Diagnostics);
            Assert.DoesNotThrow(() => RbxClientJoinPayload.RefuseServerContent(join));
        }

        [Test]
        public void Mvp4_JoinSnapshot_CarriesVisibleClientAndSharedSources()
        {
            BuildMixedWorld();

            RbxWorldPackagePayload join = RbxClientJoinPayload.CaptureForRecipient(CaptureContext(), Recipient);

            Assert.AreEqual("hud", Node(join, "hud").Script.SourceId);
            Assert.AreEqual("util", Node(join, "util").Script.SourceId);
            foreach (RbxWorldModSource mod in join.Mods)
            {
                StringAssert.Contains(mod.Manifest.Id == "hud" ? "context: client" : "context: shared", mod.Source);
            }
        }

        // ---- the client composition ------------------------------------------------------------------

        [Test]
        public void Mvp4_ClientComposition_StagesReplica_RunsOnlyClientScripts()
        {
            BuildMixedWorld();
            RbxWorldPackagePayload join = RbxClientJoinPayload.CaptureForRecipient(CaptureContext(), Recipient);

            using RbxClientWorldComposition client = Join(join);

            Assert.AreEqual(RegistryAuthority.Replica, client.Registry.Authority);
            Assert.AreEqual(0, client.Started, "the client script waits for the local player");
            CollectionAssert.Contains(client.NotStarted, "hud (waiting-for-local-player)");
            CollectionAssert.Contains(client.NotStarted, "util (module)");
            Assert.IsFalse(client.Stack.Runtime.IsLoaded("door"));
            Assert.IsFalse(client.Stack.Runtime.IsLoaded("legacy"));

            using (client.Registry.BeginReplicationApply())
            {
                client.Roblox.Players.EnsureActor(client.Registry, Recipient);
            }

            client.Stack.Runtime.Tick(0.1d);

            Assert.IsTrue(client.Roblox.TryGetExecutingScript("hud", out RbxInstance hud));
            Assert.AreEqual("true|util", Convert.ToString(hud.GetAttribute("side")),
                "the client script runs as the client and requires the shared module on this side");
        }

        [Test]
        public void Mvp4_ClientComposition_MintsNoPlayer()
        {
            BuildMixedWorld();
            RbxWorldPackagePayload join = RbxClientJoinPayload.CaptureForRecipient(CaptureContext(), Recipient);

            using RbxClientWorldComposition client = Join(join);
            client.Stack.Runtime.Tick(0.1d);

            CollectionAssert.IsEmpty(client.Roblox.Players.GetPlayers(),
                "a replica admits players only through replication (SC-15)");
        }

        [Test]
        public void Mvp4_ClientComposition_RefusesAPayloadWithAServerSource()
        {
            _server.Load("door", ServerHeader + "local x = 1");
            RbxWorldPackagePayload full = RbxWorldPackageSerializer.Capture(CaptureContext());

            RbxWorldPackageException refused = Assert.Throws<RbxWorldPackageException>(() => Join(full));

            StringAssert.Contains("mod 'door', a server source", refused.Message);
        }

        [Test]
        public void Mvp4_ClientComposition_RefusesAPayloadWithALegacySource()
        {
            _server.Load("legacy", "local x = 1");
            RbxWorldPackagePayload full = RbxWorldPackageSerializer.Capture(CaptureContext());

            RbxWorldPackageException refused = Assert.Throws<RbxWorldPackageException>(() => Join(full));

            StringAssert.Contains("mod 'legacy', a legacy (it declares no context) source", refused.Message,
                "the runtime starts legacy mods on a client topology (SC-8), so the composition keeps them off it");
        }

        [Test]
        public void Mvp4_ClientComposition_RefusesAPayloadWithAnInstanceInServerStorage()
        {
            Part("Vault", _server.Roblox.Game.FindFirstChildOfClass("ServerStorage"));
            RbxWorldPackagePayload full = RbxWorldPackageSerializer.Capture(CaptureContext());

            RbxWorldPackageException refused = Assert.Throws<RbxWorldPackageException>(() => Join(full));

            StringAssert.Contains("Part 'Vault' under ServerStorage", refused.Message);
            Assert.IsNull(Node(RbxClientJoinPayload.CaptureForRecipient(CaptureContext(), Recipient), "Vault"),
                "twin: the join snapshot leaves it on the server");
        }

        [Test]
        public void Mvp4_JoinSnapshot_AReferenceToAServerOnlyInstance_ReadsAsNil_AndTheClientStagesIt()
        {
            RbxInstance vault = Part("Vault", _server.Roblox.Game.FindFirstChildOfClass("ServerStorage"));
            RbxObjectValue pointer = (RbxObjectValue)_server.Registry.Create("ObjectValue");
            pointer.Name = "Pointer";
            pointer.Value = vault;
            pointer.Parent = _server.Registry.WorldRoot;

            RbxWorldPackagePayload join = RbxClientJoinPayload.CaptureForRecipient(CaptureContext(), Recipient);

            Assert.AreEqual(0UL, Node(join, "Pointer").Value.ObjectTargetId);
            Assert.AreNotEqual(0UL, Node(RbxWorldPackageSerializer.Capture(CaptureContext()), "Pointer").Value.ObjectTargetId,
                "the server's own capture keeps the reference");
            using RbxClientWorldComposition client = Join(join);
            Assert.IsNotNull(client.Registry.WorldRoot.FindFirstChild("Pointer"));
        }

        [Test]
        public void Mvp4_ClientComposition_ANegative_AWorldOfClientScriptsOnly_IsAccepted()
        {
            _server.Load("util", SharedHeader + "return { name = 'util' }");
            _server.Load("hud", Hud);
            RbxWorldPackagePayload full = RbxWorldPackageSerializer.Capture(CaptureContext());

            using RbxClientWorldComposition client = Join(full);

            CollectionAssert.Contains(client.NotStarted, "hud (waiting-for-local-player)");
        }

        // ---- harness ---------------------------------------------------------------------------------

        /// <summary>A loopback bridge that reports another topology; nothing in these tests sends a remote.</summary>
        private sealed class TopologyBridge : INetworkBridge
        {
            private readonly List<string> _actors = new();

            public TopologyBridge(RbxNetworkTopology topology)
            {
                Topology = topology;
            }

            public RbxNetworkTopology Topology { get; }

            public IReadOnlyList<string> ActorIds => _actors;

            public event Action<RbxNetworkEventMessage> EventReceived
            {
                add { }
                remove { }
            }

            public event Action<RbxNetworkRequestMessage, RbxNetworkRequestResponder> RequestReceived
            {
                add { }
                remove { }
            }

            public event Action<RbxNetworkPeerDisconnected> PeerDisconnected
            {
                add { }
                remove { }
            }

            public int MaxPayloadBytes => 65536;

            public double ServerClockOffsetSeconds => 0d;

            public void RegisterActor(string actorId)
            {
                if (!_actors.Contains(actorId))
                {
                    _actors.Add(actorId);
                }
            }

            public void UnregisterActor(string actorId)
            {
                _actors.Remove(actorId);
            }

            public void SendEvent(RbxNetworkEventMessage message)
            {
            }

            public void SendRequest(RbxNetworkRequestMessage message, Action<RbxNetworkResponse> response)
            {
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
