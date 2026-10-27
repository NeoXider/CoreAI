#if COREAI_HAS_HUB
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using CoreAI.Ai;
using CoreAI.Ai.Hub;
using CoreAI.CodeEditing;
using CoreAI.Infrastructure.Lua;
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
        private class CountingHubModService : IHubModService
        {
            public int ListModsCalls;
            public int ErrorEntryReads;
            public int ReportReads;
            public Dictionary<string, string> Sources { get; } = new(StringComparer.Ordinal);
            public HubModPersistenceStatus SavePersistenceStatus = HubModPersistenceStatus.Saved;
            public bool MutateBeforeSaveFailure;
            public Exception SaveFailure;
            public bool ThrowOnRead;
            public bool DeleteResult;
            public int DeleteCalls;
            public Exception DeleteFailure;

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
                if (ThrowOnRead)
                {
                    throw new InvalidOperationException("read refused");
                }

                return Sources.TryGetValue(id, out source);
            }

            public bool IsLoaded(string id)
            {
                return false;
            }

            public void SaveOrReload(string id, string code)
            {
            }

            public List<ModReloadMode> SaveModes { get; } = new();

            public virtual HubModSaveResult SaveOrReload(string id, string code, ModReloadMode mode)
            {
                if (SaveFailure != null)
                {
                    if (MutateBeforeSaveFailure)
                    {
                        Sources[id] = code;
                    }

                    throw SaveFailure;
                }

                SaveModes.Add(mode);
                Sources[id] = code;
                return new HubModSaveResult(id, true, mode, mode == ModReloadMode.KeepObjects
                    ? new ModReloadReport(id, mode, 0, 126, 0)
                    : new ModReloadReport(id, mode, 126, 0, 0), SavePersistenceStatus, "storage refused");
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
                DeleteCalls++;
                if (DeleteFailure != null)
                {
                    throw DeleteFailure;
                }

                return DeleteResult;
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

        private sealed class BudgetedCountingHubModService : CountingHubModService, IBudgetedHubModService
        {
            private InvocationGuard _activeInvocation;
            public int InvocationCount { get; private set; }
            public int DisposedInvocationCount { get; private set; }
            public int ActiveInvocationCount => _activeInvocation == null ? 0 : 1;
            public LuaModInvocationLimits LastInvocationLimits { get; private set; }

            public IDisposable BeginInvocation(LuaModInvocationLimits limits, CancellationToken cancellationToken = default)
            {
                if (limits == null) throw new ArgumentNullException(nameof(limits));
                LuaModInvocationLimits validated = new LuaModInvocationLimits(limits.TimeoutMs, limits.MaxSteps);
                cancellationToken.ThrowIfCancellationRequested();
                if (_activeInvocation != null) throw new InvalidOperationException("A test invocation is already active.");
                LastInvocationLimits = validated;
                InvocationCount++;
                _activeInvocation = new InvocationGuard(this, cancellationToken);
                return _activeInvocation;
            }

            public override HubModSaveResult SaveOrReload(string id, string code, ModReloadMode mode)
            {
                if (_activeInvocation == null) throw new InvalidOperationException("A save requires its bounded invocation.");
                _activeInvocation.ThrowIfCancellationRequested();
                return base.SaveOrReload(id, code, mode);
            }

            private sealed class InvocationGuard : IDisposable
            {
                private BudgetedCountingHubModService _owner;
                private readonly CancellationToken _cancellationToken;

                public InvocationGuard(BudgetedCountingHubModService owner, CancellationToken cancellationToken)
                {
                    _owner = owner;
                    _cancellationToken = cancellationToken;
                }

                public void ThrowIfCancellationRequested() => _cancellationToken.ThrowIfCancellationRequested();

                public void Dispose()
                {
                    if (_owner == null) return;
                    _owner._activeInvocation = null;
                    _owner.DisposedInvocationCount++;
                    _owner = null;
                }
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
            BudgetedCountingHubModService service = new();
            service.Sources["castle"] = "local x = 1";
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
            Assert.AreEqual(2, service.InvocationCount);
            AssertSaveReleased(root, service);
        }

        [Test]
        public void ModEditor_KeepObjectsToggle_IsOneHubPreference_ReadByEveryEditorAndWrittenOnChange()
        {
            BudgetedCountingHubModService service = new();
            service.Sources["first"] = "local x = 1";
            service.Sources["second"] = "local x = 2";
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
        public void ModEditor_ExternalEditsRequireAnExplicitOverwriteOfTheLatestReviewedSource()
        {
            BudgetedCountingHubModService service = new();
            service.Sources["edit"] = "local original = 1";
            HubModEditorPage editor = new(service, "edit", null, null, new MemoryEditorPreferences());
            VisualElement root = editor.Build();
            root.Q<TextField>("coreai-hub-mod-code").value = "local draft = 2";
            service.Sources["edit"] = "local external = 3";

            editor.Save();

            Assert.AreEqual(0, service.SaveModes.Count);
            Assert.AreEqual("local external = 3", service.Sources["edit"]);
            Assert.AreEqual("local draft = 2", editor.DraftSource);
            Assert.IsTrue(editor.IsDirty);
            StringAssert.Contains("changed outside", root.Q<Label>(HubModEditorPage.StatusLabelName).text);
            service.Sources["edit"] = "local newer = 4";
            Invoke(editor, "SaveCore", true);
            Assert.AreEqual(0, service.SaveModes.Count,
                "An overwrite decision cannot approve a version that changed again after the warning.");
            Assert.AreEqual("local newer = 4", service.Sources["edit"]);
            Invoke(editor, "SaveCore", true);
            Assert.AreEqual("local draft = 2", service.Sources["edit"]);
            Assert.AreEqual(1, service.SaveModes.Count);
            Assert.IsFalse(editor.IsDirty);
            AssertSaveReleased(root, service);
        }

        [Test]
        public void ModEditor_TwoEditorsCannotSilentlyReplaceEachOthersSavedDraft()
        {
            BudgetedCountingHubModService service = new();
            service.Sources["shared"] = "local original = 1";
            HubModEditorPage first = new(service, "shared", null, null, new MemoryEditorPreferences());
            HubModEditorPage second = new(service, "shared", null, null, new MemoryEditorPreferences());
            first.Build().Q<TextField>("coreai-hub-mod-code").value = "local first = 2";
            second.Build().Q<TextField>("coreai-hub-mod-code").value = "local second = 3";

            first.Save();
            second.Save();

            Assert.AreEqual("local first = 2", service.Sources["shared"]);
            Assert.AreEqual("local second = 3", second.DraftSource);
            Assert.AreEqual(1, service.SaveModes.Count);
        }

        [TestCase(HubModPersistenceStatus.Failed)]
        [TestCase(HubModPersistenceStatus.Unconfirmed)]
        public void ModEditor_FailedOrUnconfirmedPersistenceKeepsTheDraftAndBackGuard_AndCanRetryItsOwnLiveChange(
            HubModPersistenceStatus persistenceStatus)
        {
            BudgetedCountingHubModService service = new() { SavePersistenceStatus = persistenceStatus };
            service.Sources["retry"] = "local previous = 1";
            int closes = 0;
            HubModEditorPage editor = new(service, "retry", null, () => closes++, new MemoryEditorPreferences());
            VisualElement root = editor.Build();
            root.Q<TextField>("coreai-hub-mod-code").value = "local retry = 2";

            editor.Save();
            Invoke(editor, "Close");

            Assert.AreEqual(0, closes);
            Assert.IsTrue(editor.IsDirty);
            Assert.AreEqual("local retry = 2", editor.DraftSource);
            Assert.AreEqual(DisplayStyle.Flex, root.Q<Button>("coreai-hub-mod-discard").style.display.value);
            AssertSaveReleased(root, service);
            service.SavePersistenceStatus = HubModPersistenceStatus.Saved;
            editor.Save();
            Assert.AreEqual(2, service.SaveModes.Count, "The retry must not conflict with its own previous live execution.");
            Assert.IsFalse(editor.IsDirty);
            Invoke(editor, "Close");
            Assert.AreEqual(1, closes);
            AssertSaveReleased(root, service);
        }

        [TestCase(HubModPersistenceStatus.Failed)]
        [TestCase(HubModPersistenceStatus.Unconfirmed)]
        public void ModEditor_UnacknowledgedSaveOfUnchangedSourceStillKeepsTheDraftDirty(
            HubModPersistenceStatus persistenceStatus)
        {
            BudgetedCountingHubModService service = new() { SavePersistenceStatus = persistenceStatus };
            service.Sources["same"] = "local x = 1";
            int closes = 0;
            HubModEditorPage editor = new(service, "same", null, () => closes++, new MemoryEditorPreferences());
            VisualElement root = editor.Build();
            Assert.IsFalse(editor.IsDirty);

            editor.Save();
            Invoke(editor, "Close");

            Assert.IsTrue(editor.IsDirty);
            Assert.AreEqual(0, closes);
            Assert.AreEqual("local x = 1", editor.DraftSource);
            AssertSaveReleased(root, service);
            service.SavePersistenceStatus = HubModPersistenceStatus.Saved;
            editor.Save();
            Assert.IsFalse(editor.IsDirty);
            AssertSaveReleased(root, service);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ModEditor_ThrownOrCancelledSavePreservesTheBufferAndUnsavedCloseGuard(bool cancelled)
        {
            BudgetedCountingHubModService service = new()
            {
                SaveFailure = cancelled ? new OperationCanceledException() : new InvalidOperationException("write refused")
            };
            service.Sources["edit"] = "local original = 1";
            int closes = 0;
            HubModEditorPage editor = new(service, "edit", null, () => closes++, new MemoryEditorPreferences());
            VisualElement root = editor.Build();
            root.Q<TextField>("coreai-hub-mod-code").value = "local draft = 2";
            editor.Save();
            Invoke(editor, "Close");
            Assert.AreEqual("local draft = 2", editor.DraftSource);
            Assert.IsTrue(editor.IsDirty);
            Assert.AreEqual(0, closes);
            Assert.AreEqual("local original = 1", service.Sources["edit"]);
            AssertSaveReleased(root, service);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ModEditor_ThrownOrCancelledSaveAfterLiveMutationCanRetryWithoutAnOverwritePrompt(bool cancelled)
        {
            BudgetedCountingHubModService service = new()
            {
                SaveFailure = cancelled ? new OperationCanceledException() : new InvalidOperationException("write refused"),
                MutateBeforeSaveFailure = true
            };
            service.Sources["retry"] = "local original = 1";
            HubModEditorPage editor = new(service, "retry", null, null, new MemoryEditorPreferences());
            VisualElement root = editor.Build();
            root.Q<TextField>("coreai-hub-mod-code").value = "local draft = 2";
            editor.Save();
            Assert.IsTrue(editor.IsDirty);
            Assert.AreEqual("local draft = 2", service.Sources["retry"]);
            AssertSaveReleased(root, service);

            service.SaveFailure = null;
            editor.Save();

            Assert.IsFalse(editor.IsDirty);
            Assert.AreEqual(1, service.SaveModes.Count);
            StringAssert.DoesNotContain("changed outside", root.Q<Label>(HubModEditorPage.StatusLabelName).text);
            AssertSaveReleased(root, service);
        }

        [Test]
        public void ModEditor_UnbudgetedLegacyServiceCannotRunOrSaveTheDraft()
        {
            CountingHubModService service = new();
            service.Sources["legacy"] = "local original = 1";
            HubModEditorPage editor = new(service, "legacy", null, null, new MemoryEditorPreferences());
            VisualElement root = editor.Build();
            try
            {
                root.Q<TextField>("coreai-hub-mod-code").value = "local draft = 2";

                editor.Save();

                Assert.AreEqual(CodeRunStatus.Unsupported, editor.LastRunResult.Status);
                Assert.IsFalse(editor.LastRunResult.Executed);
                Assert.IsFalse(editor.LastRunResult.Persisted);
                Assert.AreEqual(0, service.SaveModes.Count);
                Assert.AreEqual("local original = 1", service.Sources["legacy"]);
                Assert.AreEqual("local draft = 2", editor.DraftSource);
                Assert.IsTrue(editor.IsDirty);
                StringAssert.Contains("does not support bounded", root.Q<Label>(HubModEditorPage.StatusLabelName).text);
                Assert.IsTrue(root.Q<Button>("coreai-hub-mod-save-run").enabledSelf);
            }
            finally
            {
                editor.OnDestroyed();
            }
        }

        [Test]
        public void ModEditor_UnreadableExistingSourceNeverOpensAnEmptyBuffer()
        {
            CountingHubModService service = new() { ThrowOnRead = true };
            Assert.Throws<InvalidOperationException>(() => new HubModEditorPage(service, "existing", null, null));
            Assert.AreEqual(0, service.SaveModes.Count);
            service.ThrowOnRead = false;
            Assert.Throws<InvalidOperationException>(() => new HubModEditorPage(service, "missing", null, null));
        }

        [Test]
        public void ModsPage_DeactivationPreservesTheDraftButDestructionFencesItsSaveCallbacks()
        {
            CountingHubModService service = new();
            service.Sources["edit"] = "local original = 1";
            HubModsPage page = new(service, 300, null, new MemoryEditorPreferences());
            VisualElement root = (VisualElement)page.CreatePageContent();
            Invoke(page, "OpenEditEditor", "edit");
            HubModEditorPage editor = (HubModEditorPage)typeof(HubModsPage)
                .GetField("_editor", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(page);
            root.Q<TextField>("coreai-hub-mod-code").value = "local preserved = 2";

            page.OnDeactivated();
            page.OnActivated();
            Assert.AreEqual("local preserved = 2", editor.DraftSource);
            page.OnDestroyed();
            editor.Save();
            Invoke(page, "DeleteMod", "edit");

            Assert.AreEqual(0, service.SaveModes.Count);
            Assert.AreEqual(0, service.DeleteCalls);
            Assert.AreEqual("local original = 1", service.Sources["edit"]);
        }

        [Test]
        public void ModsPage_FalseDeleteResultIsVisibleAndQueuedRefreshCannotReviveDestroyedViews()
        {
            CountingHubModService service = new();
            List<Action> queued = new();
            HubModsPage page = new(service, 300, queued.Add);
            page.CreatePageContent();
            Invoke(page, "DeleteMod", "noisy");
            Label status = (Label)typeof(HubModsPage).GetField("_status", BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(page);
            StringAssert.Contains("not confirmed", status.text);
            service.RaiseModsChanged();
            Assert.AreEqual(1, queued.Count);
            int reads = service.ListModsCalls;
            page.OnDestroyed();
            queued[0]();
            service.RaiseModsChanged();
            Assert.AreEqual(reads, service.ListModsCalls);
            Assert.AreEqual(1, queued.Count);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ModsPage_DeletionCancellationAndLocalCommitArmingFailureHaveDistinctOutcomes(bool cancelled)
        {
            CountingHubModService service = new()
            {
                DeleteFailure = cancelled ? (Exception)new OperationCanceledException()
                    : new LuaModSourcePersistenceException(true, new InvalidOperationException("arming refused"))
            };
            HubModsPage page = new(service, 300, null, new MemoryEditorPreferences());
            VisualElement root = (VisualElement)page.CreatePageContent();
            try
            {
                Invoke(page, "DeleteMod", "removed");
                Label status = (Label)typeof(HubModsPage).GetField("_status", BindingFlags.Instance | BindingFlags.NonPublic)
                    .GetValue(page);
                StringAssert.Contains(cancelled ? "cancelled" : "Deleted 'removed' locally", status.text);
                if (!cancelled)
                {
                    StringAssert.Contains("NOT armed", status.text);
                }

                Assert.AreEqual(1, service.DeleteCalls);
            }
            finally
            {
                page.OnDestroyed();
            }
        }

        private static void Invoke(object target, string method, params object[] arguments)
        {
            MethodInfo member = target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(member, method);
            member.Invoke(target, arguments);
        }

        private static void AssertSaveReleased(VisualElement root, BudgetedCountingHubModService service)
        {
            Assert.AreEqual(service.InvocationCount, service.DisposedInvocationCount,
                "Every completed save must release its invocation, including failed and cancelled saves.");
            Assert.AreEqual(0, service.ActiveInvocationCount);
            Assert.IsTrue(root.Q<Button>("coreai-hub-mod-save-run").enabledSelf);
            Assert.IsTrue(root.Q<DropdownField>("coreai-code-editor-language").enabledSelf);
            Assert.IsFalse(root.Q<TextField>("coreai-hub-mod-code").isReadOnly);
            if (service.InvocationCount > 0)
            {
                Assert.AreEqual(100, service.LastInvocationLimits.TimeoutMs);
                Assert.AreEqual(500_000, service.LastInvocationLimits.MaxSteps);
            }
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
