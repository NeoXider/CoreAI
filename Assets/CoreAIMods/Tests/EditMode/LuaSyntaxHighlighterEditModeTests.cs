#if COREAI_HAS_HUB
using System;
using System.Collections.Generic;
using CoreAI.Ai;
using CoreAI.Ai.Hub;
using NUnit.Framework;
using UnityEngine.UIElements;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Pure markup assertions for the Hub mod editor's <see cref="LuaSyntaxHighlighter"/>: keywords,
    /// comments, strings and numbers get wrapped in color tags, hostile '&lt;' input can never form a
    /// real rich-text tag, and null/empty input yields "".
    /// </summary>
    public sealed class LuaSyntaxHighlighterEditModeTests
    {
        [Test]
        public void Highlight_Keyword_IsWrappedInColorTag()
        {
            string result = LuaSyntaxHighlighter.Highlight("local x = y");

            StringAssert.Contains("<color=#61AFEF>local</color>", result);
        }

        [Test]
        public void Highlight_LineComment_IsWrappedInColorTag()
        {
            string result = LuaSyntaxHighlighter.Highlight("--comment\nprint(1)");

            StringAssert.Contains("<color=#8A929E>--comment</color>", result);
        }

        [Test]
        public void Highlight_StringLiteral_IsWrappedInColorTag()
        {
            string doubleQuoted = LuaSyntaxHighlighter.Highlight("local s = \"hello\"");
            string singleQuoted = LuaSyntaxHighlighter.Highlight("local s = 'hi'");

            StringAssert.Contains("<color=#98C379>\"hello\"</color>", doubleQuoted);
            StringAssert.Contains("<color=#98C379>'hi'</color>", singleQuoted);
        }

        [Test]
        public void Highlight_NumberLiteral_IsWrappedInColorTag()
        {
            string result = LuaSyntaxHighlighter.Highlight("local n = 42");

            StringAssert.Contains("<color=#D19A66>42</color>", result);
        }

        [Test]
        public void Highlight_LessThanInSource_CannotFormAnInjectedTag()
        {
            string result = LuaSyntaxHighlighter.Highlight("local s = \"<color=red>evil</color>\"");

            // WHY: every '<' from user source must be followed by a zero-width space, so the literal
            // "<color"/"</color" runs can never be parsed as real rich-text tags; only the highlighter's
            // own "<color=#RRGGBB>" tags may survive intact.
            StringAssert.DoesNotContain("<color=red", result);
            StringAssert.Contains("<​color=red", result);
        }

        [Test]
        public void Highlight_ComparisonOperator_KeepsSourceCharactersVisible()
        {
            string result = LuaSyntaxHighlighter.Highlight("if a < b then end");

            // WHY: escaping only inserts invisible zero-width spaces — stripping them back out must
            // reproduce the visible source (minus the highlighter's own tags), so nothing the user typed
            // is lost or mangled on screen.
            string withoutMarkup = System.Text.RegularExpressions.Regex
                .Replace(result, "</?color[^>]*>", "")
                .Replace("​", "");
            Assert.AreEqual("if a < b then end", withoutMarkup);
        }

        [Test]
        public void Highlight_NullOrEmpty_ReturnsEmptyString()
        {
            Assert.AreEqual(string.Empty, LuaSyntaxHighlighter.Highlight(null));
            Assert.AreEqual(string.Empty, LuaSyntaxHighlighter.Highlight(""));
        }

        [Test]
        public void Highlight_IsDeterministic()
        {
            const string source = "-- tick\nlocal t = 1.5\nhooks_every(t, function() end)";

            Assert.AreEqual(LuaSyntaxHighlighter.Highlight(source), LuaSyntaxHighlighter.Highlight(source));
        }
    }

    /// <summary>
    /// The Hub Mods and Logs pages rebuild their lists from runtime events. Those events come one per
    /// mod change, handler error or report, hundreds in a frame from a noisy mod, and may arrive off the
    /// main thread; the pages must hand them to the panel and rebuild once per panel update, not once
    /// per event on the raising thread.
    /// </summary>
    public sealed class HubModPagesRefreshEditModeTests
    {
        private sealed class CountingHubModService : IHubModService
        {
            public int ListModsCalls;
            public int ErrorEntryReads;
            public int ReportReads;

            public bool IsSupported => true;

            public event Action ModsChanged;

            public event Action LogsChanged;

            public void RaiseModsChanged()
            {
                ModsChanged?.Invoke();
            }

            public void RaiseLogsChanged()
            {
                LogsChanged?.Invoke();
            }

            public IReadOnlyList<HubModRecord> ListMods()
            {
                ListModsCalls++;
                return new[] { new HubModRecord { Id = "noisy", Name = "Noisy", IsLoaded = true } };
            }

            public bool TryGetSource(string id, out string source)
            {
                source = "";
                return false;
            }

            public bool IsLoaded(string id)
            {
                return false;
            }

            public void SaveOrReload(string id, string code)
            {
            }

            public List<ModReloadMode> SaveModes { get; } = new();

            public HubModSaveResult SaveOrReload(string id, string code, ModReloadMode mode)
            {
                SaveModes.Add(mode);
                return new HubModSaveResult(id, true, mode, mode == ModReloadMode.KeepObjects
                    ? new ModReloadReport(id, mode, 0, 126, 0)
                    : new ModReloadReport(id, mode, 126, 0, 0));
            }

            public string NewModTemplate => HubModTemplates.Legacy;

            public void Enable(string id)
            {
            }

            public void Disable(string id)
            {
            }

            public bool Delete(string id)
            {
                return false;
            }

            public IReadOnlyList<LuaScriptRevision> ListModVersions(string id)
            {
                return Array.Empty<LuaScriptRevision>();
            }

            public bool TryRevertMod(string id, int revisionIndex, out string restoredSource)
            {
                restoredSource = null;
                return false;
            }

            public string ExportMod(string id)
            {
                return null;
            }

            public bool ImportMod(string bundleJson)
            {
                return false;
            }

            public bool ApplyBundledUpdate(string id)
            {
                return false;
            }

            public string RecentErrors(string id)
            {
                return "";
            }

            public IReadOnlyList<LuaModHandlerError> RecentErrorEntries(string modId = null)
            {
                ErrorEntryReads++;
                return new[]
                {
                    new LuaModHandlerError
                    {
                        ModId = "noisy", Error = "boom", ConsecutiveCount = 1, AtUtc = DateTime.UtcNow
                    }
                };
            }

            public IReadOnlyList<LuaModReport> RecentReports(string modId = null)
            {
                ReportReads++;
                return new[] { new LuaModReport { ModId = "noisy", Message = "tick", AtUtc = DateTime.UtcNow } };
            }

            public void ClearReports()
            {
            }

            public void ClearErrors()
            {
            }

            public bool GetReportLoggingEnabled(string modId)
            {
                return true;
            }

            public bool SetReportLoggingEnabled(string modId, bool enabled)
            {
                return true;
            }
        }

        /// <summary>
        /// A mod with 500 erroring Heartbeat handlers raises 500 LogsChanged in one frame; the open Logs
        /// tab queued a full rebuild of every kept line for each of them.
        /// </summary>
        [Test]
        public void LogsPage_ManyLogEventsBeforeThePanelUpdates_QueueOneRebuild()
        {
            CountingHubModService service = new();
            List<Action> queued = new();
            HubModLogsPage page = new(service, 350, queued.Add);
            Assert.IsNotNull(page.CreatePageContent(), "The Logs page must build its content.");
            int errorReadsAfterBuild = service.ErrorEntryReads;
            int reportReadsAfterBuild = service.ReportReads;

            for (int i = 0; i < 500; i++)
            {
                service.RaiseLogsChanged();
            }

            Assert.AreEqual(1, queued.Count,
                "500 log events before the panel updates must queue one list rebuild, not one per event.");
            Assert.AreEqual(errorReadsAfterBuild, service.ErrorEntryReads,
                "A log event must not rebuild the list on the thread that raised it.");

            queued[0]();

            Assert.AreEqual(errorReadsAfterBuild + 1, service.ErrorEntryReads,
                "The queued rebuild must read the errors once.");
            Assert.AreEqual(reportReadsAfterBuild + 1, service.ReportReads,
                "The queued rebuild must read the reports once.");

            service.RaiseLogsChanged();

            Assert.AreEqual(2, queued.Count,
                "A log event after the queued rebuild ran must queue the next one, or the page goes stale.");
        }

        /// <summary>
        /// A rehydrate of N stored mods raises N ModsChanged; the visible Mods list re-read every mod and
        /// rebuilt the whole tree for each, synchronously on the raising thread.
        /// </summary>
        [Test]
        public void ModsPage_ManyModChangesBeforeThePanelUpdates_ListTheModsOnce()
        {
            CountingHubModService service = new();
            List<Action> queued = new();
            HubModsPage page = new(service, 300, queued.Add);
            Assert.IsNotNull(page.CreatePageContent(), "The Mods page must build its content.");
            int listCallsAfterBuild = service.ListModsCalls;

            for (int i = 0; i < 30; i++)
            {
                service.RaiseModsChanged();
            }

            Assert.AreEqual(listCallsAfterBuild, service.ListModsCalls,
                "A mod change must not rebuild the list on the thread that raised it.");
            Assert.AreEqual(1, queued.Count,
                "30 mod changes before the panel updates must queue one list rebuild, not one per change.");

            queued[0]();

            Assert.AreEqual(listCallsAfterBuild + 1, service.ListModsCalls,
                "The queued rebuild must list the mods once.");

            service.RaiseModsChanged();

            Assert.AreEqual(2, queued.Count,
                "A mod change after the queued rebuild ran must queue the next one, or the list goes stale.");
        }

        private sealed class MemoryEditorPreferences : IHubModEditorPreferences
        {
            public bool KeepObjectsOnSave { get; set; }
        }

        /// <summary>
        /// The mod editor's "Keep objects on Save &amp; run" toggle starts from the Hub-wide preference,
        /// off by default, Save &amp; run reloads in the mode it shows, and the status line says what the
        /// reload did with the previous run's objects.
        /// </summary>
        [Test]
        public void ModEditor_KeepObjectsToggle_DefaultsOff_PicksTheReloadMode_AndTheStatusSaysWhatWasCleaned()
        {
            CountingHubModService service = new();
            MemoryEditorPreferences preferences = new();
            HubModEditorPage editor = new(service, "castle", "local x = 1", null, preferences);
            UnityEngine.UIElements.VisualElement root = editor.Build();
            UnityEngine.UIElements.Toggle toggle =
                root.Q<UnityEngine.UIElements.Toggle>(HubModEditorPage.KeepObjectsToggleName);
            UnityEngine.UIElements.Label status =
                root.Q<UnityEngine.UIElements.Label>(HubModEditorPage.StatusLabelName);

            Assert.IsNotNull(toggle, "the editor shows the toggle");
            Assert.IsFalse(toggle.value, "keeping objects is off by default");

            editor.Save();

            CollectionAssert.AreEqual(new[] { ModReloadMode.CleanStartupObjects }, service.SaveModes);
            Assert.AreEqual("Saved & ran 'castle'; cleaned 126 objects of the previous run.", status.text);

            toggle.value = true;
            editor.Save();

            Assert.AreEqual(ModReloadMode.KeepObjects, service.SaveModes[1], "the toggle picks keep mode");
            StringAssert.Contains("kept 126 objects", status.text);
        }

        [Test]
        public void ModEditor_KeepObjectsToggle_IsOneHubPreference_ReadByEveryEditorAndWrittenOnChange()
        {
            CountingHubModService service = new();
            MemoryEditorPreferences preferences = new();
            HubModEditorPage first = new(service, "first", "local x = 1", null, preferences);
            first.Build();

            first.OnKeepObjectsToggled(true);

            Assert.IsTrue(preferences.KeepObjectsOnSave, "changing the toggle writes the Hub preference");
            HubModEditorPage second = new(service, "second", "local x = 2", null, preferences);
            UnityEngine.UIElements.Toggle toggle = second.Build()
                .Q<UnityEngine.UIElements.Toggle>(HubModEditorPage.KeepObjectsToggleName);
            Assert.IsTrue(toggle.value, "an editor for another mod opens with the same preference");

            second.Save();

            Assert.AreEqual(ModReloadMode.KeepObjects, service.SaveModes[0]);
        }

        [Test]
        public void ModsPage_ASuspendedMod_SaysWhyItIsOffAndHowToStartIt()
        {
            Assert.AreEqual("  suspended after repeated budget trips — start it manually",
                HubModsPage.SuspensionNote(new HubModRecord { Id = "spinner", SuspendedAfterBudgetTrips = true }));
            Assert.AreEqual("", HubModsPage.SuspensionNote(new HubModRecord { Id = "plain" }),
                "a mod that was not suspended gets no note");
        }

        [Test]
        public void ModsPage_ChangeBeforeTheContentIsBuilt_QueuesNothing()
        {
            CountingHubModService service = new();
            List<Action> queued = new();
            HubModsPage page = new(service, 300, queued.Add);
            page.OnActivated();

            service.RaiseModsChanged();

            Assert.AreEqual(0, queued.Count, "A page without content has no list to rebuild.");
            Assert.AreEqual(0, service.ListModsCalls, "A page without content must not list the mods.");
            page.OnDestroyed();
        }
    }
}
#endif
