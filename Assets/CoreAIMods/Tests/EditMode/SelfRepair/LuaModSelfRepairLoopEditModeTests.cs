using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Ai.Logging;
using CoreAI.Ai.LuaCs;
using CoreAI.Ai.SelfRepair;
using CoreAI.Authority;
using CoreAI.ChatOverlay;
using CoreAI.Composition;
using CoreAI.Infrastructure.Logging;
using CoreAI.Logging;
using CoreAI.Mods.WorldPackages;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.SelfRepair
{
    /// <summary>
    /// The runtime repair loop end to end on the real Lua-CSharp mod runtime and the real <c>manage_mods</c> tool: a
    /// scripted model repairs through the tool, through a world-mutation gate that records every change, and the loop
    /// verifies the fix by the mod's errors stopping. Twins: a mod that fails once is not touched, a mod the model cannot
    /// fix is stopped after three attempts, a hand-written mod is reported but not rewritten, offline says so once, ten
    /// failing mods do not start ten turns, and the player's stop ends the repair.
    /// </summary>
    public sealed class LuaModSelfRepairLoopEditModeTests
    {
        private const string Broken = "hooks_every(0.05, function() local part = nil; return part.Size end)";
        private const string Fixed = "hooks_every(0.05, function() local part = { Size = 1 }; return part.Size end)";

        [Test]
        public async Task AiWrittenMod_ThatStartsFailing_IsRepairedThroughTheTool_AndVerified()
        {
            using Harness harness = new();
            await harness.LoadThroughToolAsync("tower", Broken);
            harness.Model.Fix = _ => Fixed;

            harness.FailUntilIncident();
            StringAssert.StartsWith("Mod 'tower' is failing: attempt to index a nil value", harness.Lines[0]);

            harness.Coordinator.Tick();
            Assert.AreEqual(1, harness.Model.Requests.Count, "one repair turn starts");
            await harness.Model.Pending;
            harness.Coordinator.Tick();
            Assert.AreEqual(LuaModRepairPhase.Verifying, harness.Coordinator.Policy.PhaseOf("tower"));

            harness.TickRuntime(5);
            harness.Clock += 6d;
            harness.Coordinator.Tick();

            Assert.AreEqual(LuaModRepairPhase.Fixed, harness.Coordinator.Policy.PhaseOf("tower"));
            Assert.AreEqual("'tower' is fixed: no errors for 5 s.", harness.Lines[harness.Lines.Count - 1]);
            Assert.IsTrue(harness.Runtime.TryGetModSource(harness.Actor, "tower", out string source));
            Assert.AreEqual(Fixed, source);
            CollectionAssert.Contains(harness.Gate.Triggers, LuaModsLlmTool.ReloadBackupTrigger,
                "the fix went through the shared gate as a manage_mods reload, the path /undo takes back");
            Assert.AreEqual(harness.Actor.ActorId, harness.Gate.Actors[harness.Gate.Actors.Count - 1],
                "and it is attributed to the chat's actor, whose undo history it lands in");
            Assert.AreEqual(LuaModAuthor.Ai, harness.Coordinator.Authorship.AuthorOf("tower"));
        }

        [Test]
        public async Task RepairRequest_CarriesTheErrorTheLineTheLogsTheSourceAndThePlayersRequest()
        {
            using Harness harness = new();
            harness.Requests.Record("build a tower with a glowing top");
            await harness.LoadThroughToolAsync("tower", "-- tower\n" + Broken);
            harness.Model.Fix = _ => Fixed;
            harness.FailUntilIncident();

            harness.Coordinator.Tick();

            string text = harness.Model.Requests[0].Text;
            StringAssert.Contains("Runtime repair, attempt 1 of 3", text);
            StringAssert.Contains("lua_error=attempt to index a nil value", text);
            StringAssert.Contains("(tower:2)", text);
            StringAssert.Contains("player_request=build a tower with a glowing top", text);
            StringAssert.Contains("recent_logs:", text);
            StringAssert.Contains("fix_this_lua (lines 1-2 of 2):", text);
            StringAssert.Contains(">2| hooks_every", text);
            StringAssert.Contains("manage_mods reload mod_id='tower'", text);
            StringAssert.Contains("since_reload=true wait_seconds=5", text);
            Assert.LessOrEqual(text.Length, LuaModRepairPromptBuilder.MaximumCharacters);
            Assert.AreEqual(BuiltInAgentRoleIds.Programmer, harness.Model.Requests[0].RoleId);
            Assert.AreEqual("AI is fixing 'tower' (try 1 of 3). /stop cancels.", harness.Model.Requests[0].Announcement);
            await harness.Model.Pending;
        }

        [Test]
        public async Task ModThatFailsOnce_AndRecovers_IsNotTouched()
        {
            using Harness harness = new();
            await harness.LoadThroughToolAsync("blip",
                "local n = 0\nhooks_every(0.05, function() n = n + 1; if n == 1 then error('once') end end)");

            harness.TickRuntime(10);
            harness.Coordinator.Tick();

            Assert.IsEmpty(harness.Model.Requests);
            Assert.IsEmpty(harness.Lines);
            Assert.AreEqual(LuaModRepairPhase.Healthy, harness.Coordinator.Policy.PhaseOf("blip"));
        }

        [Test]
        public async Task ModTheModelCannotFix_IsStoppedAfterThreeAttempts_AndReported()
        {
            using Harness harness = new();
            await harness.LoadThroughToolAsync("tower", Broken);
            harness.Model.Fix = request => "-- try " + request.Attempt + "\n" + Broken;
            harness.FailUntilIncident();

            for (int attempt = 1; attempt <= 3; attempt++)
            {
                harness.Coordinator.Tick();
                Assert.AreEqual(attempt, harness.Model.Requests.Count, "attempt " + attempt + " started");
                await harness.Model.Pending;
                harness.Coordinator.Tick();
                harness.TickRuntime(2);
                harness.Clock += 31d;
            }

            harness.TickRuntime(20);
            harness.Clock += 1000d;
            harness.Coordinator.Tick();

            Assert.AreEqual(3, harness.Model.Requests.Count, "no fourth attempt");
            Assert.AreEqual(LuaModRepairPhase.GaveUp, harness.Coordinator.Policy.PhaseOf("tower"));
            string last = harness.Lines[harness.Lines.Count - 1];
            StringAssert.StartsWith("Could not fix 'tower' after 3 tries", last);
            StringAssert.Contains("/undo takes back the AI's change", last);
            Assert.IsTrue(harness.Lines.Exists(line => line.Contains("the AI tries again in 10 s")), "first backoff");
            Assert.IsTrue(harness.Lines.Exists(line => line.Contains("the AI tries again in 30 s")), "second backoff");
        }

        [Test]
        public void HandWrittenMod_IsReported_NotRewritten()
        {
            using Harness harness = new();
            harness.Runtime.LoadMod(harness.Actor, "mine", Broken, LuaCapabilities.All, false);
            harness.Model.Fix = _ => Fixed;

            harness.FailUntilIncident("mine");
            harness.Coordinator.Tick();
            harness.TickRuntime(10);
            harness.Coordinator.Tick();

            Assert.IsEmpty(harness.Model.Requests, "the AI does not rewrite the player's mod behind their back");
            Assert.AreEqual(1, harness.Lines.Count, "reported once");
            StringAssert.Contains("The AI did not write it, so the AI leaves it alone; /fixmod mine asks the AI to fix it.",
                harness.Lines[0]);
            Assert.AreEqual(LuaModAuthor.Other, harness.Coordinator.Authorship.AuthorOf("mine"));
        }

        [Test]
        public async Task HandWrittenMod_IsRepaired_WhenThePlayerAsks()
        {
            using Harness harness = new();
            harness.Runtime.LoadMod(harness.Actor, "mine", Broken, LuaCapabilities.All, false);
            harness.Model.Fix = _ => Fixed;
            harness.FailUntilIncident("mine");

            Assert.AreEqual("Queued 'mine' for repair.", harness.Coordinator.RequestRepair("mine"));
            harness.Coordinator.Tick();
            await harness.Model.Pending;
            harness.Coordinator.Tick();
            harness.Clock += 6d;
            harness.Coordinator.Tick();

            Assert.AreEqual(1, harness.Model.Requests.Count);
            Assert.AreEqual(LuaModRepairPhase.Fixed, harness.Coordinator.Policy.PhaseOf("mine"));
            Assert.AreEqual(LuaModAuthor.Ai, harness.Coordinator.Authorship.AuthorOf("mine"),
                "the AI made the last change now");
        }

        [Test]
        public void AllModsScope_RepairsAHandWrittenMod_WithoutAsking()
        {
            using Harness harness = new(new LuaModRuntimeRepairOptions { Scope = LuaModRuntimeRepairScope.AllMods });
            harness.Runtime.LoadMod(harness.Actor, "mine", Broken, LuaCapabilities.All, false);
            harness.Model.Fix = _ => Fixed;
            harness.FailUntilIncident("mine");

            harness.Coordinator.Tick();

            Assert.AreEqual(1, harness.Model.Requests.Count);
        }

        [Test]
        public async Task AskFirstMode_ReportsWithAHint_AndRepairsOnlyOnRequest()
        {
            using Harness harness = new(new LuaModRuntimeRepairOptions { Mode = LuaModRuntimeRepairMode.AskFirst });
            await harness.LoadThroughToolAsync("tower", Broken);
            harness.Model.Fix = _ => Fixed;
            harness.FailUntilIncident();
            harness.Coordinator.Tick();

            Assert.IsEmpty(harness.Model.Requests);
            StringAssert.EndsWith("/fixmod tower asks the AI to fix it.", harness.Lines[0]);
            harness.Coordinator.RequestRepair("tower");
            harness.Coordinator.Tick();
            Assert.AreEqual(1, harness.Model.Requests.Count);
            await harness.Model.Pending;
        }

        [Test]
        public async Task OffMode_OnlyReports()
        {
            using Harness harness = new(new LuaModRuntimeRepairOptions { Mode = LuaModRuntimeRepairMode.Off });
            await harness.LoadThroughToolAsync("tower", Broken);
            harness.FailUntilIncident();
            harness.Coordinator.Tick();

            Assert.IsEmpty(harness.Model.Requests);
            Assert.AreEqual(1, harness.Lines.Count);
        }

        [Test]
        public async Task Offline_SaysSoOnce_AndStartsNoTurn()
        {
            using Harness harness = new();
            harness.Model.IsModelAvailable = false;
            await harness.LoadThroughToolAsync("a", Broken);
            await harness.LoadThroughToolAsync("b", Broken);

            harness.FailUntilIncident("a");
            harness.FailUntilIncident("b");
            for (int frame = 0; frame < 20; frame++)
            {
                harness.Coordinator.Tick();
                harness.TickRuntime(1);
                harness.Clock += 10d;
            }

            Assert.IsEmpty(harness.Model.Requests);
            Assert.AreEqual(2, harness.Lines.Count, "one line per failing mod, then silence");
            StringAssert.Contains("The AI is offline", harness.Lines[0]);
            StringAssert.DoesNotContain("offline", harness.Lines[1]);
        }

        [Test]
        public async Task TenFailingMods_StartOneTurnAtATime_AndQueueAtMostThree()
        {
            using Harness harness = new();
            harness.Model.HoldTurns = true;
            for (int index = 0; index < 10; index++)
            {
                await harness.LoadThroughToolAsync("m" + index, Broken);
            }

            harness.TickRuntime(4);
            harness.Coordinator.Tick();
            harness.Coordinator.Tick();

            Assert.AreEqual(1, harness.Model.Requests.Count, "one repair turn at a time");
            Assert.AreEqual(2, harness.Coordinator.Queue.Count, "three were queued, one of them runs");
            Assert.AreEqual(7, harness.Lines.FindAll(line => line.Contains("The AI is busy with other failing mods")).Count);
            harness.Model.ReleaseHeld();
        }

        [Test]
        public async Task PlayerStop_EndsTheRepair_AndNoMoreAttemptsFollow()
        {
            using Harness harness = new();
            await harness.LoadThroughToolAsync("tower", Broken);
            harness.Model.EndWith = LuaModRepairTurnEnd.Cancelled;
            harness.FailUntilIncident();

            harness.Coordinator.Tick();
            await harness.Model.Pending;
            harness.Coordinator.Tick();
            harness.TickRuntime(10);
            harness.Clock += 1000d;
            harness.Coordinator.Tick();

            Assert.AreEqual(1, harness.Model.Requests.Count);
            Assert.AreEqual("Stopped repairing 'tower'. /fixmod tower starts again.", harness.Lines[harness.Lines.Count - 1]);
        }

        [Test]
        public async Task TurnPastItsTimeout_IsStopped_AndCountsAsAFailedAttempt()
        {
            using Harness harness = new();
            await harness.LoadThroughToolAsync("tower", Broken);
            harness.Model.HoldTurns = true;
            harness.FailUntilIncident();
            harness.Coordinator.Tick();

            harness.Clock += 121d;
            harness.Coordinator.Tick();
            Assert.AreEqual(1, harness.Model.Cancels, "the loop cancels the turn");
            harness.Model.ReleaseHeld(LuaModRepairTurnEnd.Cancelled);
            harness.Coordinator.Tick();

            Assert.AreEqual(LuaModRepairPhase.Backoff, harness.Coordinator.Policy.PhaseOf("tower"));
            StringAssert.StartsWith("The repair of 'tower' took too long", harness.Lines[harness.Lines.Count - 1]);
        }

        [Test]
        public async Task RepairLines_GoToTheModLog_ForTheConsoleAndTheModel()
        {
            using Harness harness = new();
            await harness.LoadThroughToolAsync("tower", Broken);
            harness.Model.Fix = _ => Fixed;
            harness.FailUntilIncident();
            harness.Coordinator.Tick();
            await harness.Model.Pending;

            IReadOnlyList<LuaLogEntry> repair = harness.Logs.Query(new LuaLogQuery { ModId = "tower" })
                .FindAllAsList(entry => entry.ScriptName == LuaModSelfRepairCoordinator.RepairScriptName);
            Assert.AreEqual(2, repair.Count);
            Assert.AreEqual(LuaLogLevel.Warn, repair[0].Level);
            StringAssert.StartsWith("AI is fixing 'tower' (try 1 of 3)", repair[1].Message);
        }

        [Test]
        public async Task UnloadedMod_IsForgotten_AndNotRepaired()
        {
            using Harness harness = new();
            await harness.LoadThroughToolAsync("tower", Broken);
            harness.Model.Fix = _ => Fixed;
            harness.FailUntilIncident();
            harness.Runtime.UnloadMod(harness.Actor, "tower");

            harness.Coordinator.Tick();

            Assert.IsEmpty(harness.Model.Requests);
            Assert.IsNull(harness.Coordinator.Policy.Get("tower"));
        }

        /// <summary>The real mod stack, the real tool, a recording gate and a scripted model.</summary>
        private sealed class Harness : IDisposable
        {
            public Harness(LuaModRuntimeRepairOptions options = null)
            {
                Logs = new LuaLogService();
                Stack = LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
                {
                    Logger = new SilentGameLogger(),
                    LogService = Logs,
                    Capabilities = LuaCapabilities.All,
                    OneOffCapabilities = LuaCapabilities.All,
                    AutoPersistMods = false
                });
                Runtime = Stack.Runtime;
                IActorIdentityProvider identity = CoreServicesInstaller.DefaultLocalHostIdentityProvider;
                Actor = identity.GetActorContext(BuiltInAgentRoleIds.Programmer);
                Gate = new RecordingGate();
                Tool = new LuaModsLlmTool(Runtime, new CoreAISettingsOptions(), NullLog.Instance, LuaCapabilities.All,
                    true, identity, BuiltInAgentRoleIds.Programmer, Gate, null);
                Model = new ScriptedRepairModel(Tool);
                Requests = new LuaModPlayerRequestRecorder();
                LuaModAuthorshipTracker authorship = new();
                Gate.AddObserver(authorship);
                Coordinator = new LuaModSelfRepairCoordinator(Runtime, Actor, options, authorship, Model, Logs, Chat,
                    null, () => Clock, () => Requests.CurrentRequest);
                Coordinator.PlayerLinePosted += (_, line) =>
                {
                    if (!line.StartsWith("AI is fixing", StringComparison.Ordinal))
                    {
                        Lines.Add(line);
                    }
                };
            }

            public LuaLogService Logs { get; }

            public LuaCsModStack Stack { get; }

            public ILuaModRuntime Runtime { get; }

            public ActorContext Actor { get; }

            public RecordingGate Gate { get; }

            public LuaModsLlmTool Tool { get; }

            public ScriptedRepairModel Model { get; }

            public LuaModPlayerRequestRecorder Requests { get; }

            public RecordingMessages Chat { get; } = new();

            public LuaModSelfRepairCoordinator Coordinator { get; }

            public List<string> Lines { get; } = new();

            public double Clock { get; set; } = 1000d;

            public async Task LoadThroughToolAsync(string modId, string code)
            {
                string json = await Tool.ExecuteAsync("load", modId, code);
                StringAssert.Contains("\"success\":true", json);
            }

            public void TickRuntime(int frames)
            {
                for (int frame = 0; frame < frames; frame++)
                {
                    Clock += 0.1d;
                    Runtime.Tick(Actor, 0.1d);
                }
            }

            public void FailUntilIncident(string modId = "tower")
            {
                for (int frame = 0; frame < 20 && Coordinator.Policy.PhaseOf(modId) == LuaModRepairPhase.Healthy; frame++)
                {
                    TickRuntime(1);
                }

                Assert.AreNotEqual(LuaModRepairPhase.Healthy, Coordinator.Policy.PhaseOf(modId),
                    "the failing mod opened an incident");
            }

            public void Dispose()
            {
                Coordinator.Dispose();
                Model.ReleaseHeld();
            }
        }

        /// <summary>
        /// A model that answers a repair turn by calling the real <c>manage_mods reload</c> with the code
        /// <see cref="Fix"/> returns (none: it changes nothing), then ends the turn as <see cref="EndWith"/> says.
        /// </summary>
        private sealed class ScriptedRepairModel : ILuaModRepairTurnRunner
        {
            private readonly LuaModsLlmTool _tool;
            private readonly List<(Action<LuaModRepairTurnEnd, string> Ended, Handle Handle)> _held = new();

            public ScriptedRepairModel(LuaModsLlmTool tool)
            {
                _tool = tool;
            }

            public List<LuaModRepairTurnRequest> Requests { get; } = new();

            public Func<LuaModRepairTurnRequest, string> Fix { get; set; } = _ => null;

            public LuaModRepairTurnEnd EndWith { get; set; } = LuaModRepairTurnEnd.Completed;

            public bool HoldTurns { get; set; }

            public bool IsModelAvailable { get; set; } = true;

            public bool IsBusy => false;

            public Task Pending { get; private set; } = Task.CompletedTask;

            public int Cancels { get; private set; }

            public ILuaModRepairTurnHandle TryStart(LuaModRepairTurnRequest request, Action<LuaModRepairTurnEnd, string> ended)
            {
                Requests.Add(request);
                Handle handle = new(this);
                if (HoldTurns)
                {
                    _held.Add((ended, handle));
                    return handle;
                }

                Pending = RunAsync(request, ended, handle);
                return handle;
            }

            public void ReleaseHeld(LuaModRepairTurnEnd end = LuaModRepairTurnEnd.Completed)
            {
                foreach ((Action<LuaModRepairTurnEnd, string> ended, Handle handle) in _held.ToArray())
                {
                    handle.Running = false;
                    ended(end, "");
                }

                _held.Clear();
            }

            private async Task RunAsync(LuaModRepairTurnRequest request, Action<LuaModRepairTurnEnd, string> ended,
                Handle handle)
            {
                string code = Fix(request);
                if (code != null)
                {
                    await _tool.ExecuteAsync("reload", request.ModId, code);
                }

                handle.Running = false;
                ended(EndWith, "");
            }

            private sealed class Handle : ILuaModRepairTurnHandle
            {
                private readonly ScriptedRepairModel _owner;

                public Handle(ScriptedRepairModel owner)
                {
                    _owner = owner;
                }

                public bool Running { get; set; } = true;

                public bool IsRunning => Running;

                public void Cancel()
                {
                    _owner.Cancels++;
                }
            }
        }

        /// <summary>A gate that runs every mutation at once, tells its observers, and records trigger and actor.</summary>
        private sealed class RecordingGate : IAttributedWorldMutationGate, IObservableWorldMutationGate
        {
            private readonly List<IRbxWorldMutationObserver> _observers = new();
            private long _sequence;

            public List<string> Triggers { get; } = new();

            public List<string> Actors { get; } = new();

            public Task<TResult> ExecuteAsync<TResult>(string trigger, Func<CancellationToken, Task<TResult>> mutationAsync,
                CancellationToken cancellationToken)
            {
                return ExecuteAsync(trigger, "", mutationAsync, cancellationToken);
            }

            public async Task<TResult> ExecuteAsync<TResult>(string trigger, string actorId,
                Func<CancellationToken, Task<TResult>> mutationAsync, CancellationToken cancellationToken)
            {
                RbxWorldMutationInfo info = new(trigger, actorId, ++_sequence);
                Triggers.Add(trigger);
                Actors.Add(actorId);
                foreach (IRbxWorldMutationObserver observer in _observers.ToArray())
                {
                    observer.OnMutationStarting(info);
                }

                bool completed = false;
                try
                {
                    TResult result = await mutationAsync(cancellationToken);
                    completed = true;
                    return result;
                }
                finally
                {
                    foreach (IRbxWorldMutationObserver observer in _observers.ToArray())
                    {
                        observer.OnMutationFinished(info, null, completed);
                    }
                }
            }

            public void AddObserver(IRbxWorldMutationObserver observer)
            {
                if (!_observers.Contains(observer))
                {
                    _observers.Add(observer);
                }
            }

            public void RemoveObserver(IRbxWorldMutationObserver observer)
            {
                _observers.Remove(observer);
            }
        }

        /// <summary>The chat feed: what the player was told.</summary>
        private sealed class RecordingMessages : IChatOverlayMessages
        {
            public List<string> Posted { get; } = new();

            public void PostSystemMessage(string text)
            {
                Posted.Add(text);
            }

            public void PostMessage(string speaker, string text, ChatFeedLineKind kind = ChatFeedLineKind.System)
            {
                Posted.Add(text);
            }
        }

        private sealed class SilentGameLogger : IGameLogger
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

    internal static class LuaLogEntryListExtensions
    {
        public static List<LuaLogEntry> FindAllAsList(this IReadOnlyList<LuaLogEntry> entries, Predicate<LuaLogEntry> match)
        {
            List<LuaLogEntry> found = new();
            foreach (LuaLogEntry entry in entries)
            {
                if (match(entry))
                {
                    found.Add(entry);
                }
            }

            return found;
        }
    }
}
