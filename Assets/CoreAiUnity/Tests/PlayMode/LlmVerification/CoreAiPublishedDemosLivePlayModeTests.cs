#if UNITY_EDITOR && COREAI_LLM && COREAI_LUA
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Authority;
using CoreAI.Chat;
using CoreAI.Composition;
using CoreAI.Features.Audit;
using CoreAI.Infrastructure.Llm;
using CoreAI.Infrastructure;
using CoreAI.Infrastructure.AiMemory;
using CoreAI.Infrastructure.Logging;
using CoreAI.Infrastructure.Lua;
using CoreAI.Infrastructure.World;
using CoreAI.Logging;
using CoreAI.Messaging;
using CoreAI.Mods.Rbx.Binding;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.WorldPackages;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using VContainer;
using VContainer.Unity;
using Object = UnityEngine.Object;

namespace CoreAI.Tests.PlayMode
{
    /// <summary>Explicit real-provider actions in every scene of the published seventeen-scene matrix.</summary>
    [Explicit("Requires explicit COREAI_TEST_BASE_URL / COREAI_TEST_MODEL and a real HTTP provider.")]
    [Category("LiveLlm")]
    [Timeout(TestTimeoutMs)]
    public sealed class CoreAiPublishedDemosLivePlayModeTests
    {
        private const int TestTimeoutMs = 180_000;
        private PublishedDemoLiveSession _session;

        [SetUp]
        public void SetUp()
        {
            LogAssert.ignoreFailingMessages = false;
            _session = new PublishedDemoLiveSession(TestTimeoutMs);
        }

        [TearDown]
        public void RestoreSettings()
        {
            _session?.RestoreSettings();
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (_session != null)
            {
                yield return _session.Close();
                _session = null;
            }
        }

        [UnityTest]
        public IEnumerator Demo01_FullAccess_SelfTestTetrisAndLiveChat()
        {
            yield return Open("FullAccess/FullAccessDemo");
            object driver = PublishedDemoLiveSession.FindComponent("LuaPlatformExampleController");
            yield return _session.WaitUntil(() => PublishedDemoLiveSession.Read<bool>(driver, "IsReady"), 10f, "platform driver");
            _session.ReserveModId("platform_selftest_a");
            _session.ReserveModId("platform_selftest_b");
            _session.ReserveModId("tetris3d");
            PublishedDemoLiveSession.Call(driver, "RunSelfTest");
            yield return _session.WaitUntil(() =>
                !PublishedDemoLiveSession.Read<string>(driver, "SelfTestSummary").Contains("running"), 12f, "platform self-test verdict");
            string verdict = PublishedDemoLiveSession.Read<string>(driver, "SelfTestSummary");
            StringAssert.Contains("PASS", verdict);
            StringAssert.DoesNotContain("FAIL", verdict);
            PublishedDemoLiveSession.Call(driver, "StartTetris");
            yield return _session.WaitUntil(() => PublishedDemoLiveSession.Read<bool>(driver, "IsTetrisRunning") &&
                !string.IsNullOrWhiteSpace(PublishedDemoLiveSession.Read<string>(driver, "TetrisHud")), 10f, "Tetris board and HUD");
            RbxInstance activePiece = _session.Workspace.GetDescendants().FirstOrDefault(instance => instance.Name.EndsWith("_a1", StringComparison.Ordinal));
            Assert.IsNotNull(activePiece);
            RbxInstance firstColumn = activePiece.Parent.GetChildren().Single(instance => instance.Name.EndsWith("_wf2", StringComparison.Ordinal));
            RbxInstance secondColumn = activePiece.Parent.GetChildren().Single(instance => instance.Name.EndsWith("_wf3", StringComparison.Ordinal));
            yield return _session.WaitUntil(() => _session.WorldHost.Binder.GetLivePositionStuds(activePiece.Id).Y >
                _session.WorldHost.Binder.GetLivePositionStuds(firstColumn.Id).Y, 2f, "Tetris active piece is rendered above the floor");
            float beforeX = _session.WorldHost.Binder.GetLivePositionStuds(activePiece.Id).X;
            float leftmostX = _session.WorldHost.Binder.GetLivePositionStuds(firstColumn.Id).X;
            if (beforeX <= leftmostX + 0.05f)
            {
                // WHY: the real demo chooses a random spawn/target, including the left wall where another left move is invalid.
                float cellWidth = _session.WorldHost.Binder.GetLivePositionStuds(secondColumn.Id).X - leftmostX;
                PublishedDemoLiveSession.Call(driver, "TetrisMove", "1");
                yield return _session.WaitUntil(() => _session.WorldHost.Binder.GetLivePositionStuds(activePiece.Id).X >
                    beforeX + cellWidth * 0.5f, 2f, "Tetris right input makes room for the left move");
                beforeX = _session.WorldHost.Binder.GetLivePositionStuds(activePiece.Id).X;
            }
            PublishedDemoLiveSession.Call(driver, "TetrisMove", "-1");
            yield return _session.WaitUntil(() => _session.WorldHost.Binder.GetLivePositionStuds(activePiece.Id).X < beforeX - 0.05f, 2f, "Tetris left input moves the rendered piece");
            PublishedDemoLiveSession.Call(driver, "TetrisMove", "1");
            yield return null;
            PublishedDemoLiveSession.Call(driver, "StopTetris");
            Assert.IsFalse(PublishedDemoLiveSession.Read<bool>(driver, "IsTetrisRunning"));
            yield return _session.LiveChat();
            _session.Receipt("PASS", verdict + "; Tetris loaded, nudged, and stopped");
        }

        [UnityTest]
        public IEnumerator Demo02_GameplayServices_TourContactGravityAndLiveHost()
        {
            yield return Open("GameplayServices/GameplayServicesDemo");
            _session.ReserveModId("gameplay-services-tour");
            PublishedDemoLiveSession.Click("Run tour");
            StringAssert.DoesNotContain("The tour failed:", PublishedDemoLiveSession.ReadUiText(),
                "The actual tour controller reports a load failure before any world can appear.");
            yield return _session.WaitUntil(() => _session.Workspace.FindFirstChild("Door") != null, 5f, "tour world");
            Assert.IsNotNull(_session.Workspace.FindFirstChild("KillBrick"));
            RbxHumanoid humanoid = _session.Workspace.FindFirstChild("Humanoid") as RbxHumanoid;
            Assert.IsNotNull(humanoid);
            LuaCsModStack stack = _session.ModsContainer.Resolve<LuaCsModStack>();
            double gravity = stack.GameplayBindings.RbxApi.WorldPhysics.Gravity;
            PublishedDemoLiveSession.Click("Toggle gravity");
            Assert.AreNotEqual(gravity, stack.GameplayBindings.RbxApi.WorldPhysics.Gravity);
            PublishedDemoLiveSession.Click("Toggle gravity");
            Assert.AreEqual(gravity, stack.GameplayBindings.RbxApi.WorldPhysics.Gravity, 0.001d);
            PublishedDemoLiveSession.Click("Drop a block");
            yield return _session.WaitUntil(() => humanoid.Health <= 0d, 15f, "dropped block contact kills the tour Humanoid");
            _session.AssertModHealthy("gameplay-services-tour");
            yield return _session.LiveChat();
            _session.Receipt("PASS", "tour built; physical contact changed Humanoid health; gravity restored");
        }

        [UnityTest]
        public IEnumerator Demo03_Hub_LiveChatStatisticsModsAndTranscript()
        {
            yield return Open("Hub/CoreAiHubDemo");
            yield return _session.LiveChat();
            object hub = PublishedDemoLiveSession.FindComponent("CoreAiHubWindow");
            foreach (string page in new[] { "coreai.hub.statistics", "coreai.hub.mods", "coreai.hub.chat" })
            {
                PublishedDemoLiveSession.Call(hub, "ActivatePage", page);
                yield return null;
                Assert.AreEqual(page, PublishedDemoLiveSession.Read<string>(hub, "ActivePageId"));
            }
            Assert.IsNotNull(_session.Panel.ChatService);
            _session.Receipt("PASS", "live panel result; Statistics/Mods/Chat pages activate");
        }

        [UnityTest]
        public IEnumerator Demo04_LiveMechanics_OwnedRuleChangesRealBattleAndRestores()
        {
            yield return Open("LiveMechanics/LiveMechanicsDemo");
            LuaCsLogicSlots slots = _session.ModsContainer.Resolve<LuaCsLogicSlots>();
            if (slots.IsOverridden("damage_formula"))
            {
                _session.Ignore("An existing user mod owns damage_formula; replacing it would destroy that override.");
            }

            string id = _session.NewModId("rule");
            _session.Runtime.LoadMod(_session.Actor, id,
                "logic_define('damage_formula', function(atk, def) return 33 end)",
                LuaCapabilities.Read | LuaCapabilities.LogicOverride, false);
            Assert.IsTrue(slots.TryInvokeNumber("damage_formula", out double damage, 25d, 10d));
            Assert.AreEqual(33d, damage);
            yield return _session.WaitUntil(() => PublishedDemoLiveSession.ReadUiText().Contains("33 (Lua rule)"), 6f, "battle uses the owned Lua rule");
            _session.Runtime.UnloadMod(_session.Actor, id);
            Assert.IsFalse(slots.IsOverridden("damage_formula"));
            yield return _session.WaitUntil(() => PublishedDemoLiveSession.ReadUiText().Contains("15 (C# rule)"), 6f, "battle returns to vanilla rule");
            yield return _session.LiveChat();
            _session.Receipt("PASS", "real battle hit 33 under owned mod and returned to vanilla 15");
        }

        [UnityTest]
        public IEnumerator Demo05_ModsChat_ManagerDeactivatesAndActivatesOnlyOwnedMod()
        {
            yield return Open("LiveMechanicsMods/LiveMechanicsModsChatDemo");
            string id = _session.NewModId("manager");
            _session.Runtime.LoadMod(_session.Actor, id,
                "hooks_every(0.25, function() report('" + id + "') end)", LuaCapabilities.Read);
            object manager = PublishedDemoLiveSession.FindComponent("LiveMechanicsModsChatPersistenceController");
            PublishedDemoLiveSession.Write(manager, "PanelVisible", true);
            yield return _session.WaitUntil(() => PublishedDemoLiveSession.FindButton("Deactivate", id) != null, 5f, "owned mod manager row");
            PublishedDemoLiveSession.Click("Logs", id);
            Assert.IsTrue(_session.Runtime.GetModReportLoggingEnabled(_session.Actor, id));
            yield return _session.WaitUntil(() => _session.Runtime.GetRecentReports(_session.Actor, id).Count > 0, 5f, "owned timer report");
            PublishedDemoLiveSession.Click("Deactivate", id);
            Assert.IsFalse(_session.Runtime.IsLoaded(_session.Actor, id));
            yield return _session.WaitUntil(() => PublishedDemoLiveSession.FindButton("Activate", id) != null, 5f, "inactive owned row");
            PublishedDemoLiveSession.Click("Activate", id);
            Assert.IsTrue(_session.Runtime.IsLoaded(_session.Actor, id));
            _session.AssertModHealthy(id);
            yield return _session.LiveChat();
            _session.Receipt("PASS", "owned row Logs/Deactivate/Activate; real timer reports; live panel");
        }

        [UnityTest]
        public IEnumerator Demo06_WaveAutoBattler_RealTicksReachOwnedObserver()
        {
            yield return Open("LiveMechanicsMods/WaveAutoBattlerModsDemo");
            object driver = PublishedDemoLiveSession.FindComponent("WaveAutoBattlerModsDemoController");
            Assert.IsTrue(PublishedDemoLiveSession.Read<bool>(driver, "IsReady"));
            string id = _session.NewModId("wave");
            _session.Runtime.LoadMod(_session.Actor, id,
                "hooks_on('battle_tick', function(name, payload) report('" + id + " ' .. payload) end)", LuaCapabilities.Read, false);
            _session.Runtime.SetModReportLoggingEnabled(_session.Actor, id, true);
            yield return _session.WaitUntil(() => _session.Runtime.GetRecentReports(_session.Actor, id).Count >= 2, 8f, "two real battle ticks");
            Assert.Greater(PublishedDemoLiveSession.Read<int>(driver, "Wave"), 0);
            StringAssert.Contains(":", _session.Runtime.GetRecentReports(_session.Actor, id)[0].Message);
            _session.Runtime.UnloadMod(_session.Actor, id);
            int reports = _session.Runtime.GetRecentReports(_session.Actor, id).Count;
            yield return _session.WaitSeconds(0.5f);
            Assert.AreEqual(reports, _session.Runtime.GetRecentReports(_session.Actor, id).Count);
            yield return _session.LiveChat();
            _session.Receipt("PASS", "wave payloads observed; observer stops after unloading; live panel");
        }

        [UnityTest]
        public IEnumerator Demo07_LuaMods_ShippedEventAndOverrideUseActualButtons()
        {
            yield return Open("LuaMods/LuaModsDemo");
            _session.ReserveModId("wave_director");
            _session.ReserveModId("damage_tuner");
            LuaCsLogicSlots slots = _session.ModsContainer.Resolve<LuaCsLogicSlots>();
            if (slots.IsOverridden("damage_formula"))
            {
                _session.Ignore("An existing user rule owns damage_formula; the sample reset cannot run safely.");
            }

            PublishedDemoLiveSession.Click("Load mod");
            Assert.IsTrue(_session.Runtime.IsLoaded(_session.Actor, "wave_director"));
            PublishedDemoLiveSession.Click("Emit 'wave_started'");
            yield return _session.WaitUntil(() => PublishedDemoLiveSession.ReadUiText().Contains("wave_director ->"), 5f, "shipped event callback");
            _session.AssertModHealthy("wave_director");
            PublishedDemoLiveSession.Click("Unload");
            Assert.IsFalse(_session.Runtime.IsLoaded(_session.Actor, "wave_director"));
            PublishedDemoLiveSession.Click("Load override mod");
            Assert.IsTrue(slots.TryInvokeNumber("damage_formula", out double damage, 25d, 10d));
            Assert.AreEqual(45d, damage);
            PublishedDemoLiveSession.Click("Unload + reset slot");
            Assert.IsFalse(slots.IsOverridden("damage_formula"));
            yield return _session.LiveChat();
            _session.Receipt("PASS", "shipped mod/event and damage 45; unload restores vanilla slot");
        }

        [UnityTest]
        public IEnumerator Demo08_MiniRpg_LiveChatAndManagerReturnToCamera()
        {
            yield return Open("MiniRpg/MiniRpgModsDemo");
            yield return _session.LiveChat();
            object manager = PublishedDemoLiveSession.FindComponent("LiveMechanicsModsChatPersistenceController");
            bool visible = PublishedDemoLiveSession.Read<bool>(manager, "PanelVisible");
            PublishedDemoLiveSession.Write(manager, "PanelVisible", !visible);
            yield return null;
            Assert.AreEqual(!visible, PublishedDemoLiveSession.Read<bool>(manager, "PanelVisible"));
            PublishedDemoLiveSession.Write(manager, "PanelVisible", visible);
            Assert.IsNotNull(Object.FindFirstObjectByType<Camera>());
            _session.Receipt("PARTIAL", "live panel and manager toggle passed; native walk/look input requires interactive QA");
            Assert.Ignore("Live HTTP chat/manager passed; native walk/look input remains an interactive acceptance gate.");
        }

        [UnityTest]
        public IEnumerator Demo09_ModdableUnits_UniqueUnitsFightAndLiveHostResponds()
        {
            yield return Open("ModdableUnits/ModdableUnitsDemo");
            object driver = PublishedDemoLiveSession.FindComponent("ModdableUnitsDemoController");
            string ally = _session.RunId + "_ally";
            string enemy = _session.RunId + "_enemy";
            int before = (int)PublishedDemoLiveSession.Call(driver, "Count", "all");
            Assert.IsTrue((bool)PublishedDemoLiveSession.Call(driver, "Define", ally, "ally", 100d, 50d, 1d, 2d, "#55ccff"));
            Assert.IsTrue((bool)PublishedDemoLiveSession.Call(driver, "Define", enemy, "enemy", 1d, 0d, 1d, 2d, "#ff9966"));
            Assert.AreNotEqual(0, PublishedDemoLiveSession.Call(driver, "Spawn", ally, -0.5d, 0d));
            Assert.AreNotEqual(0, PublishedDemoLiveSession.Call(driver, "Spawn", enemy, 0.5d, 0d));
            Assert.AreEqual(before + 2, PublishedDemoLiveSession.Call(driver, "Count", "all"));
            yield return _session.WaitUntil(() => (int)PublishedDemoLiveSession.Call(driver, "Count", "all") < before + 2, 8f, "owned enemy dies in actual combat");
            yield return _session.LiveChat();
            _session.Receipt("PASS", "unique public-controller archetypes spawned and fought; live panel");
        }

        [UnityTest]
        public IEnumerator Demo10_MultiplayerFoundation_ProofAndPrivateLiveActorChat()
        {
            yield return Open("MultiplayerFoundation/MultiplayerFoundationDemo");
            object driver = PublishedDemoLiveSession.FindComponent("MultiplayerFoundationDemoController");
            int initial = PublishedDemoLiveSession.Read<int>(driver, "ActorCount");
            PublishedDemoLiveSession.Call(driver, "SetActorCountAndRerun", initial + 1);
            Assert.IsTrue(PublishedDemoLiveSession.Read<bool>(PublishedDemoLiveSession.Read<object>(driver, "Report"), "Passed"));
            PublishedDemoLiveSession.Call(driver, "SetActorCountAndRerun", initial);
            object report = PublishedDemoLiveSession.Read<object>(driver, "Report");
            Assert.IsTrue(PublishedDemoLiveSession.Read<bool>(report, "Passed"));
            IList actors = ((IEnumerable)PublishedDemoLiveSession.Read<object>(report, "Actors")).Cast<object>().ToArray();
            CancellationTokenSource cancellation = _session.Requests.CreateCancellation();
            string marker = _session.RunId + "_actor_a";
            Task<LlmCompletionResult> turn = _session.Requests.Track((Task<LlmCompletionResult>)PublishedDemoLiveSession.Call(
                driver, "SendChatAsync", 0, "Reply briefly with " + marker, cancellation.Token));
            yield return _session.WaitTask(turn, 90f, "actor A live chat", cancellation);
            PublishedDemoLiveSession.AssertCompletion(turn.Result);
            IEnumerable transcriptA = PublishedDemoLiveSession.Read<object>(actors[0], "ChatTranscript") as IEnumerable;
            IEnumerable transcriptB = PublishedDemoLiveSession.Read<object>(actors[1], "ChatTranscript") as IEnumerable;
            StringAssert.Contains(marker, string.Join(" ", transcriptA.Cast<string>()));
            StringAssert.DoesNotContain(marker, string.Join(" ", transcriptB.Cast<string>()));
            _session.Receipt("PASS", "actor count proof rerun; real actor A result; B transcript stays private; one-process proof only");
        }

        [UnityTest]
        public IEnumerator Demo11_OnlineAuthority_GrantRevokeAndLiveHost()
        {
            yield return Open("OnlineAuthority/OnlineAuthorityDemo");
            RbxInstance statue = _session.WorldHost.Registry.WorldRoot.FindFirstChild("Statue", true);
            Assert.IsNotNull(statue);
            long revision = _session.Revision(statue);
            PublishedDemoLiveSession.Click("Guest: move statue");
            Assert.AreEqual(revision, _session.Revision(statue));
            PublishedDemoLiveSession.Click("Host: grant");
            PublishedDemoLiveSession.Click("Guest: move statue");
            Assert.Greater(_session.Revision(statue), revision);
            revision = _session.Revision(statue);
            PublishedDemoLiveSession.Click("Host: revoke");
            PublishedDemoLiveSession.Click("Guest: move statue");
            Assert.AreEqual(revision, _session.Revision(statue));
            PublishedDemoLiveSession.Click("Host: move statue");
            Assert.Greater(_session.Revision(statue), revision);
            yield return _session.LiveChat();
            _session.Receipt("PASS", "guest refused/allowed/refused by revision; host still changes statue; one-process proof only");
        }

        [UnityTest]
        public IEnumerator Demo12_ProceduralMaterials_SelectionViewSweepAndLiveHost()
        {
            yield return Open("ProceduralMaterials/ProceduralMaterialsShowcase");
            object driver = PublishedDemoLiveSession.FindComponent("RbxProceduralMaterialsShowcase");
            int material = PublishedDemoLiveSession.Read<int>(driver, "SelectedMaterialIndex");
            int view = PublishedDemoLiveSession.Read<int>(driver, "SelectedViewIndex");
            bool sweep = PublishedDemoLiveSession.Read<bool>(driver, "SweepEnabled");
            PublishedDemoLiveSession.Call(driver, "AdvanceMaterial", 1);
            Assert.AreNotEqual(material, PublishedDemoLiveSession.Read<int>(driver, "SelectedMaterialIndex"));
            PublishedDemoLiveSession.Call(driver, "SelectView", (view + 1) % 5);
            Assert.AreNotEqual(view, PublishedDemoLiveSession.Read<int>(driver, "SelectedViewIndex"));
            PublishedDemoLiveSession.Call(driver, "SetSweepEnabled", !sweep);
            Assert.AreEqual(!sweep, PublishedDemoLiveSession.Read<bool>(driver, "SweepEnabled"));
            PublishedDemoLiveSession.Call(driver, "DumpMaterialApiEvidence");
            PublishedDemoLiveSession.Call(driver, "SelectMaterial", material);
            PublishedDemoLiveSession.Call(driver, "SelectView", view);
            PublishedDemoLiveSession.Call(driver, "SetSweepEnabled", sweep);
            yield return _session.LiveChat();
            _session.Receipt("PARTIAL", "material/view/sweep driver and live host passed; visual shader assessment requires captures");
            Assert.Ignore("Material/view/sweep drivers and live HTTP passed; visual shader assessment requires reviewed captures.");
        }

        [UnityTest]
        public IEnumerator Demo13_QwenGenie_HttpWishExecutesGoldToolAndSpendsCharge()
        {
            yield return Open("QwenDemo/QwenGenieDemo");
            yield return _session.UiModelTurn("Make a wish", "DemoGenie", "Grant three gold coins.", "grant_gold");
            StringAssert.Contains("wishes left (code-enforced limit): <b>2</b>", PublishedDemoLiveSession.ReadUiText());
            Assert.IsNotNull(GameObject.Find("GenieRoot"));
            Assert.Greater(_session.MaximumObservedEffectChildren, _session.InitialObservedEffectChildren, "The real gold tool must create effects during its turn.");
            PublishedDemoLiveSession.Click("Reset lamp (+3)");
            yield return null;
            StringAssert.Contains("wishes left (code-enforced limit): <b>3</b>", PublishedDemoLiveSession.ReadUiText());
            yield return _session.LiveChat();
            _session.Receipt("PASS", "actual wish UI through HTTP; grant_gold receipt/effect; charge 3 to 2 and reset");
        }

        [UnityTest]
        public IEnumerator Demo14_QwenSpellcraft_HttpCastExecutesToolAndSpendsMana()
        {
            yield return Open("QwenDemo/QwenSpellcraftDemo");
            yield return _session.UiModelTurn("Cast", "DemoMage", "A weak fire spell.", "cast_spell");
            string text = PublishedDemoLiveSession.ReadUiText();
            Match mana = Regex.Match(text, @"mana \(code-enforced guard\): <b>(\d+)/100</b>");
            Assert.IsTrue(mana.Success, text);
            Assert.Less(_session.MinimumObservedMana, 100, "The actual spell must spend authoritative mana during its turn, before regeneration.");
            PublishedDemoLiveSession.Click("Refill mana");
            yield return null;
            StringAssert.Contains("mana (code-enforced guard): <b>100/100</b>", PublishedDemoLiveSession.ReadUiText());
            yield return _session.LiveChat();
            _session.Receipt("PASS", "actual Cast UI through HTTP; cast_spell receipt; mana spent then refilled");
        }

        [UnityTest]
        public IEnumerator Demo15_Skills_RealModelCraftsAndAttacksThroughDemoAgent()
        {
            yield return Open("Skills/SkillsDemo");
            yield return _session.UiModelTurn("Ask the Game Master", "DemoGameMaster",
                "Craft me an iron sword, then attack the training dummy", "call_skill_tool");
            string text = PublishedDemoLiveSession.ReadUiText();
            StringAssert.Contains("iron_ingot x2", text);
            StringAssert.Contains("wood x4", text);
            Match health = Regex.Match(text, @"Training dummy HP: (\d+)");
            Assert.IsTrue(health.Success, text);
            Assert.Less(int.Parse(health.Groups[1].Value), 100);
            StringAssert.DoesNotContain("Crafted: -", text);
            _session.Receipt("PASS", "real GameMaster consumed one iron/wood, crafted an item, and damaged dummy");
        }

        [UnityTest]
        public IEnumerator Demo16_WorldCommands_ActualSpawnDestroyAndLiveHost()
        {
            yield return Open("WorldCommands/WorldCommandsDemo");
            PublishedDemoLiveSession.Click("Spawn enemy");
            yield return _session.WaitUntil(() => GameObject.Find("cmd_enemy_1") != null, 5f, "world command actually spawns");
            PublishedDemoLiveSession.Click("Destroy last");
            yield return _session.WaitUntil(() => GameObject.Find("cmd_enemy_1") == null, 5f, "world command actually destroys");
            yield return _session.LiveChat();
            _session.Receipt("PASS", "real scene command controls spawn and destroy their object; real host result");
        }

        [UnityTest]
        public IEnumerator Demo17_StandaloneChat_RealTypedResultAndReopen()
        {
            yield return _session.Open("Assets/CoreAiUnity/Scenes/CoreAiChatDemo.unity");
            yield return _session.LiveChat();
            CoreAiChatPanel panel = _session.Panel;
            Assert.IsNotNull(panel);
            panel.SetCollapsed(true, false);
            yield return null;
            panel.SetCollapsed(false, false);
            yield return null;
            Assert.IsFalse(panel.IsBusy);
            Assert.IsNotNull(panel.ChatService);
            _session.Receipt("PASS", "actual SmartChat panel typed completion and collapse/reopen");
        }

        private IEnumerator Open(string relativeScene)
        {
            return _session.Open("Assets/CoreAI.Demos/" + relativeScene + ".unity");
        }
    }

    /// <summary>Owns one scene, reversible HTTP settings, scoped memory, requests, and explicitly reserved QA ids.</summary>
    internal sealed class PublishedDemoLiveSession
    {
        private readonly Dictionary<CoreAISettingsAsset, string> _settings = new();
        private readonly HashSet<CoreAISettingsAsset> _initiallyDirtySettings = new();
        private readonly Dictionary<string, byte[]> _assetBytes = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _originalModSources = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _originalManifests = new(StringComparer.Ordinal);
        private readonly HashSet<string> _ownedModIds = new(StringComparer.Ordinal);
        private readonly HashSet<string> _uiRoles = new(StringComparer.Ordinal);
        private readonly List<string> _errors = new();
        private readonly CoreAISettingsAsset _previousSettings;
        private IDisposable _installers;
        private readonly List<string> _ownedStorageRoots = new();
        private string _worldStoreRoot;
        private string _modStateRoot;
        private string _modSourceRoot;
        private bool _isolationApplied;
        private bool _modsStorageIsolated;
        private string _scenePath;
        private float _started;
        private bool _capturing;
        private bool _opened;
        private bool _reported;
        private static PublishedDemoLiveSession s_loadingSession;
        private static PublishedDemoLiveSession s_activeSession;
        private static ulong s_mirrorSceneId = 1_000_000;

        static PublishedDemoLiveSession()
        {
            EditorApplication.playModeStateChanged += state =>
            {
                if (state == PlayModeStateChange.ExitingPlayMode)
                {
                    s_loadingSession = null;
                    s_activeSession?.RestoreSettings();
                    s_activeSession = null;
                }
            };
        }

        internal PublishedDemoLiveSession(int timeoutMilliseconds)
        {
            Requests = new LiveTestRequestScope(timeoutMilliseconds);
            RunId = "qa_live_" + Guid.NewGuid().ToString("N");
            _previousSettings = CoreAISettingsAsset.Instance;
            s_activeSession = this;
        }

        internal string RunId { get; }
        internal LiveTestRequestScope Requests { get; }
        internal CoreAILifetimeScope Scope { get; private set; }
        internal IObjectResolver ModsContainer { get; private set; }
        internal ILuaModRuntime Runtime { get; private set; }
        internal ActorContext Actor { get; private set; }
        internal RbxWorldHost WorldHost => Object.FindFirstObjectByType<RbxWorldHost>();
        internal RbxInstance Workspace => WorldHost?.Game?.FindFirstChildOfClass("Workspace");
        internal CoreAiChatPanel Panel => Object.FindFirstObjectByType<CoreAiChatPanel>(FindObjectsInactive.Include);
        internal LlmCompletionResult LastCompletion { get; private set; }
        internal int MinimumObservedMana { get; private set; } = int.MaxValue;
        internal int InitialObservedEffectChildren { get; private set; }
        internal int MaximumObservedEffectChildren { get; private set; }

        internal IEnumerator Open(string scenePath)
        {
            _scenePath = scenePath;
            _started = Time.realtimeSinceStartup;
            string explicitBase = Environment.GetEnvironmentVariable(PlayModeOpenAiTestConfig.EnvBaseUrl);
            string explicitModel = Environment.GetEnvironmentVariable(PlayModeOpenAiTestConfig.EnvModel);
            if (string.IsNullOrWhiteSpace(explicitBase) || string.IsNullOrWhiteSpace(explicitModel))
            {
                Ignore("Explicit COREAI_TEST_BASE_URL and COREAI_TEST_MODEL are required; no asset, local, native, or Offline fallback is allowed.");
            }

            PlayModeOpenAiTestConfig.ResolvedConfig config = PlayModeOpenAiTestConfig.Resolve();
            Assert.IsTrue(config.IsComplete, PlayModeOpenAiTestConfig.BuildIgnoreReason(config));
            Assert.IsTrue(Uri.TryCreate(config.BaseUrl, UriKind.Absolute, out Uri endpoint) &&
                (endpoint.Scheme == Uri.UriSchemeHttp || endpoint.Scheme == Uri.UriSchemeHttps), "An explicit HTTP provider endpoint is required.");
            Assert.IsTrue(File.Exists(scenePath), "Published scene is missing: " + scenePath);
            SnapshotAndOverride(CoreAISettingsAsset.Instance, config);
            foreach (string dependency in AssetDatabase.GetDependencies(scenePath, true))
            {
                if (dependency.EndsWith(".asset", StringComparison.OrdinalIgnoreCase))
                {
                    SnapshotAndOverride(AssetDatabase.LoadAssetAtPath<CoreAISettingsAsset>(dependency), config);
                }
            }

            Assert.IsNotEmpty(_settings, "The scene must resolve a CoreAI settings asset before its scope builds.");
            _worldStoreRoot = Path.GetFullPath(Path.Combine(Application.temporaryCachePath, "CoreAI.LiveQA", RunId));
            Assert.IsFalse(Directory.Exists(_worldStoreRoot), "The GUID-owned QA world-store directory already exists.");
            Directory.CreateDirectory(_worldStoreRoot);
            _ownedStorageRoots.Add(_worldStoreRoot);
            _modStateRoot = Path.Combine(_worldStoreRoot, "ModState");
            _modSourceRoot = Path.Combine(_worldStoreRoot, "ModPackages");
            _installers = LifetimeScope.Enqueue(IsolateContainerStorage);
            Application.logMessageReceived += Capture;
            _capturing = true;
            s_loadingSession = this;
            _opened = true;
            EditorSceneManager.LoadSceneInPlayMode(scenePath, new LoadSceneParameters(LoadSceneMode.Single));
            for (int frame = 0; frame < 5; frame++)
            {
                yield return null;
            }

            _installers.Dispose();
            _installers = null;
            s_loadingSession = null;
            Assert.IsTrue(_isolationApplied, "File-store factories must be isolated before container construction; no live request may run after a missed installer.");
            Scope = Object.FindFirstObjectByType<CoreAILifetimeScope>();
            Assert.IsNotNull(Scope, "The published scene must contain its actual production CoreAI scope.");
            Assert.IsNotNull(Scope.Container);
            Assert.IsTrue(Scope.Settings.UseHttpApi, "The actual scene scope must use the explicit HTTP backend.");
            Assert.AreEqual(config.Model, Scope.Settings.ModelName);
            Assert.AreEqual(config.BaseUrl.TrimEnd('/'), Scope.Settings.ApiBaseUrl.TrimEnd('/'));
            Actor = Scope.Container.Resolve<IActorIdentityProvider>().GetActorContext(BuiltInAgentRoleIds.Programmer);
            CoreAiModsLifetimeScope mods = Object.FindFirstObjectByType<CoreAiModsLifetimeScope>(FindObjectsInactive.Include);
            if (mods != null)
            {
                Assert.IsTrue(_modsStorageIsolated, "Mod source/state and world backups must use only the owned storage root.");
                Assert.IsNotNull(mods.Container);
                ModsContainer = mods.Container;
                Runtime = ModsContainer.Resolve<ILuaModRuntime>();
                IRbxWorldPackageStore packageStore = ModsContainer.Resolve<IRbxWorldPackageStore>();
                Assert.IsInstanceOf<FileRbxWorldPackageStore>(packageStore);
                Assert.Throws<InvalidOperationException>(() => mods.SetWorldPackageStore(packageStore),
                    "A host must not retarget the already composed package store.");
                Assert.AreSame(packageStore, ModsContainer.Resolve<IRbxWorldPackageStore>());
                foreach (LuaModInfo info in Runtime.ListMods(Actor))
                {
                    Assert.IsTrue(Runtime.TryGetModSource(Actor, info.Id, out string source));
                    _originalModSources.Add(info.Id, source);
                }

                ILuaModSourceStore sourceStore = ModsContainer.Resolve<ILuaModSourceStore>();
                foreach (LuaModManifest manifest in sourceStore.List())
                {
                    Assert.IsTrue(sourceStore.TryLoad(manifest.Id, out string source, out LuaModManifest current));
                    _originalManifests.Add(manifest.Id, source + "\n" + JsonUtility.ToJson(current));
                }
                AssertOwnedStorageCanAdmitAndRoundTrip();
            }

            Receipt("STARTED", "production scene scope; HTTP model=" + config.Model + "; endpoint=" + endpoint.Host + "; original file-store types and facades rooted under " + _worldStoreRoot);
        }

        internal IEnumerator LiveChat()
        {
            AssertHttpRole(BuiltInAgentRoleIds.SmartChat);
            AssertHttpRole(BuiltInAgentRoleIds.Programmer);
            object hub = TryFindComponent("CoreAiHubWindow");
            if (hub != null)
            {
                Call(hub, "SetCollapsed", false);
                Call(hub, "ActivatePage", "coreai.hub.chat");
                yield return null;
            }

            CoreAiChatPanel panel = Panel;
            if (panel != null)
            {
                panel.SetCollapsed(false, false);
                yield return WaitUntil(() => panel.ChatService != null, 8f, "actual demo chat service");
                CancellationTokenSource cancellation = Requests.CreateCancellation();
                CancellationTokenSource deadline = Requests.CreateCancellation();
                deadline.CancelAfter(TimeSpan.FromSeconds(Requests.Cap(90f)));
                Task<CoreAiChatExternalSubmitResult> turn = Requests.Track(panel.SubmitMessageFromExternalResultAsync(
                    "Reply briefly with READY " + RunId + ". Do not call tools or change the world.",
                    new CoreAiChatExternalSubmitOptions { DeadlineCancellationToken = deadline.Token }, cancellation.Token));
                yield return WaitTask(turn, 95f, "published demo panel real completion", cancellation);
                Assert.IsTrue(turn.Result.Admitted, "The actual panel rejected the turn: " + turn.Result.Rejection);
                LastCompletion = turn.Result.Completion;
                AssertCompletion(LastCompletion);
                Assert.IsFalse(panel.IsBusy, "The actual panel must return to idle after its terminal result.");
            }
            else
            {
                yield return Turn(BuiltInAgentRoleIds.SmartChat,
                    "Reply briefly with READY " + RunId + ". Do not call tools or change the world.", 90f, 128);
            }

            Receipt("MODEL", "typed nonempty successful completion; model=" + LastCompletion.Model + "; tools=" + (LastCompletion.ExecutedToolCalls?.Count ?? 0));
        }

        internal IEnumerator Turn(string role, string prompt, float timeoutSeconds, int maxOutputTokens = 1024, string[] tools = null)
        {
            AssertHttpRole(role);
            IAiOrchestrationService orchestrator = Scope.Container.Resolve<IAiOrchestrationService>();
            IAiTaskResultService typed = orchestrator as IAiTaskResultService;
            Assert.IsNotNull(typed, "The real scene orchestration path must expose truthful completion results.");
            Assert.IsTrue(typed.SupportsTaskResults);
            CancellationTokenSource cancellation = Requests.CreateCancellation();
            cancellation.CancelAfter(TimeSpan.FromSeconds(Requests.Cap(timeoutSeconds)));
            Task<LlmCompletionResult> turn = Requests.Track(typed.RunTaskResultAsync(new AiTaskRequest
            {
                RoleId = role,
                Hint = prompt,
                TraceId = RunId,
                SourceTag = RunId,
                CancellationScope = RunId,
                LuaScriptVersionKey = RunId,
                AllowedToolNames = tools,
                MaxOutputTokens = maxOutputTokens,
                MaxToolCallRoundtrips = 8
            }, cancellation.Token));
            yield return WaitTask(turn, timeoutSeconds + 2f, "actual scene role " + role, cancellation);
            LastCompletion = turn.Result;
            AssertCompletion(LastCompletion);
        }

        internal IEnumerator UiModelTurn(string caption, string role, string input, string requiredTool)
        {
            AssertHttpRole(role);
            Component button = null;
            yield return WaitUntil(() => (button = FindButton(caption)) != null && Read<bool>(button, "interactable"), 10f, "actual " + caption + " readiness");
            Component owner = button.GetComponentsInParent<MonoBehaviour>()
                .SingleOrDefault(candidate => candidate != null && candidate.GetType().Name == "CoreAiDemoPanel");
            Assert.IsNotNull(owner, "The action button must belong to the actual demo panel.");
            Component field = owner.GetComponentsInChildren<MonoBehaviour>()
                .SingleOrDefault(candidate => candidate != null && candidate.GetType().FullName == "TMPro.TMP_InputField");
            Assert.IsNotNull(field, "The demo must expose its actual text input.");
            Write(field, "text", input);
            yield return null;
            Assert.AreEqual(input, Read<string>(field, "text"), "The real demo input must retain the submitted description.");
            int baseline = CoreAi.GetToolCallHistorySnapshot()?.Count ?? 0;
            _uiRoles.Add(role);
            InitialObservedEffectChildren = GameObject.Find("GenieRoot")?.transform.childCount ?? 0;
            MaximumObservedEffectChildren = InitialObservedEffectChildren;
            Assert.IsTrue(Read<bool>(button, "interactable"), "The actual demo action must remain ready after entering its description.");
            Call(Read<object>(button, "onClick"), "Invoke");
            yield return WaitUntil(() => !Read<bool>(button, "interactable") ||
                (CoreAi.GetToolCallHistorySnapshot()?.Count ?? 0) > baseline,
                10f, "actual " + caption + " UI admits the real model turn");
            yield return null;
            yield return WaitUntil(() =>
            {
                Match mana = Regex.Match(ReadUiText(), @"mana \(code-enforced guard\): <b>(\d+)/100</b>");
                if (mana.Success)
                {
                    MinimumObservedMana = Math.Min(MinimumObservedMana, int.Parse(mana.Groups[1].Value));
                }
                MaximumObservedEffectChildren = Math.Max(MaximumObservedEffectChildren, GameObject.Find("GenieRoot")?.transform.childCount ?? 0);
                return Read<bool>(button, "interactable");
            }, 100f, "actual " + caption + " real model turn");
            // WHY end on the re-enabled action and not on a new tool receipt: a turn that ends in a provider
            // error (401, 429, empty reply) re-enables the button without a receipt, and waiting for one
            // turned a 137 ms failure into an opaque 100-second timeout. The short grace covers a receipt
            // recorded in the frame after the button re-enables.
            float receiptGrace = Time.realtimeSinceStartup + 2f;
            while ((CoreAi.GetToolCallHistorySnapshot()?.Count ?? 0) <= baseline && Time.realtimeSinceStartup < receiptGrace)
            {
                yield return null;
            }

            string text = ReadUiText();
            StringAssert.DoesNotContain("✘", text, "The actual demo reports a failed model/tool turn: " + text);
            IReadOnlyList<LlmToolCallRecord> history = CoreAi.GetToolCallHistorySnapshot();
            List<LlmToolCallRecord> own = history.Skip(baseline).Where(record => record != null).ToList();
            Assert.IsTrue(own.Any(record => record.Info.ToolName == requiredTool && record.Status == "completed"),
                "The actual demo agent must execute " + requiredTool + "; a textual promise is insufficient.");
            Assert.IsFalse(own.Any(record => record.Status == "failed" || record.Status == "cancelled" || record.Status == "error"),
                "The demo model turn produced a failed tool receipt.");
            StringAssert.DoesNotContain("(empty response", text);
            Receipt("MODEL_ACTION", caption + "; actual tool=" + requiredTool + "; calls=" + own.Count);
        }

        internal string NewModId(string suffix)
        {
            string id = RunId + "_" + suffix;
            ReserveModId(id);
            return id;
        }

        internal void ReserveModId(string id)
        {
            Assert.IsNotNull(Runtime, "This scene needs the actual mods composition.");
            ILuaModSourceStore sourceStore = ModsContainer.Resolve<ILuaModSourceStore>();
            if (Runtime.IsLoaded(Actor, id) || sourceStore.TryLoad(id, out _, out _))
            {
                Ignore("The sample id '" + id + "' already exists; QA must preserve its loaded source and saved package.");
            }

            _ownedModIds.Add(id);
        }

        internal void AssertModHealthy(string id)
        {
            LuaModInfo info = Runtime.ListMods(Actor).FirstOrDefault(mod => mod.Id == id);
            Assert.IsNotNull(info, "Expected loaded QA mod: " + id);
            Assert.IsFalse(info.Quarantined, "The owned QA mod was quarantined.");
            Assert.AreEqual(0, info.ErrorCount, "The owned QA mod produced handler failures.");
            Assert.IsEmpty(Runtime.GetRecentHandlerErrors(Actor, id));
        }

        private void AssertOwnedStorageCanAdmitAndRoundTrip()
        {
            string id = RunId + "_storage_probe";
            FileLuaModSourceStore sources = ModsContainer.Resolve<FileLuaModSourceStore>();
            ILuaModSourceAdmission admission = sources;
            Assert.IsTrue(admission.CanAdmit(id, out string refusal),
                "A fresh owned source root must admit a mod before a live request: " + refusal);
            ILuaModStore state = ModsContainer.Resolve<ILuaModStore>();
            Assert.IsInstanceOf<FileLuaModStore>(state);
            try
            {
                state.Set(id, "qa_probe", RunId);
                Assert.AreEqual(RunId, state.Get(id, "qa_probe"),
                    "The real file-backed key/value store must round-trip before a paid model turn.");
            }
            finally
            {
                state.Clear(id);
            }
            Receipt("STORAGE_PREFLIGHT", "fresh source admission and real file-backed owned key/value roundtrip passed");
        }

        internal long Revision(RbxInstance instance)
        {
            Assert.IsTrue(WorldHost.Registry.TryGetRecord(instance.Id, out InstanceRecord record));
            return record.Revision;
        }

        internal IEnumerator WaitTask(Task task, float seconds, string label, CancellationTokenSource cancellation)
        {
            return PlayModeTestAwait.WaitTask(task, Requests.Cap(seconds), label, cancellation);
        }

        internal IEnumerator WaitUntil(Func<bool> predicate, float seconds, string label)
        {
            return PlayModeTestAwait.WaitUntil(predicate, Requests.Cap(seconds), label);
        }

        internal IEnumerator WaitSeconds(float seconds)
        {
            float end = Time.realtimeSinceStartup + Requests.Cap(seconds);
            while (Time.realtimeSinceStartup < end)
            {
                yield return null;
            }
        }

        internal void Receipt(string status, string detail)
        {
            if (status == "PASS" || status == "PARTIAL" || status == "IGNORED")
            {
                _reported = true;
            }

            TestContext.WriteLine("LIVE_DEMO_RECEIPT status=" + status + " scene=" + _scenePath +
                " run=" + RunId + " seconds=" + (Time.realtimeSinceStartup - _started).ToString("0.00") + " detail=" + detail);
        }

        internal void Ignore(string reason)
        {
            Receipt("IGNORED", reason);
            Assert.Ignore(reason);
        }

        internal IEnumerator Close()
        {
            List<string> cleanupErrors = new();
            try
            {
                _installers?.Dispose();
                _installers = null;
                s_loadingSession = null;
                Panel?.StopAgent();
                if (Scope != null && Scope.Container != null)
                {
                    IAiOrchestrationService orchestrator = Scope.Container.Resolve<IAiOrchestrationService>();
                    foreach (string role in _uiRoles)
                    {
                        orchestrator.CancelTasks(role);
                    }

                    orchestrator.CancelTasks(RunId);
                }

                yield return Requests.CancelAllAndDrain();
                if (Runtime != null)
                {
                    foreach (string id in _ownedModIds)
                    {
                        try
                        {
                            Runtime.ForgetMod(Actor, id);
                            ModsContainer.Resolve<ILuaModSourceStore>().Delete(id);
                            ModsContainer.Resolve<ILuaModStore>().Clear(id);
                        }
                        catch (Exception exception)
                        {
                            cleanupErrors.Add("Owned mod cleanup " + id + ": " + exception.Message);
                        }
                    }

                    foreach (KeyValuePair<string, string> saved in _originalModSources)
                    {
                        if (!Runtime.TryGetModSource(Actor, saved.Key, out string source) || source != saved.Value)
                        {
                            cleanupErrors.Add("A pre-existing loaded mod changed: " + saved.Key);
                        }
                    }

                    ILuaModSourceStore sourceStore = ModsContainer.Resolve<ILuaModSourceStore>();
                    foreach (KeyValuePair<string, string> saved in _originalManifests)
                    {
                        if (!sourceStore.TryLoad(saved.Key, out string source, out LuaModManifest manifest) ||
                            source + "\n" + JsonUtility.ToJson(manifest) != saved.Value)
                        {
                            cleanupErrors.Add("A pre-existing saved mod changed: " + saved.Key);
                        }
                    }
                }

                if (_opened)
                {
                    yield return PlayModeSceneSandbox.UnloadToEmptyScene();
                }

                RestoreSettings();
                foreach (string directory in _ownedStorageRoots)
                {
                    try
                    {
                        string absolute = Path.GetFullPath(directory);
                        bool owned = absolute == _worldStoreRoot || absolute == _modStateRoot || absolute == _modSourceRoot;
                        Assert.IsTrue(owned && absolute != Path.GetPathRoot(absolute) && !string.IsNullOrEmpty(Path.GetFileName(absolute)),
                            "Cleanup may remove only a reserved GUID-owned QA directory.");
                        if (Directory.Exists(absolute))
                        {
                            Assert.IsFalse((File.GetAttributes(absolute) & FileAttributes.ReparsePoint) != 0,
                                "Cleanup refuses a redirected QA directory.");
                            Directory.Delete(absolute, true);
                        }
                    }
                    catch (Exception exception)
                    {
                        cleanupErrors.Add("Owned directory cleanup: " + exception.Message);
                    }
                }
                foreach (KeyValuePair<string, byte[]> original in _assetBytes)
                {
                    if (!File.Exists(original.Key) || !original.Value.SequenceEqual(File.ReadAllBytes(original.Key)))
                    {
                        cleanupErrors.Add("A shared settings asset changed on disk: " + original.Key);
                    }
                }

                if (_capturing)
                {
                    Application.logMessageReceived -= Capture;
                    _capturing = false;
                }
                if (s_activeSession == this)
                {
                    s_activeSession = null;
                }
            }
            finally
            {
                RestoreSettings();
                s_loadingSession = null;
                if (_capturing)
                {
                    Application.logMessageReceived -= Capture;
                    _capturing = false;
                }
                if (s_activeSession == this)
                {
                    s_activeSession = null;
                }
            }

            if (!_reported)
            {
                Receipt("INCOMPLETE", "test did not reach its acceptance receipt; inspect the NUnit failure and earlier phases");
            }

            Assert.IsEmpty(cleanupErrors, string.Join("\n", cleanupErrors));
            Assert.IsEmpty(_errors, "Unexpected scene/provider/runtime errors:\n" + string.Join("\n", _errors));
        }

        internal void RestoreSettings()
        {
            foreach (KeyValuePair<CoreAISettingsAsset, string> snapshot in _settings)
            {
                if (snapshot.Key != null)
                {
                    EditorJsonUtility.FromJsonOverwrite(snapshot.Value, snapshot.Key);
                    if (_initiallyDirtySettings.Contains(snapshot.Key))
                    {
                        EditorUtility.SetDirty(snapshot.Key);
                    }
                    else
                    {
                        EditorUtility.ClearDirty(snapshot.Key);
                    }
                }
            }

            CoreAISettingsAsset.SetInstance(_previousSettings);
            CoreAISettings.Instance = _previousSettings;
        }

        private void AssertHttpRole(string role)
        {
            ILlmClientRegistry registry = Scope.Container.Resolve<ILlmClientRegistry>();
            Assert.AreEqual(LlmExecutionMode.ClientOwnedApi, registry.ResolveExecutionModeForRole(role),
                "The actual role route must use the explicit HTTP client; local/native/Offline routes are prohibited in this live fixture: " + role);
        }

        internal static void AssertCompletion(LlmCompletionResult result)
        {
            Assert.IsNotNull(result, "The production path returned no typed completion.");
            Assert.IsTrue(result.Ok, "Real provider turn failed: " + result.ErrorCode + " " + result.Error);
            Assert.AreEqual(LlmErrorCode.None, result.ErrorCode);
            Assert.IsFalse(string.IsNullOrWhiteSpace(result.Content), "The real model returned no visible answer.");
            foreach (LlmToolCallTrace call in result.ExecutedToolCalls ?? Array.Empty<LlmToolCallTrace>())
            {
                Assert.IsTrue(call.Success, "A real tool call failed: " + call.Name + " " + call.Detail);
            }
        }

        internal static object FindComponent(string typeName)
        {
            object component = TryFindComponent(typeName);
            Assert.IsNotNull(component, "Missing actual published scene component: " + typeName);
            return component;
        }

        internal static object TryFindComponent(string typeName)
        {
            return Object.FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .FirstOrDefault(component => component != null && component.GetType().Name == typeName);
        }

        internal static object Call(object target, string method, params object[] arguments)
        {
            Assert.IsNotNull(target);
            MethodInfo member = target.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .SingleOrDefault(candidate => candidate.Name == method && candidate.GetParameters().Length == arguments.Length);
            Assert.IsNotNull(member, "Missing public runtime driver: " + target.GetType().Name + "." + method);
            try
            {
                return member.Invoke(target, arguments);
            }
            catch (TargetInvocationException exception)
            {
                throw exception.InnerException ?? exception;
            }
        }

        internal static T Read<T>(object target, string property)
        {
            Assert.IsNotNull(target);
            PropertyInfo member = target.GetType().GetProperty(property, BindingFlags.Instance | BindingFlags.Public);
            Assert.IsNotNull(member, "Missing public runtime state: " + target.GetType().Name + "." + property);
            return (T)member.GetValue(target);
        }

        internal static void Write(object target, string property, object value)
        {
            PropertyInfo member = target.GetType().GetProperty(property, BindingFlags.Instance | BindingFlags.Public);
            Assert.IsNotNull(member, "Missing public runtime input: " + target.GetType().Name + "." + property);
            member.SetValue(target, value);
        }

        internal static Component FindButton(string caption, string rowId = null)
        {
            foreach (MonoBehaviour component in Object.FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None))
            {
                if (component == null || component.GetType().FullName != "UnityEngine.UI.Button")
                {
                    continue;
                }

                string label = ReadUiText(component.transform);
                if (label != caption && !component.name.Equals("Button_" + caption, StringComparison.Ordinal))
                {
                    continue;
                }

                if (rowId == null || ReadUiText(component.transform.parent).Contains(rowId))
                {
                    return component;
                }
            }

            return null;
        }

        internal static void Click(string caption, string rowId = null)
        {
            Component button = FindButton(caption, rowId);
            Assert.IsNotNull(button, "Missing real scene button: " + caption + " row=" + rowId);
            Assert.IsTrue(Read<bool>(button, "interactable"), "Scene button is disabled: " + caption);
            Call(Read<object>(button, "onClick"), "Invoke");
        }

        internal static string ReadUiText(Transform root = null)
        {
            MonoBehaviour[] components = root == null
                ? Object.FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None)
                : root.GetComponentsInChildren<MonoBehaviour>();
            List<string> text = new();
            foreach (MonoBehaviour component in components)
            {
                if (component != null && component.GetType().FullName == "TMPro.TextMeshProUGUI")
                {
                    text.Add(Read<string>(component, "text"));
                }
            }

            return string.Join("\n", text);
        }

        private void SnapshotAndOverride(CoreAISettingsAsset settings, PlayModeOpenAiTestConfig.ResolvedConfig config)
        {
            if (settings == null || _settings.ContainsKey(settings))
            {
                return;
            }

            if (EditorUtility.IsDirty(settings))
            {
                _initiallyDirtySettings.Add(settings);
            }
            _settings.Add(settings, EditorJsonUtility.ToJson(settings));
            string path = AssetDatabase.GetAssetPath(settings);
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                _assetBytes[path] = File.ReadAllBytes(path);
            }

            settings.ConfigureHttpApi(config.BaseUrl, config.ApiKey, config.Model, temperature: 0f,
                timeoutSeconds: 120, maxTokens: 1024, overrideTemperature: true);
            settings.ConfigureFallbackBackend(false, "", "");
            settings.SetOrchestratorTimeoutSeconds(130f);
            settings.SetMaxToolCallRoundtrips(8);
            CoreAISettingsOptions options = settings.ToOptions();
            options.EnableStreaming = config.Streaming;
            options.MaxLlmRequestRetries = 0;
            options.MaxContextOverflowRetries = 0;
            options.LogToolCallArguments = true;
            settings.ApplyOptions(options);
            if (!string.IsNullOrWhiteSpace(config.ExtraBodyJson))
            {
                foreach (JProperty property in JObject.Parse(config.ExtraBodyJson).Properties())
                {
                    settings.SetProviderBodyParameter(property.Name, property.Value.DeepClone());
                }
            }
            EditorUtility.ClearDirty(settings);
        }

        private void IsolateContainerStorage(IContainerBuilder builder)
        {
            Component origin = builder.ApplicationOrigin as Component;
            if (origin == null || origin.gameObject.scene.path != _scenePath)
            {
                return;
            }

            Dictionary<Type, Func<IObjectResolver, object>> replacements = new();
            HashSet<Type> required = new();
            if (origin is CoreAILifetimeScope core)
            {
                builder.RegisterInstance<ILlmEndpointRegistryStore>(new FileLlmEndpointRegistryStore(
                    Path.Combine(_worldStoreRoot, "llm-endpoints.json")));
                replacements.Add(typeof(FileLuaScriptVersionStore), resolver =>
                    new FileLuaScriptVersionStore(resolver.Resolve<IGameLogger>(), Path.Combine(_worldStoreRoot, "lua-versions.json")));
                replacements.Add(typeof(FileDataOverlayVersionStore), resolver =>
                    new FileDataOverlayVersionStore(resolver.Resolve<IGameLogger>(), Path.Combine(_worldStoreRoot, "overlays.json")));
                replacements.Add(typeof(AuditLogWriter), resolver =>
                    new AuditLogWriter(Path.Combine(_worldStoreRoot, "Audit"), new AuditLogWriterOptions()));
#if !UNITY_WEBGL
                replacements.Add(typeof(ITokenCalibrationStore), resolver =>
                    new FileTokenCalibrationStore(Path.Combine(_worldStoreRoot, "token-calibration.json"), null));
#endif
                required.UnionWith(replacements.Keys);
                replacements.Add(typeof(FileSkillStore), resolver =>
                    new FileSkillStore(Path.Combine(_worldStoreRoot, "Skills"), resolver.Resolve<ILog>()));
                replacements.Add(typeof(WorldStateManager), resolver => new WorldStateManager(
                    resolver.Resolve<IGameLogger>(), resolver.Resolve<ICoreAiPrefabRegistry>(),
                    resolver.ResolveOrDefault<ICoreAISettings>()?.AllowWorldPrimitives ?? true,
                    Path.Combine(_worldStoreRoot, "world_state.json")));
                required.Add(typeof(WorldStateManager));
                if (core.ConfiguredAgentMemoryPersistenceMode == AgentMemoryPersistenceMode.Persistent)
                {
                    replacements.Add(typeof(FileAgentMemoryStore), resolver =>
                        new FileAgentMemoryStore(resolver.Resolve<ILog>(), Path.Combine(_worldStoreRoot, "AgentMemory"),
                            core.ConfiguredChatHistoryMessageCap, core.ConfiguredTranscriptEntryCap));
                    replacements.Add(typeof(FileConversationSummaryStore), resolver =>
                        new FileConversationSummaryStore(Path.Combine(_worldStoreRoot, "Summaries"), resolver.Resolve<ILog>(),
                            CoreAiWebGlPersistence.Sync, token => CoreAiWebGlPersistence.SyncAsync(token).AsTask()));
                    required.Add(typeof(FileAgentMemoryStore));
                    required.Add(typeof(FileConversationSummaryStore));
                }
            }
            else if (origin is CoreAiModsLifetimeScope)
            {
                // WHY: the root already contains the GUID; a second Stores/SHA256 namespace can exceed Windows' source-path limit.
                replacements.Add(typeof(FileLuaModStore), resolver =>
                    new FileLuaModStore(rootDirectory: _modStateRoot));
                replacements.Add(typeof(FileLuaModSourceStore), resolver =>
                    new FileLuaModSourceStore(rootDirectory: _modSourceRoot));
                replacements.Add(typeof(FileRbxWorldPackageStore), resolver =>
                    new FileRbxWorldPackageStore(rootDirectory: Path.Combine(_worldStoreRoot, "WorldPackages"), startupNamespace: RunId));
                required.UnionWith(replacements.Keys);
            }
            else
            {
                return;
            }

            HashSet<Type> replaced = ReplaceStorageRegistrations(builder, replacements);
            if (replaced.Contains(typeof(WorldStateManager)))
            {
                builder.RegisterBuildCallback(resolver => Assert.AreEqual(
                    Path.GetFullPath(Path.Combine(_worldStoreRoot, "world_state.json")),
                    resolver.Resolve<WorldStateManager>().SaveFilePath,
                    "The real world snapshot manager must use the owned path before its entry point initializes."));
            }
            foreach (Type type in required)
            {
                Assert.IsTrue(replaced.Contains(type), "The real scene's storage factory was not isolated before any service could resolve: " + type.FullName);
            }
            if (origin is CoreAILifetimeScope)
            {
                _isolationApplied = true;
            }
            else
            {
                _modsStorageIsolated = true;
            }
            Receipt("ISOLATED", origin.GetType().Name + "; exact original builder slots replaced=" + replaced.Count);
        }

        internal static HashSet<Type> ReplaceStorageRegistrations(
            IContainerBuilder builder, IReadOnlyDictionary<Type, Func<IObjectResolver, object>> replacements)
        {
            HashSet<Type> replaced = new();
            int count = builder.Count;
            for (int index = 0; index < count; index++)
            {
                Registration original = builder[index].Build();
                if (replacements.TryGetValue(original.ImplementationType, out Func<IObjectResolver, object> factory))
                {
                    Assert.IsTrue(replaced.Add(original.ImplementationType), "Unexpected duplicate production storage factory: " + original.ImplementationType);
                    builder[index] = new StorageRegistrationBuilder(original, factory);
                }
            }
            Assert.AreEqual(count, builder.Count, "Storage isolation must replace registrations rather than append duplicate singleton types.");
            return replaced;
        }

        private sealed class StorageRegistrationBuilder : RegistrationBuilder
        {
            private readonly Registration _original;
            private readonly Func<IObjectResolver, object> _factory;

            internal StorageRegistrationBuilder(Registration original, Func<IObjectResolver, object> factory)
                : base(original.ImplementationType, original.Lifetime)
            {
                _original = original;
                _factory = factory;
            }

            public override Registration Build()
            {
                return new Registration(_original.ImplementationType, _original.Lifetime,
                    _original.InterfaceTypes, new StorageInstanceProvider(_factory), _original.Key);
            }
        }

        private sealed class StorageInstanceProvider : IInstanceProvider
        {
            private readonly Func<IObjectResolver, object> _factory;

            internal StorageInstanceProvider(Func<IObjectResolver, object> factory)
            {
                _factory = factory;
            }

            public object SpawnInstance(IObjectResolver resolver)
            {
                return _factory(resolver);
            }
        }

        [PostProcessScene(-50)]
        private static void PatchGeneratedMirrorSceneMetadata()
        {
            PublishedDemoLiveSession session = s_loadingSession;
            if (session == null || !EditorApplication.isPlaying || BuildPipeline.isBuildingPlayer)
            {
                return;
            }

            Type identityType = AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetType("Mirror.NetworkIdentity", false)).FirstOrDefault(type => type != null);
            FieldInfo sceneId = identityType?.GetField("sceneId", BindingFlags.Public | BindingFlags.Instance);
            if (sceneId != null)
            {
                string yaml = File.ReadAllText(session._scenePath);
                foreach (Component identity in Resources.FindObjectsOfTypeAll(identityType).Cast<Component>())
                {
                    if (identity.gameObject.scene.path != session._scenePath || (ulong)sceneId.GetValue(identity) != 0)
                    {
                        continue;
                    }
                    MonoScript script = MonoScript.FromMonoBehaviour((MonoBehaviour)identity);
                    string guid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(script));
                    Assert.IsFalse(yaml.Contains("guid: " + guid), "A baked Mirror identity has sceneId zero; repair the authored scene.");
                    sceneId.SetValue(identity, ++s_mirrorSceneId);
                    session.Receipt("COMPATIBILITY", "optional Mirror generated identity received transient scene metadata: " + identity.name);
                }
            }
        }

        private void Capture(string condition, string stackTrace, LogType type)
        {
            if (type == LogType.Error || type == LogType.Assert || type == LogType.Exception)
            {
                _errors.Add(type + ": " + condition + "\n" + stackTrace);
            }
        }

    }

    /// <summary>Registration isolation keeps the original file store and never invokes the replaced default factory.</summary>
    public sealed class PublishedDemoStorageRegistrationPlayModeTests
    {
        [Test]
        public void ExactSlotReplacement_PreservesFileStoreContractAndSingleton_WithoutOpeningDefaultStore()
        {
            string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "CoreAI.LiveQA", Guid.NewGuid().ToString("N")));
            Assert.IsFalse(Directory.Exists(root), "The test may own only a previously absent GUID directory.");
            int defaultFactoryCalls = 0;
            ContainerBuilder builder = new();
            builder.Register<FileLuaModSourceStore>(resolver =>
            {
                defaultFactoryCalls++;
                throw new InvalidOperationException("The original default storage factory must never run.");
            }, Lifetime.Singleton).AsSelf().As<ILuaModSourceStore>();
            Registration original = builder[0].Build();
            Dictionary<Type, Func<IObjectResolver, object>> replacements = new()
            {
                [typeof(FileLuaModSourceStore)] = resolver => new FileLuaModSourceStore(rootDirectory: root)
            };
            HashSet<Type> replaced = PublishedDemoLiveSession.ReplaceStorageRegistrations(builder, replacements);
            Registration isolated = builder[0].Build();
            CollectionAssert.AreEquivalent(original.InterfaceTypes, isolated.InterfaceTypes);
            Assert.AreEqual(original.ImplementationType, isolated.ImplementationType);
            Assert.AreEqual(original.Lifetime, isolated.Lifetime);
            Assert.AreEqual(original.Key, isolated.Key);
            Assert.AreEqual(1, replaced.Count);
            Assert.AreEqual(0, defaultFactoryCalls, "Describing and replacing a factory must not construct the old store.");
            IObjectResolver container = builder.Build();
            try
            {
                ILuaModSourceStore store = container.Resolve<ILuaModSourceStore>();
                Assert.IsInstanceOf<FileLuaModSourceStore>(store);
                Assert.AreSame(store, container.Resolve<FileLuaModSourceStore>());
                Assert.AreSame(store, container.Resolve<ILuaModSourceStore>());
                store.Save("qa_owned", "return 7", new LuaModManifest { Id = "qa_owned" });
                Assert.IsTrue(store.TryLoad("qa_owned", out string source, out LuaModManifest manifest));
                Assert.AreEqual("return 7", source);
                Assert.AreEqual("qa_owned", manifest.Id);
                Assert.IsTrue(Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Any());
                Assert.AreEqual(0, defaultFactoryCalls);
            }
            finally
            {
                container.Dispose();
                string expectedBase = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "CoreAI.LiveQA")) + Path.DirectorySeparatorChar;
                Assert.IsTrue(root.StartsWith(expectedBase, StringComparison.Ordinal));
                if (Directory.Exists(root))
                {
                    Assert.IsFalse((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0,
                        "The test refuses recursive cleanup of a redirected directory.");
                    Directory.Delete(root, true);
                }
            }
        }
    }
}
#endif
