using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using NUnit.Framework;
using Newtonsoft.Json.Linq;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// A skill catalog that changes while the game is running: a skill authored, revised or deleted
    /// mid-session, and a tool call that was already resolved when the catalog moved underneath it.
    /// <para>
    /// WHY these are pinned separately from <see cref="SkillSectionDisclosureEditModeTests"/>: that suite
    /// asks what one fixed catalog answers. The dangerous case is the catalog CHANGING - the model reads a
    /// skill, the host publishes a new version of it, and the pending call must still run the body it was
    /// bound to. A call silently executed by a different tool body with the same name is the worst
    /// possible outcome here, because the model gets a plausible answer from the wrong code and nothing
    /// anywhere reports an error.
    /// </para>
    /// <para>
    /// Preparation is checked too: it must go through the ASYNC store surface only. A synchronous store
    /// call inside an LLM turn parks the frame, and on a WebGL player - one thread, no pool - that is a
    /// permanent freeze rather than a stall.
    /// </para>
    /// </summary>
    public sealed class SkillLiveAvailabilityEditModeTests
    {
        private SynchronizationContext _previousSynchronizationContext;

        /// <summary>
        /// WHY this fixture detaches: a test here waits on a Task from the calling thread
        /// (Assert.ThrowsAsync/CatchAsync does exactly that). Under Unity's SynchronizationContext the
        /// awaited continuation is posted back to the very thread the wait is holding, and the whole
        /// EditMode run hangs at this fixture with no results file - not a failure, silence.
        /// </summary>
        [SetUp]
        public void DetachSynchronizationContext()
        {
            _previousSynchronizationContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
        }

        [TearDown]
        public void RestoreSynchronizationContext()
        {
            SynchronizationContext.SetSynchronizationContext(_previousSynchronizationContext);
        }

        private static DelegateLlmTool Tool(string name, Func<string> body)
        {
            return new DelegateLlmTool(name, "Test tool: " + name, body);
        }

        private static SkillSet MultiDocSkill(string name, string entryBody, string referenceBody,
            params ILlmTool[] tools)
        {
            return SkillSet.FromTextParts(name, name + " description",
                new[]
                {
                    new KeyValuePair<string, string>("SKILL.md", entryBody),
                    new KeyValuePair<string, string>("references/api.md", referenceBody)
                },
                tools);
        }

        private static async Task<JObject> ReadSkillAsync(ILlmTool readSkill, string skillName,
            string section = null, bool all = false)
        {
            Dictionary<string, object> args = new()
            {
                ["skill_name"] = skillName,
                ["all"] = all
            };
            if (section != null)
            {
                args["section"] = section;
            }

            object result = await ((IAIFunctionLlmTool)readSkill).CreateAIFunction()
                .InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments(args), CancellationToken.None);
            return JObject.Parse(result?.ToString() ?? "{}");
        }

        private static bool TryResolve(ILlmTool callSkillTool, string toolName,
            out ResolvedLlmToolInvocation invocation, out string error)
        {
            return ((IResolvedLlmToolCallProvider)callSkillTool).TryResolveInvocation(
                new Dictionary<string, object>
                {
                    ["tool_name"] = toolName,
                    ["arguments_json"] = "{}"
                },
                out invocation, out error);
        }

        // ============ the catalog changes while the session runs ============

        [Test]
        public async Task ASkillAddedMidSession_BecomesReadableAndCallableWithoutRebuildingTheTools()
        {
            MutableSkillCatalog catalog = new();
            ILlmTool readSkill = ReadSkillLlmTool.Create(catalog);
            ILlmTool callSkillTool = CallSkillToolLlmTool.Create(catalog);

            JObject before = await ReadSkillAsync(readSkill, "Quiz");
            Assert.IsFalse(before["success"].Value<bool>(), "the skill does not exist yet");

            catalog.AddOrReplace(MultiDocSkill("Quiz", "ENTRY_BODY", "REFERENCE_BODY",
                Tool("ask_question", () => "asked")));

            JObject after = await ReadSkillAsync(readSkill, "Quiz");
            Assert.IsTrue(after["success"].Value<bool>(),
                "the meta tools read the live catalog, so a skill published mid-session is visible " +
                "without rebuilding the agent");
            StringAssert.Contains("ENTRY_BODY", after["instructions"].Value<string>());
            CollectionAssert.Contains(after["sections"].ToObject<string[]>(), "references/api.md");

            Assert.IsTrue(TryResolve(callSkillTool, "ask_question", out ResolvedLlmToolInvocation call, out string error),
                "resolve failed: " + error);
            Assert.AreEqual("asked", (await call.InvokeAsync(CancellationToken.None))?.ToString());
        }

        [Test]
        public async Task ASkillRemovedMidSession_StopsBeingReadableAndCallable_WithoutThrowing()
        {
            MutableSkillCatalog catalog = new();
            catalog.AddOrReplace(MultiDocSkill("Quiz", "ENTRY_BODY", "REFERENCE_BODY",
                Tool("ask_question", () => "asked")));
            catalog.AddOrReplace(MultiDocSkill("Grading", "GRADE_BODY", "GRADE_REFERENCE",
                Tool("grade_answer", () => "graded")));
            ILlmTool readSkill = ReadSkillLlmTool.Create(catalog);
            ILlmTool callSkillTool = CallSkillToolLlmTool.Create(catalog);

            Assert.IsTrue(catalog.Remove("Quiz"));

            JObject read = await ReadSkillAsync(readSkill, "Quiz");
            Assert.IsFalse(read["success"].Value<bool>());
            CollectionAssert.DoesNotContain(read["available"].ToObject<string[]>(), "Quiz");
            CollectionAssert.Contains(read["available"].ToObject<string[]>(), "Grading",
                "removing one skill must not take the rest of the catalog with it");

            // WHY not an exception: a withdrawn tool is an ordinary tool RESULT for the model, which can
            // then choose something else. An exception here would abort the whole turn instead.
            Assert.IsFalse(TryResolve(callSkillTool, "ask_question", out _, out string error));
            StringAssert.Contains("ask_question", error);

            Assert.IsTrue(TryResolve(callSkillTool, "grade_answer", out ResolvedLlmToolInvocation survivor, out _));
            Assert.AreEqual("graded", (await survivor.InvokeAsync(CancellationToken.None))?.ToString());
        }

        [Test]
        public async Task AResolvedCall_RunsTheBodyItWasBoundTo_EvenAfterTheNameIsReboundToAnother()
        {
            // WHY this is the sharp edge: two skills may legitimately expose the same tool name over
            // time (a revision, a replacement pack). A call resolved against the OLD catalog must not be
            // completed by the NEW body - the model would get a plausible answer produced by code it
            // never asked for, and nothing would report an error.
            int oldBodyRuns = 0;
            int newBodyRuns = 0;
            MutableSkillCatalog catalog = new();
            catalog.AddOrReplace(MultiDocSkill("Quiz", "OLD_ENTRY", "OLD_REFERENCE",
                Tool("ask_question", () =>
                {
                    oldBodyRuns++;
                    return "old-body";
                })));
            ILlmTool callSkillTool = CallSkillToolLlmTool.Create(catalog);

            Assert.IsTrue(TryResolve(callSkillTool, "ask_question", out ResolvedLlmToolInvocation pending, out string error),
                "resolve failed: " + error);

            catalog.AddOrReplace(MultiDocSkill("Quiz", "NEW_ENTRY", "NEW_REFERENCE",
                Tool("ask_question", () =>
                {
                    newBodyRuns++;
                    return "new-body";
                })));

            Assert.AreEqual("old-body", (await pending.InvokeAsync(CancellationToken.None))?.ToString(),
                "the pending call carries its own binding; a later catalog version cannot claim it");
            Assert.AreEqual(1, oldBodyRuns);
            Assert.AreEqual(0, newBodyRuns, "the replacement body must not have run at all");

            // A call resolved AFTER the swap does get the new body - the catalog really did change.
            Assert.IsTrue(TryResolve(callSkillTool, "ask_question", out ResolvedLlmToolInvocation fresh, out _));
            Assert.AreEqual("new-body", (await fresh.InvokeAsync(CancellationToken.None))?.ToString());
            Assert.AreEqual(1, newBodyRuns);
        }

        [Test]
        public async Task AResolvedCall_SurvivesTheRemovalOfItsSkill_AndCancelsBeforeEnteringTheBody()
        {
            int runs = 0;
            MutableSkillCatalog catalog = new();
            catalog.AddOrReplace(MultiDocSkill("Quiz", "ENTRY", "REFERENCE",
                Tool("ask_question", () =>
                {
                    runs++;
                    return "asked";
                })));
            ILlmTool callSkillTool = CallSkillToolLlmTool.Create(catalog);

            Assert.IsTrue(TryResolve(callSkillTool, "ask_question", out ResolvedLlmToolInvocation pending, out _));
            Assert.IsTrue(catalog.Remove("Quiz"));

            // The binding outlives the catalog entry: the queued call finishes with the body it named.
            Assert.AreEqual("asked", (await pending.InvokeAsync(CancellationToken.None))?.ToString());
            Assert.AreEqual(1, runs);

            Assert.IsFalse(TryResolve(callSkillTool, "ask_question", out _, out _),
                "a NEW resolve after the removal must fail - the tool is gone from the catalog");

            // And a cancelled queued call must not enter the body at all.
            catalog.AddOrReplace(MultiDocSkill("Quiz", "ENTRY", "REFERENCE",
                Tool("ask_question", () =>
                {
                    runs++;
                    return "asked";
                })));
            Assert.IsTrue(TryResolve(callSkillTool, "ask_question", out ResolvedLlmToolInvocation cancelled, out _));
            using CancellationTokenSource cts = new();
            cts.Cancel();
            Assert.CatchAsync<OperationCanceledException>(async () => await cancelled.InvokeAsync(cts.Token));
            Assert.AreEqual(1, runs, "cancellation is honoured before any tool body is entered");
        }

        // ============ multi-document skills across a mid-session revision ============

        [Test]
        public async Task RevisingTheMainDocumentMidSession_KeepsTheReferenceDocumentsAddressable()
        {
            MutableSkillCatalog catalog = new();
            NullSkillStore store = new();
            SkillAuthoringCoordinator coordinator = new(catalog, store, new MemoryLuaScriptVersionStore(),
                _ => null, callbackContext: PassThroughLlmAsyncMarshaler.Instance);
            ILlmTool readSkill = ReadSkillLlmTool.Create(catalog);

            catalog.AddOrReplace(MultiDocSkill("Quiz", "OLD_ENTRY", "REFERENCE_BODY"));

            SkillAuthoringResult updated = await coordinator.UpdateAsync("Quiz", null, "NEW_ENTRY", null);
            Assert.IsTrue(updated.Success, updated.Message);
            Assert.AreEqual(1, updated.Record.Version, "an update bumps the revision");

            JObject entry = await ReadSkillAsync(readSkill, "Quiz");
            Assert.AreEqual("SKILL.md", entry["section"].Value<string>(),
                "the main document is still handed back whole, as the entry document");
            Assert.AreEqual("NEW_ENTRY", entry["instructions"].Value<string>());
            Assert.That(entry["instructions"].Value<string>(), Does.Not.Contain("REFERENCE_BODY"),
                "a reference document must not arrive with the entry one");

            JObject reference = await ReadSkillAsync(readSkill, "Quiz", "references/api.md");
            Assert.IsTrue(reference["success"].Value<bool>());
            Assert.AreEqual("REFERENCE_BODY", reference["instructions"].Value<string>(),
                "revising the main document must not drop or rewrite the references");

            JObject all = await ReadSkillAsync(readSkill, "Quiz", all: true);
            StringAssert.Contains("NEW_ENTRY", all["instructions"].Value<string>());
            StringAssert.Contains("REFERENCE_BODY", all["instructions"].Value<string>());
        }

        // ============ preparation: async only, and cancellable ============

        /// <summary>
        /// A store whose synchronous surface is a trap: every sync member counts itself and throws. Any
        /// synchronous store call from an async path is a parked frame, so the double turns it into a
        /// loud failure instead of a slow one. The async surface is implemented natively - deliberately
        /// NOT through <see cref="InlineAsyncSkillStoreAdapter"/>, which would satisfy the async contract
        /// by calling the very sync members under test.
        /// </summary>
        private sealed class SyncIsForbiddenSkillStore : ISkillStore, IAsyncSkillStore
        {
            private readonly Dictionary<string, SkillRecord> _records = new(StringComparer.OrdinalIgnoreCase);
            private readonly Func<CancellationToken, Task> _beforeList;

            internal SyncIsForbiddenSkillStore(Func<CancellationToken, Task> beforeList = null)
            {
                _beforeList = beforeList;
            }

            internal int SyncCalls { get; private set; }

            internal void Seed(SkillRecord record) => _records[record.Id] = record;

            private Exception Blocked(string member)
            {
                SyncCalls++;
                return new InvalidOperationException(
                    $"Synchronous {member} reached from an async path; that parks the frame.");
            }

            public void Save(SkillRecord record) => throw Blocked(nameof(Save));

            public bool TryLoad(string id, out SkillRecord record) => throw Blocked(nameof(TryLoad));

            public IReadOnlyList<SkillRecord> List() => throw Blocked(nameof(List));

            public void Delete(string id) => throw Blocked(nameof(Delete));

            public Task<SkillRecord> LoadAsync(string id, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string key = (id ?? "").Trim();
                return Task.FromResult(_records.TryGetValue(key, out SkillRecord record) ? Copy(record) : null);
            }

            public async Task<IReadOnlyList<SkillRecord>> ListAsync(CancellationToken cancellationToken = default)
            {
                if (_beforeList != null)
                {
                    await _beforeList(cancellationToken);
                }

                cancellationToken.ThrowIfCancellationRequested();
                List<SkillRecord> all = new(_records.Count);
                foreach (SkillRecord record in _records.Values)
                {
                    all.Add(Copy(record));
                }

                return all.AsReadOnly();
            }

            public async Task<TResult> MutateAndPublishAsync<TResult>(string id,
                Func<SkillRecord, SkillStoreMutation<TResult>> prepare,
                Func<TResult, CancellationToken, Task> publish, ILlmAsyncMarshaler callbackContext,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string key = (id ?? "").Trim();
                _records.TryGetValue(key, out SkillRecord current);
                SkillStoreMutation<TResult> mutation = prepare(Copy(current));
                cancellationToken.ThrowIfCancellationRequested();
                if (mutation.Save)
                {
                    _records[key] = Copy(mutation.Record);
                }
                else if (mutation.Delete)
                {
                    _records.Remove(key);
                }

                if (publish != null)
                {
                    await publish(mutation.Result, CancellationToken.None);
                }

                return mutation.Result;
            }

            private static SkillRecord Copy(SkillRecord record) => record == null
                ? null
                : new SkillRecord(record.Id, record.Description, record.Instructions, record.ToolNames,
                    record.Version, record.Sections);
        }

        [Test]
        public void PreparationRefusesAStoreWithoutAnAsyncSurface_InsteadOfBlockingOnIt()
        {
            // WHY loud: the alternative is a silent fallback to the synchronous store, i.e. file IO inside
            // an LLM turn. A host that knowingly wires a fast in-memory store opts in with
            // InlineAsyncSkillStoreAdapter, and the message says so.
            MutableSkillCatalog catalog = new();
            SkillAuthoringCoordinator coordinator = new(catalog, new SyncOnlySkillStore(),
                new MemoryLuaScriptVersionStore(), _ => null,
                callbackContext: PassThroughLlmAsyncMarshaler.Instance);

            InvalidOperationException refusal =
                Assert.ThrowsAsync<InvalidOperationException>(async () => await coordinator.RehydrateFromStoreAsync());
            StringAssert.Contains("InlineAsync", refusal.Message);
        }

        private sealed class SyncOnlySkillStore : ISkillStore
        {
            public void Save(SkillRecord record)
            {
            }

            public bool TryLoad(string id, out SkillRecord record)
            {
                record = null;
                return false;
            }

            public IReadOnlyList<SkillRecord> List() => Array.Empty<SkillRecord>();

            public void Delete(string id)
            {
            }
        }

        [Test]
        public void PreparationIsCancellable_BeforeItTouchesTheCatalog()
        {
            MutableSkillCatalog catalog = new();
            SyncIsForbiddenSkillStore store = new();
            store.Seed(new SkillRecord("Quiz", "d", "body", Array.Empty<string>()));
            SkillAuthoringCoordinator coordinator = new(catalog, store, new MemoryLuaScriptVersionStore(),
                _ => null, callbackContext: PassThroughLlmAsyncMarshaler.Instance);

            using CancellationTokenSource cts = new();
            cts.Cancel();

            Assert.CatchAsync<OperationCanceledException>(
                async () => await coordinator.RehydrateFromStoreAsync(cts.Token));
            Assert.IsNull(catalog.Get("Quiz"),
                "a cancelled preparation must leave the live catalog exactly as it was");
        }

        [Test]
        public async Task PreparationCancelledMidRead_PublishesNothing()
        {
            // WHY mid-read and not just pre-cancelled: the pre-cancelled case never reaches the store at
            // all. The interesting one is a token that fires while the batch is being read - a partial
            // hydration published into the live catalog would leave the agent with half a skill set.
            MutableSkillCatalog catalog = new();
            CancellationTokenSource cts = new();
            bool cancelledOnce = false;
            SyncIsForbiddenSkillStore store = new(_ =>
            {
                // Only the FIRST read is interrupted; the retry below must find a healthy store.
                if (!cancelledOnce)
                {
                    cancelledOnce = true;
                    cts.Cancel();
                }

                return Task.CompletedTask;
            });
            store.Seed(new SkillRecord("Quiz", "d", "body", Array.Empty<string>()));
            SkillAuthoringCoordinator coordinator = new(catalog, store, new MemoryLuaScriptVersionStore(),
                _ => null, callbackContext: PassThroughLlmAsyncMarshaler.Instance);

            Assert.CatchAsync<OperationCanceledException>(
                async () => await coordinator.RehydrateFromStoreAsync(cts.Token));
            Assert.IsNull(catalog.Get("Quiz"));
            cts.Dispose();

            // The coordinator is still usable afterwards: a cancelled preparation is not a broken one.
            using CancellationTokenSource fresh = new();
            Assert.AreEqual(1, await coordinator.RehydrateFromStoreAsync(fresh.Token));
            Assert.IsNotNull(catalog.Get("Quiz"));
        }

        [Test]
        public async Task RestartRehydratesEveryDocumentOfAMultiDocumentSkill()
        {
            // WHY through the store and a brand-new catalog: this is the restart path. A skill that comes
            // back with only its entry document reads as "the references were never written", and the
            // model silently loses half of what it was taught.
            SyncIsForbiddenSkillStore store = new();
            MutableSkillCatalog first = new();
            SkillAuthoringCoordinator authoring = new(first, store, new MemoryLuaScriptVersionStore(),
                _ => null, callbackContext: PassThroughLlmAsyncMarshaler.Instance);

            first.AddOrReplace(MultiDocSkill("Quiz", "ENTRY_BODY", "REFERENCE_BODY"));
            Assert.IsTrue((await authoring.UpdateAsync("Quiz", null, "ENTRY_BODY_V2", null)).Success);

            MutableSkillCatalog afterRestart = new();
            SkillAuthoringCoordinator restarted = new(afterRestart, store, new MemoryLuaScriptVersionStore(),
                _ => null, callbackContext: PassThroughLlmAsyncMarshaler.Instance);
            Assert.AreEqual(1, await restarted.RehydrateFromStoreAsync());

            ILlmTool readSkill = ReadSkillLlmTool.Create(afterRestart);
            JObject entry = await ReadSkillAsync(readSkill, "Quiz");
            Assert.AreEqual("ENTRY_BODY_V2", entry["instructions"].Value<string>());
            CollectionAssert.Contains(entry["sections"].ToObject<string[]>(), "references/api.md");

            JObject reference = await ReadSkillAsync(readSkill, "Quiz", "references/api.md");
            Assert.AreEqual("REFERENCE_BODY", reference["instructions"].Value<string>());
        }

        [Test]
        public async Task AsyncAuthoringNeverFallsBackToTheSynchronousStore()
        {
            // The sync surface of a store is the frame-blocking one. Everything the authoring tool does
            // during a turn - list, get, create, update, delete - must go through the async surface.
            MutableSkillCatalog catalog = new();
            SyncIsForbiddenSkillStore store = new();
            SkillAuthoringCoordinator coordinator = new(catalog, store, new MemoryLuaScriptVersionStore(),
                _ => null, callbackContext: PassThroughLlmAsyncMarshaler.Instance);
            ManageSkillsLlmTool tool = new(coordinator);

            Assert.IsTrue(JObject.Parse(await tool.ExecuteAsync("create", "Quiz", "d", "body", "[]"))["success"]
                .Value<bool>());
            Assert.IsTrue(JObject.Parse(await tool.ExecuteAsync("update", "Quiz", null, "body v2", null))["success"]
                .Value<bool>());
            Assert.IsTrue(JObject.Parse(await tool.ExecuteAsync("get", "Quiz"))["success"].Value<bool>());
            Assert.IsTrue(JObject.Parse(await tool.ExecuteAsync("list"))["success"].Value<bool>());
            Assert.IsTrue(JObject.Parse(await tool.ExecuteAsync("delete", "Quiz"))["success"].Value<bool>());

            Assert.AreEqual(0, store.SyncCalls,
                "manage_skills reached the synchronous store surface, which parks the frame");
            Assert.IsNull(catalog.Get("Quiz"));
        }

        [Test]
        public void AuthoringIsCancellable_AndACancelledEditPublishesNothing()
        {
            MutableSkillCatalog catalog = new();
            SyncIsForbiddenSkillStore store = new();
            SkillAuthoringCoordinator coordinator = new(catalog, store, new MemoryLuaScriptVersionStore(),
                _ => null, callbackContext: PassThroughLlmAsyncMarshaler.Instance);

            using CancellationTokenSource cts = new();
            cts.Cancel();

            Assert.CatchAsync<OperationCanceledException>(async () =>
                await coordinator.CreateAsync("Quiz", "d", "body", Array.Empty<string>(), cts.Token));
            Assert.IsNull(catalog.Get("Quiz"), "a cancelled create must not reach the live catalog");
            Assert.AreEqual(0, store.SyncCalls);
        }
    }
}
