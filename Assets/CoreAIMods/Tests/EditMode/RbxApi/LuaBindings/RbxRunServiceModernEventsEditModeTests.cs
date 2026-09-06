using System.Collections.Generic;
using System.Threading;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Infrastructure.Logging;
using CoreAI.Mods.Rbx.Instances;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.LuaBindings
{
    /// <summary>
    /// The modern RunService frame events — PreAnimation, PreSimulation, PostSimulation and
    /// PreRender — through the REAL mod runtime, plus the render-phase gate.
    /// </summary>
    /// <remarks>
    /// WHY these exist: the mirror
    /// (<c>reference/engine/classes/RunService.yaml</c>) documents PreSimulation and PreRender as the
    /// replacements for Stepped and RenderStepped, and PreAnimation/PostSimulation have no legacy
    /// alias at all. Until this file landed, a copy-pasted current Roblox script using
    /// <c>RunService.PreRender:Connect(...)</c> failed with "not a valid member of RunService" —
    /// a silent parity break in the direction the 1:1 rule cares about most, because the modern
    /// names are the ones Roblox's own documentation now teaches.
    /// </remarks>
    [TestFixture]
    public sealed class RbxRunServiceModernEventsEditModeTests
    {
        private SynchronizationContext _savedContext;

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

        [Test]
        public void Lua_ModernEvents_EachFireOncePerFrameWithANumericDelta()
        {
            LuaCsRbxApiBindings roblox = new();
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(roblox, store);

            stack.Runtime.LoadMod("m", @"
                local rs = game:GetService('RunService')
                local counts = { PreAnimation = 0, PreSimulation = 0, PostSimulation = 0,
                                 PreRender = 0 }
                for name, _ in pairs(counts) do
                    local captured = name
                    rs[captured]:Connect(function(dt)
                        counts[captured] = counts[captured] + 1
                        store_set(captured, tostring(counts[captured]))
                        store_set(captured .. '_dt', tostring(dt))
                    end)
                end");

            roblox.PumpFrame(0.25f);
            roblox.Scheduler.Advance(0d);
            roblox.PumpFrame(0.25f);
            roblox.Scheduler.Advance(0d);

            foreach (string name in new[]
                     { "PreAnimation", "PreSimulation", "PostSimulation", "PreRender" })
            {
                Assert.AreEqual("2", store.Get("m", name),
                    name + " must fire exactly once per frame");
                Assert.AreEqual("0.25", store.Get("m", name + "_dt"),
                    name + " must carry the frame delta");
            }
        }

        [Test]
        public void Lua_FrameOrder_FollowsTheMirrorsPhaseOrder()
        {
            LuaCsRbxApiBindings roblox = new();
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(roblox, store);

            // WHY an accumulated string and not per-signal counters: a counter proves each phase
            // ran, which is true no matter how they are ordered. The order IS the contract here —
            // PreSimulation before PostSimulation before Heartbeat is what a script that adjusts
            // forces and then reads the outcome depends on — so the assertion has to be one that
            // a reversed pump would break.
            stack.Runtime.LoadMod("m", @"
                local rs = game:GetService('RunService')
                local order = ''
                local function note(tag)
                    return function()
                        order = order .. tag
                        store_set('order', order)
                    end
                end
                rs.PreAnimation:Connect(note('A'))
                rs.PreSimulation:Connect(note('P'))
                rs.Stepped:Connect(note('s'))
                rs.PostSimulation:Connect(note('O'))
                rs.Heartbeat:Connect(note('H'))
                rs.PreRender:Connect(note('R'))
                rs.RenderStepped:Connect(note('r'))");

            roblox.PumpFrame(0.016f);
            roblox.Scheduler.Advance(0d);

            Assert.AreEqual("APsOHRr", store.Get("m", "order"),
                "The frame must run PreAnimation, PreSimulation (+ legacy Stepped), PostSimulation, "
                + "Heartbeat, then PreRender (+ legacy RenderStepped).");
        }

        [Test]
        public void Lua_SteppedKeepsItsLegacySignature_WhilePreSimulationTakesTheDeltaAlone()
        {
            LuaCsRbxApiBindings roblox = new();
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(roblox, store);

            // Roblox never re-signatured Stepped: it is still (runTime, step) while its replacement
            // PreSimulation takes (deltaTimeSim). A script migrating between the two reads different
            // argument positions, so both shapes have to be pinned.
            stack.Runtime.LoadMod("m", @"
                local rs = game:GetService('RunService')
                rs.Stepped:Connect(function(runTime, step)
                    store_set('stepped_run', tostring(runTime))
                    store_set('stepped_step', tostring(step))
                end)
                rs.PreSimulation:Connect(function(delta, extra)
                    store_set('pre_delta', tostring(delta))
                    store_set('pre_extra', tostring(extra))
                end)");

            roblox.PumpFrame(0.5f);
            roblox.Scheduler.Advance(0d);
            roblox.PumpFrame(0.25f);
            roblox.Scheduler.Advance(0d);

            Assert.AreEqual("0.75", store.Get("m", "stepped_run"),
                "Stepped's first argument is the accumulated run time.");
            Assert.AreEqual("0.25", store.Get("m", "stepped_step"),
                "Stepped's second argument is the frame delta.");
            Assert.AreEqual("0.25", store.Get("m", "pre_delta"),
                "PreSimulation's only argument is the frame delta.");
            Assert.AreEqual("nil", store.Get("m", "pre_extra"),
                "PreSimulation must not carry Stepped's legacy run-time argument.");
        }

        [Test]
        public void Negative_ADedicatedServer_NeverRunsTheRenderPhase()
        {
            LuaCsRbxApiBindings roblox = new();
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(roblox, store);
            roblox.RunService.Topology = new FixedTopology(rendersFrames: false);

            stack.Runtime.LoadMod("m", @"
                local rs = game:GetService('RunService')
                local sim, render = 0, 0
                rs.PostSimulation:Connect(function() sim = sim + 1; store_set('sim', tostring(sim)) end)
                rs.Heartbeat:Connect(function() sim = sim + 1; store_set('sim', tostring(sim)) end)
                rs.PreRender:Connect(function() render = render + 1; store_set('render', tostring(render)) end)
                rs.RenderStepped:Connect(function() render = render + 1; store_set('render', tostring(render)) end)");

            roblox.PumpFrame(0.016f);
            roblox.Scheduler.Advance(0d);
            roblox.PumpFrame(0.016f);
            roblox.Scheduler.Advance(0d);

            Assert.AreEqual("4", store.Get("m", "sim"),
                "A dedicated server still simulates: PostSimulation and Heartbeat must keep firing.");
            Assert.AreEqual("", store.Get("m", "render"),
                "A dedicated server draws nothing, so PreRender and RenderStepped must never fire.");
        }

        [Test]
        public void SoloStillRendersEvenThoughItIsNotAClient()
        {
            // Regression guard for the gate's shape: PreRender is client-side in the mirror, but
            // CoreAI's solo process both renders and is the server, and IsClient stays false there
            // on purpose. Gating the render phase on IsClient would silently kill every solo game's
            // per-frame render handler — this test fails the moment somebody "simplifies" it that way.
            Assert.IsFalse(RbxSoloRuntimeTopology.Shared.IsClient);
            Assert.IsTrue(RbxSoloRuntimeTopology.Shared.RendersFrames);

            LuaCsRbxApiBindings roblox = new();
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(roblox, store);

            stack.Runtime.LoadMod("m", @"
                local rs = game:GetService('RunService')
                rs.PreRender:Connect(function() store_set('rendered', 'yes') end)");

            roblox.PumpFrame(0.016f);
            roblox.Scheduler.Advance(0d);

            Assert.AreEqual("yes", store.Get("m", "rendered"));
        }

        [Test]
        public void CSharp_BridgeTopology_AnswersRendersFramesFromTheTransport()
        {
            Assert.IsTrue(TopologyFor(Mods.Rbx.Instances.Networking.RbxNetworkTopology.Solo)
                .RendersFrames);
            Assert.IsTrue(TopologyFor(Mods.Rbx.Instances.Networking.RbxNetworkTopology.Host)
                .RendersFrames);
            Assert.IsTrue(TopologyFor(Mods.Rbx.Instances.Networking.RbxNetworkTopology.Client)
                .RendersFrames);
            Assert.IsFalse(
                TopologyFor(Mods.Rbx.Instances.Networking.RbxNetworkTopology.DedicatedServer)
                    .RendersFrames,
                "Only a dedicated server draws nothing.");
        }

        [Test]
        public void Step_AndPumpFrame_FireTheSameSignalsInTheSameOrder()
        {
            // WHY this test exists: RbxRunService.Step is a second frame pump with no production
            // caller — production drives the split PumpPreAnimation/PumpPreSimulation/... phases.
            // Two pumps that can disagree eventually do, and the one nobody runs is the one that
            // rots. Pinning them to the same observable order means the unused one cannot drift
            // into a different contract while looking like the same thing.
            Assert.AreEqual(ObserveOrder(pumpFrame: true), ObserveOrder(pumpFrame: false));
        }

        private static string ObserveOrder(bool pumpFrame)
        {
            LuaCsRbxApiBindings roblox = new();
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(roblox, store);
            stack.Runtime.LoadMod("m", @"
                local rs = game:GetService('RunService')
                local order = ''
                local function note(tag)
                    return function()
                        order = order .. tag
                        store_set('order', order)
                    end
                end
                rs.PreAnimation:Connect(note('A'))
                rs.PreSimulation:Connect(note('P'))
                rs.Stepped:Connect(note('s'))
                rs.PostSimulation:Connect(note('O'))
                rs.Heartbeat:Connect(note('H'))
                rs.PreRender:Connect(note('R'))
                rs.RenderStepped:Connect(note('r'))");

            if (pumpFrame)
            {
                roblox.PumpFrame(0.016f);
            }
            else
            {
                roblox.RunService.Step(0.016f);
            }

            roblox.Scheduler.Advance(0d);
            return store.Get("m", "order");
        }

        private static IRbxRuntimeTopology TopologyFor(
            Mods.Rbx.Instances.Networking.RbxNetworkTopology topology)
        {
            return new RbxBridgeRuntimeTopology(new HeadlessBridge(topology));
        }

        private static LuaCsModStack BuildStack(LuaCsRbxApiBindings roblox, MemoryStore store)
        {
            return LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
            {
                Logger = new FakeGameLogger(),
                ModStore = store,
                Capabilities = LuaCapabilities.All,
                OneOffCapabilities = LuaCapabilities.All,
                RbxApi = roblox
            });
        }

        private sealed class FixedTopology : IRbxRuntimeTopology
        {
            public FixedTopology(bool rendersFrames)
            {
                RendersFrames = rendersFrames;
            }

            public bool IsServer => true;

            public bool IsClient => false;

            public bool IsStudio => false;

            public bool IsRunning => true;

            public bool RendersFrames { get; }
        }

        private sealed class HeadlessBridge : Mods.Rbx.Instances.Networking.INetworkBridge
        {
            public HeadlessBridge(Mods.Rbx.Instances.Networking.RbxNetworkTopology topology)
            {
                Topology = topology;
            }

            public Mods.Rbx.Instances.Networking.RbxNetworkTopology Topology { get; }

            public IReadOnlyList<string> ActorIds => System.Array.Empty<string>();

            public int MaxPayloadBytes => 65536;

            public double ServerClockOffsetSeconds => 0d;

            public event System.Action<Mods.Rbx.Instances.Networking.RbxNetworkEventMessage>
                EventReceived
            {
                add { }
                remove { }
            }

            public event System.Action<Mods.Rbx.Instances.Networking.RbxNetworkRequestMessage,
                Mods.Rbx.Instances.Networking.RbxNetworkRequestResponder> RequestReceived
            {
                add { }
                remove { }
            }

            public event System.Action<Mods.Rbx.Instances.Networking.RbxNetworkPeerDisconnected>
                PeerDisconnected
            {
                add { }
                remove { }
            }

            public void RegisterActor(string actorId)
            {
            }

            public void UnregisterActor(string actorId)
            {
            }

            public void SendEvent(Mods.Rbx.Instances.Networking.RbxNetworkEventMessage message)
            {
            }

            public void SendRequest(Mods.Rbx.Instances.Networking.RbxNetworkRequestMessage message,
                System.Action<Mods.Rbx.Instances.Networking.RbxNetworkResponse> response)
            {
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
