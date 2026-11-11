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
    /// <c>Model:ScaleTo</c> and <c>Model:GetScale</c> through the real mod runtime: a player's character scaled
    /// to block-game size grows its root and keeps it on its pivot, the rig poses its head at the scaled height,
    /// the first-person eye follows the head, ScaleTo(1) gives the body back, a plain model scales around its
    /// pivot, and a factor that is not above 0 is refused (twin).
    /// </summary>
    [TestFixture]
    public sealed class ModelScaleToLuaEditModeTests
    {
        private const double FrameSeconds = 1d / 60d;
        private const string ActorId = "scale-player";

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

        private PartProperties Part(RbxInstance model, string name)
        {
            return _roblox.PartSink.GetPartPropertiesOrDefault(model.FindFirstChild(name).Id);
        }

        [Test]
        public void ACharacterScaledTo1_44_IsBlockGameSized_AndScaleOneGivesItBack()
        {
            _roblox.Players.EnsureActor(_roblox.Registry, ActorId);
            Frames(4);
            Assert.IsTrue(_roblox.Players.TryGetByActorId(ActorId, out RbxPlayer player));
            RbxInstance character = player.Character;
            RbxVector3 rootBefore = Part(character, "HumanoidRootPart").Position;

            LoadAsPlayer("grow", @"
local c = game:GetService('Players').LocalPlayer.Character
store_set('before', tostring(c:GetScale()))
c:ScaleTo(1.44)
store_set('after', tostring(c:GetScale()))");
            Frames(3);

            Assert.AreEqual("1", _store.Get("grow", "before"), "a new model's scale is 1");
            Assert.AreEqual("1.44", _store.Get("grow", "after"));
            PartProperties root = Part(character, "HumanoidRootPart");
            Assert.AreEqual(2.88f, root.Size.Y, 1e-3f, "the root grows: the motor's body capsule follows its size");
            Assert.AreEqual(1.44f, root.Size.Z, 1e-3f);
            Assert.AreEqual(rootBefore.X, root.Position.X, 1e-3f, "the root is the pivot and stays");
            PartProperties head = Part(character, "Head");
            Assert.AreEqual(RbxR6Rig.HeadOffsetFromRoot.Y * 1.44f, head.Position.Y - root.Position.Y, 0.15f,
                "the rig poses the head at the scaled height");
            Assert.AreEqual(1.44f, head.Size.Y, 1e-3f);
            RbxHumanoid humanoid = (RbxHumanoid)character.FindFirstChildOfClass("Humanoid");
            Assert.AreEqual(RbxR6Rig.HeadOffsetFromRoot.Y * 1.44f,
                LuaCsRbxApiBindings.CameraSubjectOffsetFor(humanoid).Y, 1e-3f, "the first-person eye follows the head");

            LoadAsPlayer("back", @"game:GetService('Players').LocalPlayer.Character:ScaleTo(1)");
            Frames(3);

            root = Part(character, "HumanoidRootPart");
            Assert.AreEqual(2f, root.Size.Y, 1e-4f, "ScaleTo(1) gives the body back");
            Assert.AreEqual(RbxR6Rig.HeadOffsetFromRoot.Y,
                LuaCsRbxApiBindings.CameraSubjectOffsetFor(humanoid).Y, 1e-4f);
        }

        [Test]
        public void APlainModel_ScalesAroundItsPivot()
        {
            LoadAsPlayer("plain", @"
local model = Instance.new('Model')
local a = Instance.new('Part')
a.Size = Vector3.new(2, 2, 2)
a.Position = Vector3.new(10, 5, 0)
a.Anchored = true
a.Parent = model
local b = Instance.new('Part')
b.Size = Vector3.new(4, 1, 2)
b.Position = Vector3.new(14, 5, 0)
b.Anchored = true
b.Parent = model
model.PrimaryPart = a
model.Parent = workspace
model:ScaleTo(2)
store_set('b', string.format('%.2f %.2f %.2f|%.2f', b.Position.X, b.Position.Y, b.Size.X, a.Position.X))
model:ScaleTo(1)
store_set('back', string.format('%.2f %.2f', b.Position.X, b.Size.X))");
            Frames(2);

            Assert.AreEqual("18.00 5.00 8.00|10.00", _store.Get("plain", "b"),
                "b's offset from the pivot (a) doubles, its size doubles, the pivot stays");
            Assert.AreEqual("14.00 4.00", _store.Get("plain", "back"));
        }

        [Test]
        public void ScaleTo_RefusesZero_AndLeavesTheModelAlone_NegativeTwin()
        {
            LoadAsPlayer("zero", @"
local model = Instance.new('Model')
local a = Instance.new('Part')
a.Size = Vector3.new(2, 2, 2)
a.Anchored = true
a.Parent = model
model.Parent = workspace
local ok, problem = pcall(function() model:ScaleTo(0) end)
store_set('result', tostring(ok) .. '|' .. tostring(model:GetScale()) .. '|' .. a.Size.X)
store_set('problem', tostring(problem))");
            Frames(2);

            Assert.AreEqual("false|1|2", _store.Get("zero", "result"), _store.Get("zero", "problem"));
            StringAssert.Contains("above 0", _store.Get("zero", "problem"));
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
