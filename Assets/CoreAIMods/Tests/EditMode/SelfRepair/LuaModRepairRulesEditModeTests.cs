using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Ai.Logging;
using CoreAI.Ai.LuaCs;
using CoreAI.Ai.SelfRepair;
using CoreAI.Authority;
using CoreAI.Composition;
using CoreAI.Mods.WorldPackages;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.SelfRepair
{
    /// <summary>
    /// The small rules the repair loop decides with: which backend is local and which is paid, the model's summary and
    /// the "it deleted the feature" signal, who put a source back, and the runtime's pause that keeps the mod.
    /// </summary>
    [TestFixture]
    public sealed class LuaModRepairRulesEditModeTests
    {
        [TestCase("http://localhost:1234/v1")]
        [TestCase("http://127.0.0.1:8080/v1")]
        [TestCase("http://[::1]:1234/v1")]
        [TestCase("http://192.168.1.20:1234/v1")]
        [TestCase("http://10.0.0.5/v1")]
        [TestCase("http://172.20.3.4:11434/v1")]
        [TestCase("http://gpu-box.local:1234/v1")]
        public void LocalUrls_AreLocal(string url)
        {
            Assert.IsTrue(LuaModRepairBackendCostRules.IsLocalUrl(url), url);
        }

        [TestCase("https://api.openai.com/v1")]
        [TestCase("https://openrouter.ai/api/v1")]
        [TestCase("http://8.8.8.8/v1")]
        [TestCase("http://172.32.0.1/v1")]
        [TestCase("http://localhost.example.com/v1")]
        [TestCase("")]
        [TestCase("not a url")]
        public void OtherUrls_AreNotLocal(string url)
        {
            Assert.IsFalse(LuaModRepairBackendCostRules.IsLocalUrl(url), url);
        }

        [Test]
        public void Settings_TellLocalFromPaid()
        {
            Assert.AreEqual(LuaModRepairBackendCost.Local,
                LuaModRepairBackendCostRules.Classify(LlmExecutionMode.LocalModel, "https://api.openai.com/v1"));
            Assert.AreEqual(LuaModRepairBackendCost.Local,
                LuaModRepairBackendCostRules.Classify(LlmExecutionMode.ClientOwnedApi, "http://localhost:1234/v1"),
                "LM Studio on this machine");
            Assert.AreEqual(LuaModRepairBackendCost.Paid,
                LuaModRepairBackendCostRules.Classify(LlmExecutionMode.ClientOwnedApi, "https://api.openai.com/v1"));
            Assert.AreEqual(LuaModRepairBackendCost.Paid,
                LuaModRepairBackendCostRules.Classify(LlmExecutionMode.ServerManagedApi, "http://localhost:5000/v1"),
                "a game backend that holds the provider key pays per request");
            Assert.AreEqual(LuaModRepairBackendCost.Paid,
                LuaModRepairBackendCostRules.Classify(LlmExecutionMode.Auto, "https://api.openai.com/v1"),
                "Auto may fall back to the paid endpoint");
            Assert.AreEqual(LuaModRepairBackendCost.Local,
                LuaModRepairBackendCostRules.Classify(LlmExecutionMode.Auto, "http://127.0.0.1:1234/v1"));
            Assert.AreEqual(LuaModRepairBackendCost.None,
                LuaModRepairBackendCostRules.Classify(LlmExecutionMode.Offline, ""));
        }

        [Test]
        public void ByBackend_RepairsByItselfOnlyWithALocalModel_AndExplicitModesStay()
        {
            Assert.AreEqual(LuaModRuntimeRepairMode.Automatic,
                LuaModRepairBackendCostRules.EffectiveMode(LuaModRuntimeRepairMode.ByBackend, LuaModRepairBackendCost.Local));
            Assert.AreEqual(LuaModRuntimeRepairMode.AskFirst,
                LuaModRepairBackendCostRules.EffectiveMode(LuaModRuntimeRepairMode.ByBackend, LuaModRepairBackendCost.Paid));
            Assert.AreEqual(LuaModRuntimeRepairMode.AskFirst,
                LuaModRepairBackendCostRules.EffectiveMode(LuaModRuntimeRepairMode.ByBackend, LuaModRepairBackendCost.Unknown));
            Assert.AreEqual(LuaModRuntimeRepairMode.Automatic,
                LuaModRepairBackendCostRules.EffectiveMode(LuaModRuntimeRepairMode.Automatic, LuaModRepairBackendCost.Paid));
            Assert.AreEqual(LuaModRuntimeRepairMode.Off,
                LuaModRepairBackendCostRules.EffectiveMode(LuaModRuntimeRepairMode.Off, LuaModRepairBackendCost.Local));
        }

        [Test]
        public void RoleRoute_IsLocalOnlyWhenEveryEndpointItMayReachIsLocal()
        {
            FakeRegistry registry = new();
            registry.Add("studio", LlmEndpointKind.HttpOpenAi, "http://localhost:1234/v1");
            registry.Add("cloud", LlmEndpointKind.HttpOpenAi, "https://api.openai.com/v1");
            registry.Add("gguf", LlmEndpointKind.LlmUnity, "");
            registry.Profile("local", "studio", "on-device");
            registry.Profile("on-device", "gguf");
            registry.Profile("mixed", "studio", "paid");
            registry.Profile("paid", "cloud");

            registry.Role = "local";
            Assert.AreEqual(LuaModRepairBackendCost.Local,
                LuaModRepairBackendCostRules.ClassifyRole(registry, BuiltInAgentRoleIds.Programmer));
            registry.Role = "mixed";
            Assert.AreEqual(LuaModRepairBackendCost.Paid,
                LuaModRepairBackendCostRules.ClassifyRole(registry, BuiltInAgentRoleIds.Programmer),
                "a paid fallback makes the route paid");
            registry.Role = "";
            Assert.AreEqual(LuaModRepairBackendCost.Unknown,
                LuaModRepairBackendCostRules.ClassifyRole(registry, BuiltInAgentRoleIds.Programmer));
        }

        [Test]
        public void Summary_IsTheChangedLine_PlainAndBounded()
        {
            Assert.AreEqual("The handler now checks that part exists.", LuaModRepairChangeReport.ExtractSummary(
                "I looked at it.\n**Changed:** the handler now checks that `part` exists.\n```lua\nlocal x\n```"));
            Assert.AreEqual("Guarded the nil part. Kept the tint.", LuaModRepairChangeReport.ExtractSummary(
                "<think>hm</think>Guarded the nil part. Kept the tint. Then reloaded and checked the logs."),
                "without the marker, the first two sentences");
            string longer = LuaModRepairChangeReport.ExtractSummary("Changed: " + string.Join(" ", new string[60]).Replace(" ", "word "));
            Assert.LessOrEqual(longer.Length, LuaModRepairChangeReport.MaximumSummaryCharacters);
            StringAssert.EndsWith("...", longer);
            Assert.AreEqual("", LuaModRepairChangeReport.ExtractSummary("```lua\nprint(1)\n```"));
            Assert.AreEqual("", LuaModRepairChangeReport.ExtractSummary(null));
        }

        [Test]
        public void Comparison_FlagsRemovedHandlersAndADrasticShrink_ButNotAnEqualFix()
        {
            string failing = "local a = 1\nlocal b = 2\nlocal c = 3\nlocal d = 4\nlocal e = 5\nlocal f = 6\n" +
                             "workspace.Part.Touched:Connect(function() end)\nhooks_every(1, function() end)\n";
            LuaModRepairSourceComparison shrunk = LuaModRepairChangeReport.Compare(failing, "-- gone\nlocal a = 1\n");
            LuaModRepairSourceComparison kept = LuaModRepairChangeReport.Compare(failing, failing.Replace("local f = 6", "local f = 7"));

            Assert.IsTrue(shrunk.HandlersRemoved);
            Assert.IsTrue(shrunk.DrasticallyShorter);
            Assert.AreEqual(8, shrunk.FailingLines);
            Assert.AreEqual(1, shrunk.RepairedLines, "comments do not count");
            Assert.IsFalse(kept.IsSuspicious);
            StringAssert.StartsWith("'m' runs without errors now, but the fix removed 2 of its 2 event handlers and kept 1 of 8 " +
                                    "lines of code;", LuaModRepairChangeReport.BuildFixedLine("m", "Removed it.", shrunk, ""));
            Assert.AreEqual("'m' is fixed; the AI says: Changed one value. /undo takes back this fix.",
                LuaModRepairChangeReport.BuildFixedLine("m", "Changed one value", kept,
                    LuaModRepairChangeReport.UndoWay(RbxWorldRepairRecording.OwnEntry)));
        }

        [Test]
        public void Tracker_MarksASourceAnUndoChanged_AndAnAiWriteClearsIt()
        {
            LuaModAuthorshipTracker tracker = new();
            tracker.OnMutationStarting(new RbxWorldMutationInfo(LuaModsLlmTool.LoadBackupTrigger, "chat", 7));
            tracker.NoteSourceLoaded("tower", "v1", "build a tower", out bool byAi);
            tracker.OnMutationFinished(new RbxWorldMutationInfo(LuaModsLlmTool.LoadBackupTrigger, "chat", 7), null, true);

            Assert.IsTrue(byAi);
            Assert.AreEqual(7L, tracker.AiSequenceFor("tower"));
            Assert.IsFalse(tracker.NoteSourcePutBack("tower", "v1", true), "the same source restarted is no change");
            Assert.IsFalse(tracker.IsPutBackByPlayer("tower"));
            Assert.IsTrue(tracker.NoteSourcePutBack("tower", "v0", true));
            Assert.IsTrue(tracker.IsPutBackByPlayer("tower"));
            Assert.AreEqual(0L, tracker.AiSequenceFor("tower"), "the AI did not write what runs now");

            tracker.OnMutationStarting(new RbxWorldMutationInfo(LuaModsLlmTool.ReloadBackupTrigger, "chat", 9));
            tracker.NoteSourceLoaded("tower", "v2", "", out _);
            Assert.IsFalse(tracker.IsPutBackByPlayer("tower"), "a new AI change is the AI's again");
            Assert.AreEqual(9L, tracker.AiSequenceFor("tower"));
        }

        [Test]
        public void Tracker_RaisesWholeWorldReplaced_ForUndoRedoAndWorldLoads_Only()
        {
            LuaModAuthorshipTracker tracker = new();
            List<string> seen = new();
            tracker.WholeWorldReplaced += seen.Add;

            foreach (string trigger in new[]
                     {
                         RbxWorldUndoService.UndoTrigger, LuaModsLlmTool.ReloadBackupTrigger, RbxWorldUndoService.RedoTrigger,
                         "execute_lua"
                     })
            {
                RbxWorldMutationInfo mutation = new(trigger, "chat", 1);
                tracker.OnMutationStarting(mutation);
                tracker.OnMutationFinished(mutation, null, true);
            }

            RbxWorldMutationInfo failed = new(RbxWorldUndoService.UndoTrigger, "chat", 2);
            tracker.OnMutationStarting(failed);
            tracker.OnMutationFinished(failed, null, false);

            CollectionAssert.AreEqual(new[] { RbxWorldUndoService.UndoTrigger, RbxWorldUndoService.RedoTrigger }, seen);
        }

        [Test]
        public async Task TryQuarantine_PausesTheMod_KeepsItLoaded_AndAReloadClearsIt()
        {
            using LuaModSelfRepairLoopEditModeTests.Harness harness = new();
            LuaCsModRuntime runtime = harness.Stack.Runtime;
            runtime.LoadMod(harness.Actor, "ticker", "hooks_every(0.05, function() print('tick') end)", LuaCapabilities.All, false);
            harness.TickRuntime(3);
            int before = Prints(harness);

            Assert.IsTrue(runtime.TryQuarantine(harness.Actor, "ticker", "test"));
            harness.TickRuntime(10);

            Assert.AreEqual(before, Prints(harness), "its timer no longer runs");
            Assert.IsTrue(runtime.IsLoaded(harness.Actor, "ticker"));
            Assert.IsTrue(runtime.TryGetModSource(harness.Actor, "ticker", out string source));
            StringAssert.Contains("print('tick')", source);
            Assert.IsFalse(runtime.TryQuarantine(harness.Actor, "ticker", "again"), "already paused");
            runtime.ReloadMod(harness.Actor, "ticker", source);
            harness.TickRuntime(3);
            Assert.Greater(Prints(harness), before, "a reload runs it again");
            await Task.CompletedTask;
        }

        [Test]
        public void TryQuarantine_RefusesAnUnknownMod_AndCallersWithoutHostAuthority()
        {
            using LuaModSelfRepairLoopEditModeTests.Harness harness = new();
            LuaCsModRuntime runtime = harness.Stack.Runtime;
            runtime.LoadMod(harness.Actor, "ticker", "local x = 1", LuaCapabilities.All, false);
            ActorContext restricted = new LocalActorIdentityProvider("guest").GetActorContext(BuiltInAgentRoleIds.Programmer);

            Assert.IsFalse(runtime.TryQuarantine(harness.Actor, "nobody", "test"));
            Assert.Throws<InvalidOperationException>(() => runtime.TryQuarantine(default, "ticker", "test"));
            Assert.Throws<UnauthorizedAccessException>(() => runtime.TryQuarantine(restricted, "ticker", "test"));
            Assert.IsFalse(IsQuarantined(harness, "ticker"));
        }

        private static int Prints(LuaModSelfRepairLoopEditModeTests.Harness harness)
        {
            int count = 0;
            foreach (LuaLogEntry entry in harness.Logs.Query(new LuaLogQuery { ModId = "ticker", MaxCount = 1000 }))
            {
                if (entry.Message == "tick")
                {
                    count++;
                }
            }

            return count;
        }

        private static bool IsQuarantined(LuaModSelfRepairLoopEditModeTests.Harness harness, string id)
        {
            foreach (LuaModInfo info in harness.Runtime.ListMods(harness.Actor))
            {
                if (info != null && info.Id == id)
                {
                    return info.Quarantined;
                }
            }

            return false;
        }

        /// <summary>An endpoint registry with only what the cost rule reads.</summary>
        private sealed class FakeRegistry : ILlmEndpointRegistry
        {
            private readonly List<LlmEndpointSnapshot> _endpoints = new();
            private readonly List<LlmRuntimeProfile> _profiles = new();

            public event Action Changed
            {
                add { }
                remove { }
            }

            public string Role { get; set; } = "";

            public void Add(string id, LlmEndpointKind kind, string baseUrl)
            {
                _endpoints.Add(new LlmEndpointSnapshot
                {
                    Descriptor = new LlmEndpointDescriptor { EndpointId = id, Kind = kind, BaseUrl = baseUrl }
                });
            }

            public void Profile(string id, string endpointId, params string[] fallbacks)
            {
                _profiles.Add(new LlmRuntimeProfile { ProfileId = id, EndpointId = endpointId, FallbackProfileIds = fallbacks });
            }

            public IReadOnlyList<LlmEndpointSnapshot> GetEndpoints()
            {
                return _endpoints;
            }

            public IReadOnlyList<LlmRuntimeProfile> GetProfiles()
            {
                return _profiles;
            }

            public string GetRoleProfile(string roleId)
            {
                return Role;
            }

            public Task<LlmEndpointSnapshot> AddOrUpdateEndpointAsync(LlmEndpointDescriptor descriptor,
                string sessionApiKey = null, CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public Task<LlmEndpointSnapshot> SetEndpointActiveAsync(string endpointId, bool active, bool keepWarm = false,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public Task<bool> RemoveEndpointAsync(string endpointId,
                LlmEndpointRemovalMode mode = LlmEndpointRemovalMode.Drain, string replacementEndpointId = null,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public void AddOrUpdateProfile(LlmRuntimeProfile profile)
            {
                throw new NotSupportedException();
            }

            public bool RemoveProfile(string profileId, string replacementProfileId = null)
            {
                throw new NotSupportedException();
            }

            public void AssignRoleProfile(string rolePattern, string profileId, int sortOrder = 0)
            {
                throw new NotSupportedException();
            }

            public bool ClearRoleProfile(string rolePattern)
            {
                throw new NotSupportedException();
            }
        }
    }
}
