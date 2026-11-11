using System;
using System.Collections.Generic;
using System.Threading;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Infrastructure.Logging;
using CoreAI.Mods.Rbx.Binding;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Networking;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.LuaBindings
{
    /// <summary>
    /// <c>BasePart.LocalTransparencyModifier</c> and the default camera's TransparencyController through the real
    /// mod runtime with headless seams: in first person the local character is not drawn on this machine, zooming
    /// out draws it again, another player's character is never touched, the local character never pulls the
    /// camera in, and the property is local (no revision) and bound for Lua. Every rule has its negative twin.
    /// </summary>
    [TestFixture]
    public sealed class P1LocalTransparencyLuaEditModeTests
    {
        private const double FrameSeconds = 1d / 60d;

        private SynchronizationContext _savedContext;
        private InMemoryCameraRig _rig;
        private LuaCsRbxApiBindings _roblox;
        private LuaCsModStack _stack;
        private MemoryStore _store;

        [SetUp]
        public void SetUp()
        {
            _savedContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
            _store = new MemoryStore();
            _rig = new InMemoryCameraRig();
            _roblox = new LuaCsRbxApiBindings(cameraRig: _rig, inputSource: new InMemoryInputSource());
            _stack = LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
            {
                Logger = new SilentLogger(),
                ModStore = _store,
                Capabilities = LuaCapabilities.All,
                OneOffCapabilities = LuaCapabilities.All,
                RbxApi = _roblox
            });
            _roblox.Players.EnsureActor(_roblox.Registry, "local-player");
            _roblox.Players.EnsureActor(_roblox.Registry, "other-player");
            _roblox.LocalPlayerActorId = "local-player";
            Frames(3);
        }

        [TearDown]
        public void TearDown()
        {
            _roblox?.Dispose();
            SynchronizationContext.SetSynchronizationContext(_savedContext);
        }

        private void Frames(int count)
        {
            for (int index = 0; index < count; index++)
            {
                _roblox.Scheduler.Advance(FrameSeconds);
            }
        }

        private InMemoryPartPropertySink Sink => (InMemoryPartPropertySink)_roblox.PartSink;

        private RbxPlayer Player(string actorId)
        {
            Assert.IsTrue(_roblox.Players.TryGetByActorId(actorId, out RbxPlayer player));
            Assert.IsNotNull(player.Character, actorId + " has a character");
            return player;
        }

        private List<RbxBasePart> PartsOf(string actorId)
        {
            List<RbxBasePart> parts = new();
            foreach (RbxInstance node in Player(actorId).Character.GetDescendants())
            {
                if (node is RbxBasePart part)
                {
                    parts.Add(part);
                }
            }

            Assert.IsNotEmpty(parts);
            return parts;
        }

        [Test]
        public void P1_FirstPerson_HidesTheLocalCharacter_AndZoomingOutDrawsItAgain()
        {
            RbxPlayer local = Player("local-player");
            local.CameraMode = RbxCameraMode.LockFirstPerson;
            Frames(2);

            foreach (RbxBasePart part in PartsOf("local-player"))
            {
                Assert.AreEqual(1f, part.LocalTransparencyModifier, part.Name + " in first person");
                Assert.AreEqual(1f, Sink.GetDrawnTransparency(part.Id), part.Name + " is not drawn here");
            }

            // WHY a minimum distance too: LockFirstPerson left the chosen zoom at first person, and Classic alone
            // keeps it there until the player scrolls out, as in Roblox.
            local.CameraMode = RbxCameraMode.Classic;
            local.CameraMinZoomDistance = 10d;
            Frames(2);

            foreach (RbxBasePart part in PartsOf("local-player"))
            {
                Assert.AreEqual(0f, part.LocalTransparencyModifier, part.Name + " zoomed out");
                Assert.AreEqual(Sink.GetPartPropertiesOrDefault(part.Id).Transparency,
                    Sink.GetDrawnTransparency(part.Id), part.Name + " is drawn as its Transparency says");
            }
        }

        [Test]
        public void P1_Negative_AnotherPlayersCharacter_StaysDrawnInFirstPerson()
        {
            Player("local-player").CameraMode = RbxCameraMode.LockFirstPerson;
            Frames(2);

            foreach (RbxBasePart part in PartsOf("other-player"))
            {
                Assert.AreEqual(0f, part.LocalTransparencyModifier, part.Name + " belongs to another player");
                Assert.AreEqual(0f, Sink.GetLocalTransparencyModifier(part.Id));
            }
        }

        [Test]
        public void P1_TheLocalCharacter_NeverPullsTheCameraIn_ButAnotherBodyDoes()
        {
            RbxPlayer local = Player("local-player");
            local.CameraMinZoomDistance = 1.2d;
            local.CameraMaxZoomDistance = 1.2d;
            HashSet<InstanceId> ownBody = new();
            foreach (RbxBasePart part in PartsOf("local-player"))
            {
                ownBody.Add(part.Id);
            }

            HashSet<InstanceId> otherBody = new();
            foreach (RbxBasePart part in PartsOf("other-player"))
            {
                otherBody.Add(part.Id);
            }

            BlockingPort port = new(ownBody);
            _roblox.WorldPhysics.AttachPort(port);
            Frames(2);
            RbxVector3 focus = _roblox.PartSink.GetLivePositionStuds(
                                   Player("local-player").Character.FindFirstChild(RbxCharacterFactory.RootPartName).Id)
                               + LuaCsRbxApiBindings.RigCameraSubjectOffset;
            Assert.AreEqual(1.2f, (focus - _rig.GetCFrame().Position).Magnitude, 1e-3f,
                "the local character's parts are not view-blocking geometry");
            Assert.Greater(PartsOf("local-player")[0].LocalTransparencyModifier, 0f, "and it fades this close");

            port.Blockers = otherBody;
            Frames(2);
            Assert.Less((focus - _rig.GetCFrame().Position).Magnitude, 1.2f - 1e-3f,
                "twin: another body between the focus and the camera pulls it in");
        }

        [Test]
        public void P1_LocalTransparencyModifier_IsLocal_AndBoundForLua()
        {
            RbxBasePart torso = PartsOf("other-player")[0];
            Assert.IsTrue(_roblox.Registry.TryGetRecord(torso.Id, out InstanceRecord record));
            long revision = record.Revision;

            _stack.Runtime.LoadMod("probe", @"
local part = Instance.new('Part')
part.Parent = workspace
local fired = 0
part:GetPropertyChangedSignal('LocalTransparencyModifier'):Connect(function() fired = fired + 1 end)
part.LocalTransparencyModifier = 0.5
part.Transparency = 0.5
task.wait()
store_set('read', tostring(part.LocalTransparencyModifier) .. '|' .. tostring(part.Transparency) .. '|' .. fired)
local ok, err = pcall(function() part.LocalTransparencyModifier = 0 / 0 end)
store_set('nan', tostring(ok) .. '|' .. tostring(err))");
            Frames(2);
            torso.SetLocalTransparencyModifier(0.25f);

            Assert.AreEqual("0.5|0.5|1", _store.Get("probe", "read"), "Transparency is unchanged by the modifier");
            StringAssert.StartsWith("false|", _store.Get("probe", "nan"));
            Assert.AreEqual(revision, record.Revision, "a local-only property advances no revision");
            Assert.AreEqual(0.75f, RbxBasePart.EffectiveTransparency(0.5f, 0.5f), 1e-6f,
                "drawn as 1 - (1 - Transparency) * (1 - modifier)");
        }

        [Test]
        public void P1_TheTransparencyControllerRule_FadesNearFirstPerson()
        {
            Assert.AreEqual(1d, RbxCameraTransparency.Target(0.5d), 1e-9d, "first person");
            Assert.AreEqual(0.6667d, RbxCameraTransparency.Target(1d), 1e-3d, "close third person fades");
            Assert.AreEqual(0d, RbxCameraTransparency.Target(1.3d), 1e-9d, "below one half it is not controlled");
            Assert.AreEqual(0d, RbxCameraTransparency.Target(12.5d), 1e-9d, "the default zoom draws the body");
            Assert.AreEqual(1d, RbxCameraTransparency.Step(0d, true, 1d, FrameSeconds), 1e-9d,
                "entering first person is at once");
            Assert.AreEqual(0d, RbxCameraTransparency.Step(1d, true, 0d, FrameSeconds), 1e-9d,
                "leaving it too");
            Assert.AreEqual(0.05d, RbxCameraTransparency.Step(0d, true, 0.8d, FrameSeconds), 1e-9d,
                "twin: a fade moves at most 2.8 per second");
        }

        /// <summary>A physics port whose rays stop on the listed parts, if the caller's filter accepts them.</summary>
        private sealed class BlockingPort : IRbxPhysicsPort
        {
            public BlockingPort(HashSet<InstanceId> blockers)
            {
                Blockers = blockers;
            }

            public HashSet<InstanceId> Blockers { get; set; }

            public event Action<InstanceId, InstanceId> ContactBegan
            {
                add { }
                remove { }
            }

            public event Action<InstanceId, InstanceId> ContactEnded
            {
                add { }
                remove { }
            }

            public bool TryRaycast(RbxVector3 originStuds, RbxVector3 directionStuds, bool respectCanCollide,
                Func<InstanceId, bool> isEligible, out RbxPhysicsRaycastHit hit)
            {
                foreach (InstanceId id in Blockers)
                {
                    if (isEligible == null || isEligible(id))
                    {
                        RbxVector3 unit = directionStuds.Unit;
                        hit = new RbxPhysicsRaycastHit(id, originStuds + unit * 0.5f, -unit, default, 0.5d);
                        return true;
                    }
                }

                hit = default;
                return false;
            }

            public void SetGravity(double studsPerSecondSquared)
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
            }
        }

        private sealed class SilentLogger : IGameLogger
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
