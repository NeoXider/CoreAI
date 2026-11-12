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
        private const string FragileMod = "bw_test_fragile";

        // WHY counters: the world keeps the Workspace's attributes, so only a count that grows says a mod ran again.
        private const string AiEditLine = "workspace:SetAttribute('TestAiEditRuns', (workspace:GetAttribute('TestAiEditRuns') "
                                          + "or 0) + 1) -- the AI's edit\n";

        private const string NewModSource = "--[[@coreai\nid: " + NewMod + "\nname: Block World test - Tower\n"
                                            + "version: 1.0.0\ncategory: Samples/Block World\n]]\n"
                                            + "workspace:SetAttribute('TestNewModRuns', "
                                            + "(workspace:GetAttribute('TestNewModRuns') or 0) + 1) "
                                            + "local part = Instance.new('Part') part.Name = 'TestNewModPart' "
                                            + "part.Anchored = true part.Position = Vector3.new(0, 300, 0) "
                                            + "part.Parent = workspace\n";

        /// <summary>An AI-written mod that breaks when it starts a second time: the restore keeps going without it.</summary>
        private const string FragileModSource = "--[[@coreai\nid: " + FragileMod + "\nname: Block World test - Fragile\n"
                                                + "version: 1.0.0\ncategory: Samples/Block World\n]]\n"
                                                + "local runs = (workspace:GetAttribute('TestFragileRuns') or 0) + 1 "
                                                + "workspace:SetAttribute('TestFragileRuns', runs) "
                                                + "if runs > 1 then error('this mod breaks after a restart') end\n";

        private double Runs(string attribute)
        {
            return System.Convert.ToDouble(Workspace().GetAttribute(attribute) ?? 0d);
        }

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

        /// <summary>What the crosshair rested on while <see cref="AimAtLandBeyond"/> waited for the land; empty when it did not wait.</summary>
        private string _crosshairWait = "";

        /// <summary>
        /// Waits until the crosshair rests on the land at a cell other than <paramref name="notCell"/>, looking a little
        /// to the side after a second of waiting (something standing in the line of sight); the crosshair's own mod
        /// decides, so a crosshair that never updates still fails the test.
        /// </summary>
        private IEnumerator AimAtLandBeyond(RbxVector3 eye, RbxVector3 look, RbxVector3 notCell)
        {
            _crosshairWait = "";
            RbxVector3[] looks = { look, look + new RbxVector3(0f, 0f, 2.5f), look + new RbxVector3(0f, 0f, -2.5f) };
            for (int attempt = 0; attempt < looks.Length; attempt++)
            {
                if (attempt > 0)
                {
                    yield return Aim(eye, eye + looks[attempt]);
                }

                for (int frame = 0; frame < 60; frame++)
                {
                    object cell = Workspace().GetAttribute("CrosshairCell");
                    if (Equals(Workspace().GetAttribute("CrosshairHasTarget"), true) && cell is RbxVector3 onCell
                        && (onCell.X != notCell.X || onCell.Y != notCell.Y || onCell.Z != notCell.Z))
                    {
                        yield break;
                    }

                    if (frame == 0)
                    {
                        _crosshairWait += "[look " + attempt + ": on terrain " + Workspace().GetAttribute("CrosshairHasTarget")
                                          + ", part '" + Workspace().GetAttribute("CrosshairPartName") + "', model '"
                                          + Workspace().GetAttribute("CrosshairModelName") + "'] ";
                    }

                    yield return null;
                }
            }
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
            string timing = Session().StartupRefreshDebounce.TotalSeconds + " s after the last change, at most "
                            + Session().StartupRefreshInterval.TotalSeconds + " s after the first";
            string bundled = ModSource(EditedMod);
            Assert.IsNotNull(bundled, "the bundled HUD mod runs");
            int headerEnd = bundled.IndexOf("]]\n", System.StringComparison.Ordinal) + 3;
            string edited = bundled.Substring(0, headerEnd) + AiEditLine + bundled.Substring(headerEnd);

            LuaModsLlmTool tool = ManageModsTool();
            yield return RunTool(tool.ExecuteAsync("reload", EditedMod, edited), "the AI's reload of " + EditedMod);
            yield return RunTool(tool.ExecuteAsync("load", NewMod, NewModSource), "the AI's new mod");
            yield return RunTool(tool.ExecuteAsync("load", FragileMod, FragileModSource), "the AI's fragile mod");
            double editRuns = Runs("TestAiEditRuns");
            double newModRuns = Runs("TestNewModRuns");

            // The player's own blocks, with no AI turn after them: one broken and one placed with the mouse, once the
            // land has streamed to its full radius, so the record that follows is a whole world's.
            yield return WaitUntilStanding();
            yield return WaitForStreamingToSettle();
            RbxVector3 eye = EyePosition();
            yield return Aim(eye, eye + new RbxVector3(8f, -6f, 3f));
            RbxVector3 brokenCell = (RbxVector3)Workspace().GetAttribute("CrosshairCell");
            yield return Click(_mouse.leftButton);
            yield return Frames(3);
            // WHY the crosshair is waited for: a right click places a block only where the crosshair rests on the land,
            // and the line of sight past the broken block can be crossed by a villager (or reach nothing within reach),
            // which left the click placing nothing and the stale place cell read as the player's block.
            yield return AimAtLandBeyond(eye, new RbxVector3(8f, -6f, 3f), brokenCell);
            RbxVector3 placedCell = (RbxVector3)Workspace().GetAttribute("CrosshairPlaceCell");
            yield return Tap(_keyboard.digit6Key);
            yield return Click(_mouse.rightButton);
            yield return Frames(3);
            // And a torch on the floor a little aside: a mod's Part, which the world keeps through the torch list.
            yield return Aim(eye, eye + new RbxVector3(-6f, -6f, 6f));
            RbxVector3 torchCell = (RbxVector3)Workspace().GetAttribute("CrosshairPlaceCell");
            yield return Tap(_keyboard.digit9Key);
            yield return Click(_mouse.rightButton);
            yield return Frames(3);
            string torchName = "Torch_" + (int)torchCell.X + "_" + (int)torchCell.Y + "_" + (int)torchCell.Z;
            bool torchBefore = Workspace().FindFirstChild("BlockWorldTorches")?.FindFirstChild(torchName) != null;
            yield return Tap(_keyboard.digit1Key);
            TerrainMaterials.TryGetSlot("Brick", out byte brick);
            byte brokenBefore = Terrain().Store.GetMaterial((int)brokenCell.X, (int)brokenCell.Y, (int)brokenCell.Z);
            byte placedBefore = Terrain().Store.GetMaterial((int)placedCell.X, (int)placedCell.Y, (int)placedCell.Z);
            // WHY eight seconds of wall time: a recorder that waits a few seconds after a change has had its chance.
            List<float> frames = new();
            string recordBefore = Session().LastKeptWorldRecord;
            float lastChange = Time.realtimeSinceStartup;
            float recordedAfter = -1f;
            float waitUntil = lastChange + 8f;
            float previousFrame = lastChange;
            while (Time.realtimeSinceStartup < waitUntil)
            {
                yield return null;
                frames.Add((Time.realtimeSinceStartup - previousFrame) * 1000f);
                previousFrame = Time.realtimeSinceStartup;
                if (recordedAfter < 0f && !string.Equals(Session().LastKeptWorldRecord, recordBefore, System.StringComparison.Ordinal))
                {
                    recordedAfter = Time.realtimeSinceStartup - lastChange;
                }
            }

            Record("a kept world records " + timing + "; recorded " + recordedAfter.ToString("0.00")
                   + " s of wall time after the last change; the record after the player's blocks: "
                   + Session().LastKeptWorldRecord + "; slowest frame of the 8 s " + Slowest(frames) + "; on disk "
                   + LargestStoredFiles());
            float quiet = (float)RbxWorldRuntimeSessionController.KeptWorldQuietTime.TotalSeconds;
            Expect(recordedAfter >= quiet - 0.5f && recordedAfter < quiet + 3f,
                "a kept world is recorded a few seconds after the player stops");

            ModStore().TryLoad(EditedMod, out _, out LuaModManifest editedManifest);
            Record("before the stop: " + EditedMod + " stored with origin '" + editedManifest?.Origin + "', seeded version "
                   + editedManifest?.SeededVersion + "; " + NewMod + " stored: " + ModStore().TryLoad(NewMod, out _, out _));

            yield return RestartScene();
            yield return WaitUntilStanding();
            byte brokenAfter = Terrain().Store.GetMaterial((int)brokenCell.X, (int)brokenCell.Y, (int)brokenCell.Z);
            byte placedAfter = Terrain().Store.GetMaterial((int)placedCell.X, (int)placedCell.Y, (int)placedCell.Z);
            Record("the player's blocks: broke " + brokenCell + " (slot " + brokenBefore + " before the stop, "
                   + brokenAfter + " after the start), placed Brick at " + placedCell + " (slot " + placedBefore
                   + " before, " + placedAfter + " after; Brick is " + brick + "); the crosshair after the break "
                   + (_crosshairWait.Length == 0 ? "rested on the land at once" : "waited for " + _crosshairWait));
            Expect(brokenBefore == TerrainMaterials.Air && brokenAfter == TerrainMaterials.Air,
                "a block the player broke is still gone after a stop and a start");
            Expect(placedBefore == brick && placedAfter == brick, "a block the player placed is still there");
            bool torchAfter = Workspace().FindFirstChild("BlockWorldTorches")?.FindFirstChild(torchName) != null;
            Record("the torch " + torchName + ": before the stop " + torchBefore + ", after the start " + torchAfter
                   + "; the fragile mod loaded after the start: " + (ModSource(FragileMod) != null));
            Expect(torchBefore && torchAfter, "a torch the player placed is still there");
            Expect(ModSource(FragileMod) == null, "the mod that breaks at the start stays stopped");
            string afterRestart = ModSource(EditedMod);
            ModStore().TryLoad(NewMod, out _, out LuaModManifest newManifest);
            Record("after a stop and a start: " + EditedMod + " is the AI's edit: " + (afterRestart == edited)
                   + ", its runs " + editRuns + " -> " + Runs("TestAiEditRuns") + "; " + NewMod + " loaded: "
                   + (ModSource(NewMod) != null) + ", its runs " + newModRuns + " -> " + Runs("TestNewModRuns")
                   + ", its part: " + (Workspace().FindFirstChild("TestNewModPart") != null)
                   + ", folder '"
                   + newManifest?.Category + "'");
            Expect(afterRestart == edited, "the AI's edit of a bundled mod survives a stop and a start");
            Expect(Runs("TestAiEditRuns") > editRuns, "and the edited mod runs again");
            Expect(ModSource(NewMod) == NewModSource && Runs("TestNewModRuns") > newModRuns,
                "the AI's new mod comes back and writes the Workspace again, with the rights it had");
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
            double editRunsBeforeBump = Runs("TestAiEditRuns");
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
                   + (afterBump == BundledSource(EditedMod)) + ", the edit's runs " + editRunsBeforeBump + " -> " + Runs("TestAiEditRuns")
                   + "; " + revisions.Count + " revisions, the AI's edit among them: " + editKept);
            Expect(afterBump == BundledSource(EditedMod), "a newer bundled version replaces the AI's edit");
            Expect(Runs("TestAiEditRuns") == editRunsBeforeBump, "so the edit no longer runs");
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
