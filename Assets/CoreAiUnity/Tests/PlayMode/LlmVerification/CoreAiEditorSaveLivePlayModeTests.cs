#if UNITY_EDITOR && COREAI_LLM && COREAI_LUA
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using CoreAI.Ai;
using CoreAI.Messaging;
using CoreAI.Mods.Rbx.Instances;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace CoreAI.Tests.PlayMode
{
    /// <summary>Two real model turns author and edit an owned persistent Lua mod in the published FullAccess scene.</summary>
    [Explicit("Requires explicit COREAI_TEST_BASE_URL / COREAI_TEST_MODEL and real tool-capable HTTP completions.")]
    [Category("LiveLlm")]
    [Timeout(TestTimeoutMs)]
    public sealed class CoreAiEditorSaveLivePlayModeTests
    {
        private const int TestTimeoutMs = 360_000;
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
        public IEnumerator ActualProgrammer_ModelCreatesThenEditsOwnedMod_WithSavedSourceAndObservableLuaEffects()
        {
            yield return _session.Open("Assets/CoreAI.Demos/FullAccess/FullAccessDemo.unity");
            string id = _session.NewModId("authored");
            string folderName = _session.RunId + "_folder";
            Assert.IsNull(_session.Workspace.FindFirstChild(folderName));
            string[] tools = { "manage_mods", "read_skill" };
            int createToolBaseline = CoreAi.GetToolCallHistorySnapshot()?.Count ?? 0;

            yield return _session.Turn(BuiltInAgentRoleIds.Programmer,
                "Author and LOAD one new persistent Lua mod with the exact id '" + id + "'. " +
                "Call manage_mods with action='load', mod_id='" + id + "', and code containing the complete Lua source you write yourself. " +
                "All three arguments must be present in the same structured tool call; action alone cannot load a mod. " +
                "Its harmless startup behavior must create exactly one Folder under workspace named '" + folderName + "' " +
                "and set that Folder's Stage attribute to the string 'created'. " +
                "Only use manage_mods and read_skill. If documentation is needed, call read_skill with the exact " +
                "catalog skill_name 'Rbx API' or 'Lua Modding', all=true, and section omitted. " +
                "Markdown headings are not section identifiers. Do not inspect the world or call verification tools: " +
                "the host checks the saved source and runtime effect independently. " +
                "Do not change any other mod, rule, object, world, or setting. No timers or event handlers. " +
                "After manage_mods load succeeds, stop tool use and finish with one short visible confirmation.",
                120f, 1400, tools);
            AssertHasSuccessfulTool(_session.LastCompletion, "manage_mods", "create");
            AssertCompletedManageActions(createToolBaseline, id, "load");
            _session.AssertModHealthy(id);
            Assert.IsTrue(_session.Runtime.TryGetModSource(_session.Actor, id, out string createdSource));
            Assert.IsFalse(string.IsNullOrWhiteSpace(createdSource));
            RbxInstance first = _session.Workspace.FindFirstChild(folderName);
            Assert.IsNotNull(first, "The model-authored source must actually execute in the scene's Lua VM.");
            Assert.AreEqual("created", first.GetAttribute("Stage"));
            AssertSavedSource(id, createdSource);
            IReadOnlyList<LuaScriptRevision> createdVersions = _session.Runtime.ListModVersions(_session.Actor, id);
            int initialRevisionCount = createdVersions.Count;
            Assert.Greater(initialRevisionCount, 0, "The real load must record the first source revision.");
            _session.Receipt("MODEL_CREATE", "owned mod loaded; saved source; Stage=created; revisions=" + initialRevisionCount);

            int editToolBaseline = CoreAi.GetToolCallHistorySnapshot()?.Count ?? 0;
            yield return _session.Turn(BuiltInAgentRoleIds.Programmer,
                "Edit the existing persistent Lua mod '" + id + "'. " +
                "First call manage_mods with action='get_source' and mod_id='" + id + "'. Then call manage_mods " +
                "with action='reload', mod_id='" + id + "', and code containing your complete replacement Lua source " +
                "to replace its source, preserving the mod id and granted capabilities. " +
                "Change its startup behavior so there is exactly one Folder named '" + folderName + "' under workspace " +
                "whose Stage attribute is now the string 'edited'. " +
                "Use the normal clean-startup-objects reload mode. The host independently checks the Folder and saved revision. " +
                "Only use manage_mods and read_skill; do not query the world or call other verification tools. " +
                "If documentation is needed, read the whole 'Rbx API' or 'Lua Modding' catalog skill with all=true and no section. " +
                "Do not pass a Markdown heading as a section. " +
                "Do not change another mod, rule, object, world, or setting. No timers or event handlers. " +
                "After manage_mods reload succeeds, stop tool use and finish with one short visible confirmation.",
                120f, 1400, tools);
            AssertHasSuccessfulTool(_session.LastCompletion, "manage_mods", "edit");
            AssertCompletedManageActions(editToolBaseline, id, "get_source", "reload");
            _session.AssertModHealthy(id);
            Assert.IsTrue(_session.Runtime.TryGetModSource(_session.Actor, id, out string editedSource));
            Assert.AreNotEqual(createdSource, editedSource, "The second turn must replace actual Lua source.");
            RbxInstance edited = _session.Workspace.FindFirstChild(folderName);
            Assert.IsNotNull(edited);
            Assert.AreEqual("edited", edited.GetAttribute("Stage"), "The edited Lua must execute, not merely be promised in prose.");
            Assert.AreEqual(1, _session.Workspace.GetChildren().Count(child => child.Name == folderName),
                "Reload must leave one owned object rather than duplicate startup objects.");
            AssertSavedSource(id, editedSource);
            IReadOnlyList<LuaScriptRevision> editedVersions = _session.Runtime.ListModVersions(_session.Actor, id);
            Assert.Greater(editedVersions.Count, initialRevisionCount, "The model edit must append a real saved source revision.");
            Assert.IsTrue(editedVersions.Any(revision => revision.Source == createdSource), "The first source must remain in revision history.");
            Assert.IsTrue(editedVersions.Any(revision => revision.Source == editedSource), "The edited source must appear in revision history.");
            _session.Receipt("PASS", "two actual Programmer turns created and edited the same persistent mod; Lua Stage created to edited; version history retained");
        }

        private void AssertSavedSource(string id, string expected)
        {
            ILuaModSourceStore store = (ILuaModSourceStore)_session.ModsContainer.Resolve(typeof(ILuaModSourceStore));
            Assert.IsTrue(store.TryLoad(id, out string persisted, out LuaModManifest manifest),
                "A live load/reload must save the owned source through the production mod source store.");
            Assert.AreEqual(expected, persisted);
            Assert.AreEqual(id, manifest.Id);
            Assert.IsTrue(manifest.Active);
        }

        private static void AssertHasSuccessfulTool(LlmCompletionResult result, string name, string phase)
        {
            PublishedDemoLiveSession.AssertCompletion(result);
            Assert.IsTrue(result.ExecutedToolCalls?.Any(call => call.Name == name && call.Success) == true,
                "The real model must invoke " + name + " during " + phase + "; code in assistant text is insufficient.");
        }

        private void AssertCompletedManageActions(int baseline, string id, params string[] expectedActions)
        {
            IReadOnlyList<LlmToolCallRecord> history = CoreAi.GetToolCallHistorySnapshot();
            Assert.IsNotNull(history);
            Assert.GreaterOrEqual(history.Count, baseline, "The tool receipt window was lost; prior calls cannot prove this turn.");
            List<string> actions = new();
            foreach (LlmToolCallRecord record in history.Skip(baseline))
            {
                if (record == null || record.Status != "completed" ||
                    record.Info.TraceId != _session.RunId || record.Info.ToolName != "manage_mods")
                {
                    continue;
                }

                Assert.IsFalse(string.IsNullOrWhiteSpace(record.Info.ArgumentsJson),
                    "The actual manage_mods receipt must retain its target/action for this acceptance gate.");
                JObject arguments = JObject.Parse(record.Info.ArgumentsJson);
                if ((string)arguments["mod_id"] == id)
                {
                    actions.Add((string)arguments["action"]);
                }
            }
            foreach (string action in expectedActions)
            {
                CollectionAssert.Contains(actions, action, "Missing actual successful manage_mods action for the owned mod: " + action);
            }
        }
    }
}
#endif
