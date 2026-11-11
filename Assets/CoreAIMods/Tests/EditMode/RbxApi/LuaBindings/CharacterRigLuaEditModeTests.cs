using System.Collections.Generic;
using System.Threading;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Authority;
using CoreAI.Infrastructure.Logging;
using CoreAI.Mods.Rbx.Binding;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.CharacterRig;
using CoreAI.Mods.Rbx.Instances.Networking;
using CoreAI.Tests.EditMode.Performance;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.CharacterRig
{
    /// <summary>
    /// The character rig through the real mod runtime with headless seams: a player's character is the R6 body
    /// a script finds by Roblox's names and recolours like any Part; <c>Humanoid.StateChanged</c> reaches Lua
    /// with the old and new <c>Enum.HumanoidStateType</c> once per transition, in Roblox's order, and not at
    /// all when the state holds; and posing over the real part sink allocates nothing per frame.
    /// </summary>
    [TestFixture]
    public sealed class CharacterRigLuaEditModeTests
    {
        private const double FrameSeconds = 1d / 60d;
        private const string ActorId = "rig-player";

        private SynchronizationContext _savedContext;
        private LuaCsRbxApiBindings _roblox;
        private LuaCsModStack _stack;
        private MemoryStore _store;

        [SetUp]
        public void SetUp()
        {
            _savedContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
            _store = new MemoryStore();
            _roblox = new LuaCsRbxApiBindings(inputSource: new InMemoryInputSource());
            _stack = LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
            {
                Logger = new SilentLogger(),
                ModStore = _store,
                Capabilities = LuaCapabilities.All,
                OneOffCapabilities = LuaCapabilities.All,
                RbxApi = _roblox
            });
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

        private void LoadAsPlayer(string id, string code)
        {
            ActorContext loader = new LocalActorIdentityProvider(ActorId).GetActorContext(null);
            _stack.Runtime.LoadMod(loader, id, code, LuaCapabilities.All, false);
        }

        private RbxInstance Character()
        {
            Assert.IsTrue(_roblox.Players.TryGetByActorId(ActorId, out RbxPlayer player));
            Assert.IsNotNull(player.Character, "the player has a character");
            return player.Character;
        }

        [Test]
        public void TheDefaultCharacter_IsTheR6Body_FoundByRobloxsNamesFromLua()
        {
            LoadAsPlayer("probe", @"
local Players = game:GetService('Players')
local player = Players.LocalPlayer
local c = player.Character or player.CharacterAdded:Wait()
local torso = c.Torso.Color
store_set('names', c.Head.Name .. '|' .. c:FindFirstChild('Left Arm').Name .. '|' .. c['Right Leg'].ClassName
    .. '|' .. tostring(c.PrimaryPart == c.HumanoidRootPart) .. '|' .. #c:GetChildren()
    .. '|' .. tostring(c.HumanoidRootPart.Transparency) .. '|' .. tostring(c.Torso.CanCollide)
    .. '|' .. tostring(c['Left Arm'].CanCollide) .. '|' .. tostring(c.Head.Anchored))
store_set('torso', math.floor(torso.R * 255 + 0.5) .. ',' .. math.floor(torso.G * 255 + 0.5) .. ','
    .. math.floor(torso.B * 255 + 0.5))
c.Head.Color = Color3.new(1, 0, 0)
store_set('done', 'yes')");
            Frames(6);

            Assert.AreEqual("yes", _store.Get("probe", "done"), "the probe ran: " + _store.Get("probe", "names"));
            Assert.AreEqual("Head|Left Arm|Part|true|8|1|true|false|false", _store.Get("probe", "names"),
                "seven parts and a Humanoid; the root is the PrimaryPart and invisible; R6's collision flags");
            Assert.AreEqual("40,127,71", _store.Get("probe", "torso"), "the mirror's BodyColors.TorsoColor3 default");

            RbxInstance character = Character();
            RbxInstance head = character.FindFirstChild("Head");
            PartProperties headState = _roblox.PartSink.GetPartPropertiesOrDefault(head.Id);
            Assert.AreEqual(1f, headState.Color.R, 1e-4f, "a script's colour stays: the rig poses, it never repaints");
            Assert.AreEqual(0f, headState.Color.G, 1e-4f);
        }

        [Test]
        public void TheDefaultCharacter_IsPosedAroundItsRoot_EveryFrame()
        {
            _roblox.Players.EnsureActor(_roblox.Registry, ActorId);
            Frames(4);
            RbxInstance character = Character();
            Assert.IsNotNull(_roblox.CharacterRigs, "the bindings wire the rig");
            Assert.GreaterOrEqual(_roblox.CharacterRigs.PosedCount, 1, "the character is posed");

            RbxCFrame root = _roblox.PartSink.GetPartPropertiesOrDefault(
                character.FindFirstChild("HumanoidRootPart").Id).CFrame;
            RbxCFrame torso = _roblox.PartSink.GetPartPropertiesOrDefault(character.FindFirstChild("Torso").Id).CFrame;
            RbxCFrame leftLeg = _roblox.PartSink.GetPartPropertiesOrDefault(
                character.FindFirstChild("Left Leg").Id).CFrame;
            Assert.AreEqual(RbxR6Rig.TorsoLiftStuds, (torso.Position - root.Position).Y, 0.1f,
                "the Torso rides one leg length above the root, breathing");
            Assert.AreEqual(-0.5f, root.PointToObjectSpace(leftLeg.Position).X, 0.05f, "the left leg hangs at -0.5");

            LoadAsPlayer("mover", @"
local root = workspace:FindFirstChild('" + character.Name + @"').HumanoidRootPart
root.CFrame = CFrame.new(30, 1, -12) * CFrame.Angles(0, math.pi / 2, 0)");
            Frames(2);
            RbxCFrame movedTorso = _roblox.PartSink.GetPartPropertiesOrDefault(
                character.FindFirstChild("Torso").Id).CFrame;
            Assert.AreEqual(30f, movedTorso.X, 0.1f, "the body follows a moved root");
            Assert.AreEqual(-12f, movedTorso.Z, 0.1f);
            Assert.AreEqual(3f, movedTorso.Y, 0.1f);
        }

        [Test]
        public void StateChanged_ReachesLua_WithTheOldAndNewStateAsEnumItems_OncePerTransition()
        {
            FakeRigMotor motor = new();
            RbxInstance npc = CharacterRigTestWorld.BuildModel(_roblox.Registry, "Npc", "HumanoidRootPart");
            RbxHumanoid humanoid = CharacterRigTestWorld.Humanoid(npc);
            _roblox.AttachCharacterMotorFactory(candidate => ReferenceEquals(candidate, humanoid) ? motor : null);
            _stack.Runtime.LoadMod("states", @"
local humanoid = workspace.Npc.Humanoid
local log, kinds = {}, {}
humanoid.StateChanged:Connect(function(old, new)
    local same = new == Enum.HumanoidStateType[new.Name] and old == Enum.HumanoidStateType[old.Name]
    table.insert(log, old.Name .. '>' .. new.Name .. (same and '' or '!'))
    kinds[typeof(old) .. '/' .. typeof(new)] = true
    store_set('log', table.concat(log, ','))
    local list = {}
    for kind in pairs(kinds) do table.insert(list, kind) end
    store_set('kinds', table.concat(list, ','))
end)
store_set('ready', 'yes')");
            Frames(3);
            Assert.AreEqual("yes", _store.Get("states", "ready"));
            Assert.AreEqual("", _store.Get("states", "log"), "twin: a state that holds fires nothing");

            motor.Grounded = false;
            Frames(2);
            motor.Grounded = true;
            Frames(3);
            humanoid.RequestJump();
            motor.Grounded = false;
            Frames(2);
            motor.Grounded = true;
            Frames(3);
            Assert.IsTrue(humanoid.SetFlying(true));
            motor.Grounded = false;
            Frames(3);
            Assert.IsTrue(humanoid.SetFlying(false));
            motor.Grounded = true;
            Frames(3);
            string afterTransitions = _store.Get("states", "log");
            Frames(10);

            Assert.AreEqual(
                "Running>Freefall,Freefall>Landed,Landed>Running,"
                + "Running>Jumping,Jumping>Freefall,Freefall>Landed,Landed>Running,"
                + "Running>Flying,Flying>Freefall,Freefall>Landed,Landed>Running",
                afterTransitions, "every transition once, old then new, each the world's own Enum item");
            Assert.AreEqual("EnumItem/EnumItem", _store.Get("states", "kinds"), "never nil: both arguments are EnumItems");
            Assert.AreEqual(afterTransitions, _store.Get("states", "log"), "twin: ten quiet frames fire nothing more");
        }

        [Test]
        public void AutoRotate_IsScriptable_AndReachesTheMotor_AndTrueTurnsAgain()
        {
            FakeRigMotor motor = new();
            RbxInstance npc = CharacterRigTestWorld.BuildModel(_roblox.Registry, "Npc", "HumanoidRootPart");
            RbxHumanoid humanoid = CharacterRigTestWorld.Humanoid(npc);
            _roblox.AttachCharacterMotorFactory(candidate => ReferenceEquals(candidate, humanoid) ? motor : null);
            _stack.Runtime.LoadMod("turns", @"
local humanoid = workspace.Npc.Humanoid
local changed = 0
humanoid:GetPropertyChangedSignal('AutoRotate'):Connect(function() changed = changed + 1 end)
local before = humanoid.AutoRotate
humanoid.AutoRotate = false
humanoid.AutoRotate = false
task.wait()
store_set('off', tostring(before) .. '|' .. tostring(humanoid.AutoRotate) .. '|' .. changed)
local ok = pcall(function() humanoid.AutoRotate = 'yes' end)
store_set('typed', tostring(ok))");
            Frames(3);

            Assert.AreEqual("true|false|1", _store.Get("turns", "off"),
                "default on, a script turns it off, one Changed for two equal writes");
            Assert.AreEqual("false", _store.Get("turns", "typed"), "a non-boolean is refused");
            Assert.IsFalse(humanoid.AutoRotate);
            Assert.IsFalse(motor.LastAutoRotate, "the motor stops turning: the heading is the script's");

            _stack.Runtime.LoadMod("turns-on", "workspace.Npc.Humanoid.AutoRotate = true");
            Frames(1);
            Assert.IsTrue(motor.LastAutoRotate, "twin: true hands the heading back to the motor");
        }

        [Test]
        public void PosingOverTheRealPartSink_AllocatesNothingPerFrame_IdleOrWalking()
        {
            FakeRigMotor motor = new();
            _roblox.AttachCharacterMotorFactory(_ => motor);
            _roblox.Players.EnsureActor(_roblox.Registry, ActorId);
            Frames(4);
            Character();
            RbxCharacterRigs rigs = _roblox.CharacterRigs;
            Assert.AreEqual(1, rigs.TrackedCount);

            AllocationMeter.AssertWithinBudget("idle R6 character, one rig frame over the in-memory sink",
                count =>
                {
                    for (int index = 0; index < count; index++)
                    {
                        rigs.Step(FrameSeconds);
                    }
                }, 2000, 0);

            motor.Speed = 16d;
            AllocationMeter.AssertWithinBudget("walking R6 character, one rig frame over the in-memory sink",
                count =>
                {
                    for (int index = 0; index < count; index++)
                    {
                        rigs.Step(FrameSeconds);
                    }
                }, 2000, 0);
            Assert.IsTrue(rigs.TryGetPoser(CharacterRigTestWorld.Humanoid(Character()), out RbxRigPoser poser));
            Assert.AreEqual(RbxRigPoseKind.Walk, poser.Kind, "the measured frames were walking ones");
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
