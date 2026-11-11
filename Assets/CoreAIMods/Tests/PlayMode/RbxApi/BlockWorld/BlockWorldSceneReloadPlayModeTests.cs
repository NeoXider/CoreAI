#if UNITY_EDITOR && COREAI_HAS_INPUT_SYSTEM
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Authority;
using CoreAI.Composition;
using CoreAI.Infrastructure.Lua;
using CoreAI.Logging;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances.Terrain;
using CoreAI.Mods.WorldPackages;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using VContainer;
using VContainer.Unity;

namespace CoreAI.Tests.PlayMode.RbxApi.BlockWorld
{
    /// <summary>
    /// A page reload in the editor's terms: the Block World stops (its scene unloads) and starts again over the same
    /// store. What the AI changed comes back: an edit of a bundled mod made through <c>manage_mods reload</c> and a new
    /// mod in the <c>Samples/Block World</c> folder. The twin: a store seeded by an older release meets the bundle, whose
    /// newer version then replaces the edit, and the AI's source stays in the mod's revision history.
    /// </summary>
    public sealed partial class BlockWorldScenePlayModeTests
    {
        private const string EditedMod = "blockworld_hud";
        private const string NewMod = "bw_test_tower";
        private const string AiEditLine = "workspace:SetAttribute('TestAiEditRan', true) -- the AI's edit\n";

        private const string NewModSource = "--[[@coreai\nid: " + NewMod + "\nname: Block World test - Tower\n"
                                            + "version: 1.0.0\ncategory: Samples/Block World\n]]\n"
                                            + "local part = Instance.new('Part') part.Name = 'TestNewModPart' "
                                            + "part.Anchored = true part.Position = Vector3.new(0, 300, 0) "
                                            + "part.Parent = workspace\n";

        /// <summary>The <c>manage_mods</c> tool as the Programmer's model calls it: the shared gate backs the world up first.</summary>
        private LuaModsLlmTool ManageModsTool()
        {
            IObjectResolver mods = _bootstrap.Composition.ModsScope.Container;
            return new LuaModsLlmTool(mods.Resolve<ILuaModRuntime>(), mods.Resolve<ICoreAISettings>(), NullLog.Instance,
                LuaCapabilities.All, true, mods.Resolve<IActorIdentityProvider>(), BuiltInAgentRoleIds.Programmer,
                mods.Resolve<IConfirmedWorldMutationGate>());
        }

        private IEnumerator RunTool(Task<string> call, string what)
        {
            for (int frame = 0; frame < 600 && !call.IsCompleted; frame++)
            {
                yield return null;
            }

            Assert.IsTrue(call.IsCompleted, what + " answers within 600 frames");
            Assert.IsFalse(call.IsFaulted, what + " threw: " + call.Exception?.GetBaseException().Message);
            StringAssert.Contains("\"success\":true", call.Result, what + ": " + call.Result);
        }

        private string ModSource(string id)
        {
            return Session().CurrentConcreteRuntime.TryGetModSource(PlayerActor(), id, out string source) ? source : null;
        }

        private ILuaModSourceStore ModStore()
        {
            return _bootstrap.Composition.ModsScope.Container.Resolve<ILuaModSourceStore>();
        }

        /// <summary>Stops the world as a closed page does (the scene unloads) and opens it again over the same store.</summary>
        private IEnumerator RestartScene()
        {
            foreach (GameObject root in _blockWorld.GetRootGameObjects())
            {
                root.SetActive(false);
            }

            if (_previousActive.IsValid() && _previousActive.isLoaded)
            {
                SceneManager.SetActiveScene(_previousActive);
            }

            yield return SceneManager.UnloadSceneAsync(_blockWorld);
            _blockWorld = default;
            for (int frame = 0; frame < 5; frame++)
            {
                yield return null;
            }

            InputSystem.RemoveDevice(_keyboard);
            InputSystem.RemoveDevice(_mouse);
            yield return OpenScene(null);
        }

        private static IEnumerator WaitWallSeconds(float seconds, List<float> frameMs)
        {
            float until = Time.realtimeSinceStartup + seconds;
            float previous = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup < until)
            {
                yield return null;
                frameMs?.Add((Time.realtimeSinceStartup - previous) * 1000f);
                previous = Time.realtimeSinceStartup;
            }
        }

        private static string Slowest(List<float> frameMs)
        {
            float slowest = 0f;
            foreach (float ms in frameMs)
            {
                slowest = Mathf.Max(slowest, ms);
            }

            return slowest.ToString("0.0") + " ms of " + frameMs.Count + " frames";
        }

        /// <summary>The three largest files of this run's storage root, with their sizes: where a record landed.</summary>
        private string LargestStoredFiles()
        {
            List<System.IO.FileInfo> files = new();
            foreach (string path in System.IO.Directory.GetFiles(_storageRoot, "*", System.IO.SearchOption.AllDirectories))
            {
                files.Add(new System.IO.FileInfo(path));
            }

            files.Sort((a, b) => b.Length.CompareTo(a.Length));
            System.Text.StringBuilder line = new();
            for (int index = 0; index < files.Count && index < 3; index++)
            {
                line.Append(files[index].FullName.Substring(_storageRoot.Length)).Append(' ')
                    .Append((files[index].Length / 1024d).ToString("0.0")).Append(" KB; ");
            }

            return line.ToString();
        }

        private static string BundledSource(string id)
        {
            foreach (BundledMod mod in new ResourcesBundledModSource().Load())
            {
                if (mod.Id == id)
                {
                    return mod.Source;
                }
            }

            return null;
        }

        [UnityTest]
        [Timeout(900000)]
        public IEnumerator BlockWorldScene_AnAiEditAndANewMod_SurviveAStopAndAStart_AndANewerBundleReplacesTheEdit()
        {
            yield return OpenScene(null);
            // WHY 5 s here: the test waits for the automatic record; a kept world's own default is longer.
            System.TimeSpan keptInterval = Session().StartupRefreshInterval;
            Session().StartupRefreshInterval = System.TimeSpan.FromSeconds(5d);
            string bundled = ModSource(EditedMod);
            Assert.IsNotNull(bundled, "the bundled HUD mod runs");
            int headerEnd = bundled.IndexOf("]]\n", System.StringComparison.Ordinal) + 3;
            string edited = bundled.Substring(0, headerEnd) + AiEditLine + bundled.Substring(headerEnd);

            LuaModsLlmTool tool = ManageModsTool();
            yield return RunTool(tool.ExecuteAsync("reload", EditedMod, edited), "the AI's reload of " + EditedMod);
            yield return RunTool(tool.ExecuteAsync("load", NewMod, NewModSource), "the AI's new mod");

            // The player's own blocks, with no AI turn after them: one broken and one placed with the mouse, once the
            // land has streamed to its full radius, so the record that follows is a whole world's.
            yield return WaitUntilStanding();
            yield return WaitForStreamingToSettle();
            RbxVector3 eye = EyePosition();
            yield return Aim(eye, eye + new RbxVector3(8f, -6f, 3f));
            RbxVector3 brokenCell = (RbxVector3)Workspace().GetAttribute("CrosshairCell");
            yield return Click(_mouse.leftButton);
            yield return Frames(3);
            RbxVector3 placedCell = (RbxVector3)Workspace().GetAttribute("CrosshairPlaceCell");
            yield return Tap(_keyboard.digit6Key);
            yield return Click(_mouse.rightButton);
            yield return Frames(3);
            TerrainMaterials.TryGetSlot("Brick", out byte brick);
            byte brokenBefore = Terrain().Store.GetMaterial((int)brokenCell.X, (int)brokenCell.Y, (int)brokenCell.Z);
            byte placedBefore = Terrain().Store.GetMaterial((int)placedCell.X, (int)placedCell.Y, (int)placedCell.Z);
            // WHY eight seconds of wall time: a recorder that waits a few seconds after a change has had its chance.
            List<float> frames = new();
            yield return WaitWallSeconds(8f, frames);
            Record("a kept world's own interval " + keptInterval.TotalSeconds + " s (the test uses 5 s); the record after "
                   + "the player's blocks: " + Session().LastKeptWorldRecord + "; slowest frame of the 8 s "
                   + Slowest(frames) + "; on disk " + LargestStoredFiles());

            ModStore().TryLoad(EditedMod, out _, out LuaModManifest editedManifest);
            Record("before the stop: " + EditedMod + " stored with origin '" + editedManifest?.Origin + "', seeded version "
                   + editedManifest?.SeededVersion + "; " + NewMod + " stored: " + ModStore().TryLoad(NewMod, out _, out _));

            yield return RestartScene();
            yield return WaitUntilStanding();
            byte brokenAfter = Terrain().Store.GetMaterial((int)brokenCell.X, (int)brokenCell.Y, (int)brokenCell.Z);
            byte placedAfter = Terrain().Store.GetMaterial((int)placedCell.X, (int)placedCell.Y, (int)placedCell.Z);
            Record("the player's blocks: broke " + brokenCell + " (slot " + brokenBefore + " before the stop, "
                   + brokenAfter + " after the start), placed Brick at " + placedCell + " (slot " + placedBefore
                   + " before, " + placedAfter + " after; Brick is " + brick + ")");
            Expect(brokenBefore == TerrainMaterials.Air && brokenAfter == TerrainMaterials.Air,
                "a block the player broke is still gone after a stop and a start");
            Expect(placedBefore == brick && placedAfter == brick, "a block the player placed is still there");
            string afterRestart = ModSource(EditedMod);
            ModStore().TryLoad(NewMod, out _, out LuaModManifest newManifest);
            Record("after a stop and a start: " + EditedMod + " is the AI's edit: " + (afterRestart == edited)
                   + ", it ran: " + Workspace().GetAttribute("TestAiEditRan") + "; " + NewMod + " loaded: "
                   + (ModSource(NewMod) != null) + ", its part: " + (Workspace().FindFirstChild("TestNewModPart") != null)
                   + ", folder '"
                   + newManifest?.Category + "'");
            Expect(afterRestart == edited, "the AI's edit of a bundled mod survives a stop and a start");
            Expect(Equals(Workspace().GetAttribute("TestAiEditRan"), true), "and the edited mod runs again");
            Expect(ModSource(NewMod) == NewModSource, "the AI's new mod comes back and runs");
            Expect(newManifest != null && newManifest.Category == "Samples/Block World",
                "in the Samples/Block World folder");
            CoreAI.ChatOverlay.ChatOverlayController overlay = Overlay();
            int chatStart = overlay.Feed.Lines.Count;
            overlay.Submit("/save");
            string answer = "";
            for (int frame = 0; frame < 600 && !answer.Contains("Saved ") && !answer.Contains("not saved"); frame++)
            {
                yield return null;
                answer = Lines(overlay, chatStart);
            }

            Record("/save registered: " + ChatCommandRegistered("save") + "; typed in the chat it answers: '" + answer + "'");
            Expect(ChatCommandRegistered("save") && answer.Contains("Saved "),
                "/save records the kept world and says what it saved");

            // The twin: the store says an older release seeded the HUD, so the bundle's version is newer than it.
            ILuaModSourceStore store = ModStore();
            Assert.IsTrue(store.TryLoad(EditedMod, out string kept, out LuaModManifest manifest) && manifest != null,
                "the HUD is in the store");
            manifest.SeededVersion = "0.0.1";
            store.Save(EditedMod, kept, manifest);
            yield return WaitWallSeconds(3f, null);
            yield return RestartScene();
            string afterBump = ModSource(EditedMod);
            IReadOnlyList<LuaScriptRevision> revisions = Session().CurrentConcreteRuntime.ListModVersions(PlayerActor(), EditedMod);
            bool editKept = false;
            foreach (LuaScriptRevision revision in revisions)
            {
                editKept |= revision.Source == edited;
            }

            Record("a newer bundle over the edit: " + EditedMod + " is the bundled source: "
                   + (afterBump == BundledSource(EditedMod)) + ", the edit ran: " + Workspace().GetAttribute("TestAiEditRan")
                   + "; " + revisions.Count + " revisions, the AI's edit among them: " + editKept);
            Expect(afterBump == BundledSource(EditedMod), "a newer bundled version replaces the AI's edit");
            Expect(Workspace().GetAttribute("TestAiEditRan") == null, "so the edit no longer runs");
            Expect(editKept, "and the AI's source stays in the mod's revision history");
            Report();
        }

        private static System.IDisposable KeepWorldOff()
        {
            // WHY the scope's own options object: it registered it already, and a second registration conflicts.
            return LifetimeScope.Enqueue(builder =>
            {
                if (builder.ApplicationOrigin is CoreAiModsLifetimeScope mods && mods.WorldKeepOptions != null)
                {
                    mods.WorldKeepOptions.KeepWorldBetweenStarts = false;
                }
            });
        }

        /// <summary>The twin: with the play world setting off, nothing of the world is kept, as before it existed.</summary>
        [UnityTest]
        [Timeout(600000)]
        public IEnumerator BlockWorldScene_WithoutKeepingTheWorld_APlayersBlockIsGoneAfterAStopAndAStart_NegativeTwin()
        {
            _installers = KeepWorldOff();
            yield return OpenScene(null);
            yield return WaitUntilStanding();
            RbxVector3 eye = EyePosition();
            yield return Aim(eye, eye + new RbxVector3(8f, -6f, 3f));
            RbxVector3 brokenCell = (RbxVector3)Workspace().GetAttribute("CrosshairCell");
            yield return Click(_mouse.leftButton);
            yield return Frames(3);
            byte brokenBefore = Terrain().Store.GetMaterial((int)brokenCell.X, (int)brokenCell.Y, (int)brokenCell.Z);
            yield return WaitWallSeconds(8f, null);
            bool keptBefore = Session().KeepsWorldBetweenStarts;

            _installers = KeepWorldOff();
            yield return RestartScene();
            yield return WaitUntilStanding();
            byte brokenAfter = Terrain().Store.GetMaterial((int)brokenCell.X, (int)brokenCell.Y, (int)brokenCell.Z);
            Record("with the setting off: kept " + keptBefore + ", broke " + brokenCell + " (slot " + brokenBefore
                   + "), after a stop and a start slot " + brokenAfter + "; /save registered: "
                   + ChatCommandRegistered("save"));
            Expect(!keptBefore, "the setting is off");
            Expect(brokenBefore == TerrainMaterials.Air && brokenAfter != TerrainMaterials.Air,
                "the broken block is back from the seed: nothing of the world was kept");
            Expect(!ChatCommandRegistered("save"), "and there is no /save");
            Report();
        }

        private bool ChatCommandRegistered(string name)
        {
            CoreAI.ChatOverlay.IChatCommandRegistry registry = _bootstrap.Composition.CoreScope.Container
                .ResolveOrDefault<CoreAI.ChatOverlay.IChatCommandRegistry>();
            return registry != null && registry.TryResolve(name, out _);
        }
    }
}
#endif
