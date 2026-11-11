using System.Threading;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Ai.Npc;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.BlockWorld
{
    /// <summary>
    /// P6, the Block World's villagers (<c>blockworld_villagers</c>) on the real P12 AI surface: with the game's
    /// framework setting off the bodies stand but every agent call is refused and the villagers say, naming the
    /// setting, why they are silent; with it on the mod makes three agents with a name, a persona and a memory of
    /// their own, and the one under the crosshair answers the player by name through a scripted model; with the
    /// setting on and no model the answer is the villager's line saying a model is needed.
    /// </summary>
    [TestFixture]
    public sealed class BlockWorldVillagersEditModeTests
    {
        private const string Mod = "blockworld_villagers";

        private SynchronizationContext _savedContext;
        private BlockWorldTestWorld _world;

        [SetUp]
        public void SetUp()
        {
            _savedContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
        }

        [TearDown]
        public void TearDown()
        {
            _world?.Dispose();
            _world = null;
            SynchronizationContext.SetSynchronizationContext(_savedContext);
        }

        private void Start(bool allowAgents, IAiOrchestrationService model = null)
        {
            _world = new BlockWorldTestWorld(new BlockWorldTestOptions
            {
                Ai = new CoreAiLuaAiOptions { AllowAgentsFromLua = allowAgents },
                Npc = model
            });
            _world.SetAttribute("BlockWorldSpawn", new RbxVector3(2, 10, 2));
            _world.SetAttribute("BlockWorldReady", true);
            _world.Load(Mod);
            _world.Frame(150);
        }

        private const string Brain = "idle, wander, greet, face_player, follow_player, go_home";

        private void TalkTo(string name)
        {
            _world.SetAttribute("CrosshairPartName", "Head");
            _world.SetAttribute("CrosshairModelName", name);
            _world.Tap(BlockWorldTestWorld.KeyE);
        }

        private RbxInstance Body(string name)
        {
            RbxInstance body = _world.Workspace.FindFirstChild(name);
            Assert.IsNotNull(body, name + " stands directly under the Workspace, where the brain host looks");
            return body;
        }

        [Test]
        public void P6_Villagers_AreCharacters_ByTheSpawn_WhateverTheAiSettings()
        {
            Start(allowAgents: false);

            foreach (string name in new[] { "Mira", "Tobin", "Elda" })
            {
                RbxInstance body = Body(name);
                Assert.AreEqual("Model", body.ClassName);
                Assert.IsNotNull(body.FindFirstChild("HumanoidRootPart"), name);
                Assert.IsNotNull(body.FindFirstChildOfClass("Humanoid"), name + " can be walked by a brain");
                Assert.AreEqual(9, body.GetChildren().Count, "root, Humanoid, the six R6 parts and hair");
                foreach (string part in new[] { "Head", "Torso", "Left Arm", "Right Arm", "Left Leg", "Right Leg" })
                {
                    Assert.IsNotNull(body.FindFirstChild(part), name + " has the R6 part " + part);
                }
            }

            _world.AssertHealthy(Mod);
        }

        [Test]
        public void P6_Villagers_LeaveTheSixR6PartsToTheRig_AndTheRootIsTheOnlyCollider()
        {
            Start(allowAgents: false);

            _world.LoadSource("test_body", @"
local body = workspace.Mira
local loose = 0
for _, name in ipairs({ 'Torso', 'Head', 'Left Arm', 'Right Arm', 'Left Leg', 'Right Leg' }) do
    local part = body[name]
    if not part.Anchored and not part.CanCollide then
        loose = loose + 1
    end
end
local root = body.HumanoidRootPart
workspace:SetAttribute('TestLooseParts', loose)
workspace:SetAttribute('TestRootSize', root.Size)
workspace:SetAttribute('TestRootHidden', root.Transparency == 1)
workspace:SetAttribute('TestHairHeld', body.Hair.Anchored and not body.Hair.CanCollide)");

            Assert.AreEqual(6d, _world.Number("TestLooseParts"),
                "the rig poses the six R6 parts: unanchored, and none of them collides");
            Assert.AreEqual(new RbxVector3(2, 2, 1), _world.Vector("TestRootSize"), "the root is the standard 2x2x1 box");
            Assert.AreEqual(true, _world.Attribute("TestRootHidden"), "and invisible");
            Assert.AreEqual(true, _world.Attribute("TestHairHeld"), "the hair is the mod's own, held and never colliding");
            _world.AssertHealthy(Mod);
        }

        [Test]
        public void P6_Villagers_TheHairFollowsTheHead_WhereverTheHeadIsPosed()
        {
            Start(allowAgents: false);

            _world.LoadSource("test_move_head", "workspace.Mira.Head.CFrame = CFrame.new(50, 20, 50)");
            double moved = HairGap("test_gap_moved");
            _world.Frame(2);
            double after = HairGap("test_gap_after");

            Assert.Greater(moved, 1d, "a head moved after this frame's render step has left its hair behind");
            Assert.AreEqual(0d, after, 0.01d, "the next render step puts the hair back on the head");
            _world.AssertHealthy(Mod);
        }

        private double HairGap(string probeId)
        {
            _world.LoadSource(probeId, "local body = workspace.Mira "
                                       + "workspace:SetAttribute('TestHairGap', (body.Hair.Position - "
                                       + "(body.Head.CFrame * CFrame.new(0, 0.7, 0)).Position).Magnitude)");
            return _world.Number("TestHairGap");
        }

        [Test]
        public void P6_Villagers_WithTheSettingOff_AreSilent_AndSayWhy_NamingTheSetting()
        {
            Start(allowAgents: false);

            Assert.AreEqual(0, _world.Director.Agents.Count, "no agent is made");
            Assert.IsNull(Body("Mira").GetAttribute("NpcAgent"), "and no brain asked for");
            StringAssert.Contains(CoreAiLuaAiOptions.SettingName, (string)_world.Attribute("BlockWorldNotice"));

            TalkTo("Mira");

            string notice = (string)_world.Attribute("BlockWorldNotice");
            StringAssert.StartsWith("Mira is silent", notice);
            StringAssert.Contains(CoreAiLuaAiOptions.SettingName, notice);
            Assert.IsNull(_world.Attribute("BlockWorldVillagerReply"), "nobody answered");
            _world.AssertHealthy(Mod);
        }

        [Test]
        public void P6_Villagers_WithTheSettingOn_AreAgents_AndTheOneYouLookAt_AnswersYouByName()
        {
            ScriptedVillagerModel model = new()
            {
                Reply = request => request.Hint.StartsWith("Player:") ? "Fresh bread today, Player!" : "Hm?"
            };
            Start(allowAgents: true, model);

            Assert.AreEqual(3, _world.Director.Agents.Count, "Mira, Tobin and Elda");
            Assert.AreEqual(Mod + "/Mira", Body("Mira").GetAttribute("NpcAgent"), "the brain host finds her agent");
            Assert.AreEqual(Brain, Body("Mira").GetAttribute("NpcBrain"));
            StringAssert.Contains("torches are lit", (string)Body("Tobin").GetAttribute("NpcState"),
                "the mod's facts for the villagers' model");

            TalkTo("Mira");
            _world.FrameUntil(() => _world.Attribute("BlockWorldVillagerReply") != null, 120, "Mira's answer");

            Assert.AreEqual("Mira: Fresh bread today, Player!", _world.Attribute("BlockWorldVillagerReply"));
            Assert.AreEqual(1, model.Requests.Count, "only the villager spoken to answered");
            AiTaskRequest request = model.Requests[0];
            StringAssert.Contains("village baker", request.RequestSystemInstructions, "her persona");
            StringAssert.Contains("blockworld/Mira", request.ActorContext.Value.MemoryScope.TopicId,
                "her own memory");
            _world.AssertHealthy(Mod);
        }

        [Test]
        public void P6_Villagers_WithTheSettingOnAndNoModel_AnswerThatAModelIsNeeded()
        {
            Start(allowAgents: true, UnavailableAiOrchestrationService.Instance);

            TalkTo("Tobin");
            _world.FrameUntil(() => _world.Attribute("BlockWorldVillagerReply") != null, 120, "Tobin's line");

            StringAssert.Contains("no AI model answered", (string)_world.Attribute("BlockWorldVillagerReply"));
            StringAssert.StartsWith("Tobin:", (string)_world.Attribute("BlockWorldVillagerReply"));
            _world.AssertHealthy(Mod);
        }

        [Test]
        public void P6_Villagers_UnloadingTheMod_RemovesTheirAgents()
        {
            Start(allowAgents: true, new ScriptedVillagerModel());
            Assert.AreEqual(3, _world.Director.Agents.Count);

            _world.Stack.Runtime.UnloadMod(Mod);
            _world.Frame();

            Assert.AreEqual(0, _world.Director.Agents.Count);
            Assert.IsNull(_world.Workspace.FindFirstChild("Mira"), "and their bodies");
        }
    }
}
