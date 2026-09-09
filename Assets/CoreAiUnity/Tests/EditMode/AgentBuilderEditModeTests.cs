using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.AgentMemory;
using CoreAI.Ai;
using Microsoft.Extensions.AI;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// EditMode coverage for <see cref="AgentBuilder"/> custom-agent configuration.
    /// </summary>
    [TestFixture]
    public sealed class AgentBuilderEditModeTests
    {
        private string _savedUniversalPrefix;

        [SetUp]
        public void SetUp()
        {
            _savedUniversalPrefix = CoreAISettings.UniversalSystemPromptPrefix;
            CoreAISettings.UniversalSystemPromptPrefix = string.Empty;
            CoreAIAgent.Reset();
        }

        [TearDown]
        public void TearDown()
        {
            CoreAISettings.UniversalSystemPromptPrefix = _savedUniversalPrefix;
            CoreAIAgent.Reset();
        }

        [Test]
        public void Build_AppliesToPolicyByDefault_WhenPolicyIsRegistered()
        {
            AgentMemoryPolicy policy = new();
            CoreAIAgent.Initialize(null, policy, null);

            AgentConfig config = new AgentBuilder("PolicyAwareAgent")
                .WithSystemPrompt("You are a test agent.")
                .WithMemory()
                .Build();

            Assert.IsTrue(policy.HasRole("PolicyAwareAgent"),
                "Build() should register role config on CoreAIAgent.Policy when it exists.");
            Assert.AreEqual(1, policy.GetToolsForRole("PolicyAwareAgent").Count);
            Assert.AreEqual("memory", config.Tools[0].Name);
        }

        [Test]
        public void BuildDetached_DoesNotMutateGlobalPolicy()
        {
            AgentMemoryPolicy policy = new();
            CoreAIAgent.Initialize(null, policy, null);

            _ = new AgentBuilder("DetachedAgent")
                .WithSystemPrompt("You are a test agent.")
                .WithMemory()
                .BuildDetached();

            Assert.IsFalse(policy.HasRole("DetachedAgent"),
                "BuildDetached() should not add role config to CoreAIAgent.Policy.");
        }

        [Test]
        public async Task AskAsync_Fails_WhenCoreAiNotInitialized()
        {
            // With auto-registration, an unregistered role no longer fails on "not registered" — the first
            // Ask registers it into the global policy. It still fails when CoreAI itself was never
            // initialized (no policy), with a message that points at the missing lifetime scope.
            CoreAIAgent.Reset();
            AgentConfig config = new AgentBuilder("UnregisteredAgent")
                .WithSystemPrompt("You are unregistered.")
                .BuildDetached();

            InvalidOperationException ex = null;
            try
            {
                await config.AskAsync("Hello");
            }
            catch (InvalidOperationException e)
            {
                ex = e;
            }

            Assert.NotNull(ex, "expected InvalidOperationException");
            StringAssert.Contains("Initialize CoreAI", ex.Message);
        }

        [Test]
        public void AskWithCallback_DoesNotThrow_AndLegacyAskIsObsolete()
        {
            CoreAIAgent.Reset();
            AgentConfig config = new AgentBuilder("CallbackAgent")
                .WithSystemPrompt("You are a callback agent.")
                .BuildDetached();

            // Fire-and-forget convenience must never throw into the caller; failures are logged.
            // Silence the logger so the expected "not initialized" error does not trip Unity's
            // log assertions in EditMode.
            Logging.ILog savedLog = Logging.Log.Instance;
            Logging.Log.Instance = Logging.NullLog.Instance;
            try
            {
                Assert.DoesNotThrow(() => config.AskWithCallback("Hello", _ => { }));
            }
            finally
            {
                Logging.Log.Instance = savedLog;
            }

            MethodInfo askMethod = typeof(AgentConfigExtensions).GetMethod(nameof(AgentConfigExtensions.Ask));
            Assert.IsNotNull(askMethod);
            Assert.IsTrue(askMethod.IsDefined(typeof(ObsoleteAttribute), false),
                "Ask(callback) is a legacy alias and must carry [Obsolete] pointing to AskAsync/AskWithCallback.");
        }

        [Test]
        public void Builder_CreatesBasicAgent_WithDefaults()
        {
            AgentConfig config = new AgentBuilder("TestAgent")
                .WithSystemPrompt("You are a test agent.")
                .Build();

            Assert.AreEqual("TestAgent", config.RoleId);
            Assert.AreEqual("You are a test agent.", config.SystemPrompt);
            Assert.AreEqual(0, config.Tools.Count);
            Assert.AreEqual(AgentMode.ToolsAndChat, config.Mode);
            Assert.IsTrue(config.UseLlmContextCompaction,
                "Smart compaction should default on for AgentBuilder agents.");
        }

        [Test]
        public void Builder_WithLlmContextCompaction_OverridesDefault()
        {
            AgentConfig config = new AgentBuilder("NoSmart")
                .WithSystemPrompt("x")
                .WithLlmContextCompaction(false)
                .Build();
            Assert.IsFalse(config.UseLlmContextCompaction);
        }

        [Test]
        public void Builder_AddsTools_Correctly()
        {
            AgentConfig config = new AgentBuilder("ToolAgent")
                .WithSystemPrompt("You use tools.")
                .WithTool(new MemoryLlmTool())
                .Build();

            Assert.AreEqual(1, config.Tools.Count);
            Assert.AreEqual("memory", config.Tools[0].Name);
        }

        [Test]
        public void Builder_AddsMultipleTools_Correctly()
        {
            AgentConfig config = new AgentBuilder("MultiToolAgent")
                .WithSystemPrompt("You use many tools.")
                .WithTool(new MemoryLlmTool())
                .WithTool(new MemoryLlmTool())
                .WithTool(new MemoryLlmTool())
                .Build();

            Assert.AreEqual(3, config.Tools.Count);
        }

        [Test]
        public void Builder_WithMemory_AddsMemoryTool()
        {
            AgentConfig config = new AgentBuilder("MemoryAgent")
                .WithSystemPrompt("You remember things.")
                .WithMemory()
                .Build();

            Assert.AreEqual(1, config.Tools.Count);
            Assert.AreEqual("memory", config.Tools[0].Name);
        }

        [Test]
        public void Builder_WithWaitTool_AddsWaitTool()
        {
            AgentConfig config = new AgentBuilder("WaitAgent")
                .WithSystemPrompt("You can wait for external state.")
                .WithWaitTool(5d)
                .Build();

            Assert.AreEqual(1, config.Tools.Count);
            Assert.AreEqual("wait", config.Tools[0].Name);
            Assert.IsTrue(config.Tools[0].AllowDuplicates);
        }

        [Test]
        public void Builder_WithMode_SetsCorrectMode()
        {
            AgentConfig toolsOnly = new AgentBuilder("ToolsOnly")
                .WithMode(AgentMode.ToolsOnly)
                .Build();

            AgentConfig toolsAndChat = new AgentBuilder("ToolsAndChat")
                .WithMode(AgentMode.ToolsAndChat)
                .Build();

            AgentConfig chatOnly = new AgentBuilder("ChatOnly")
                .WithMode(AgentMode.ChatOnly)
                .Build();

            Assert.AreEqual(AgentMode.ToolsOnly, toolsOnly.Mode);
            Assert.AreEqual(AgentMode.ToolsAndChat, toolsAndChat.Mode);
            Assert.AreEqual(AgentMode.ChatOnly, chatOnly.Mode);
        }

        [Test]
        public void Builder_FullConfiguration_AllFieldsSet()
        {
            AgentConfig config = new AgentBuilder("FullAgent")
                .WithSystemPrompt("You are a full configured agent.")
                .WithMemory(MemoryToolAction.Append)
                .WithMode(AgentMode.ToolsAndChat)
                .Build();

            Assert.AreEqual("FullAgent", config.RoleId);
            Assert.AreEqual("You are a full configured agent.", config.SystemPrompt);
            Assert.AreEqual(1, config.Tools.Count);
            Assert.AreEqual(AgentMode.ToolsAndChat, config.Mode);
        }

        [Test]
        public void Config_ApplyToPolicy_SetsToolsOnPolicy()
        {
            AgentMemoryPolicy policy = new();

            AgentConfig config = new AgentBuilder("PolicyAgent")
                .WithSystemPrompt("Test")
                .WithMemory()
                .Build();

            config.ApplyToPolicy(policy);

            IReadOnlyList<ILlmTool> tools = policy.GetToolsForRole("PolicyAgent");
            Assert.Greater(tools.Count, 0, "Should have tools after ApplyToPolicy");
        }

        [Test]
        public void Builder_ChainCalls_ReturnsSameBuilder()
        {
            AgentBuilder builder = new("ChainAgent");

            AgentBuilder result1 = builder.WithSystemPrompt("Test");
            AgentBuilder result2 = builder.WithMemory();
            AgentBuilder result3 = builder.WithMode(AgentMode.ChatOnly);

            Assert.AreSame(builder, result1);
            Assert.AreSame(builder, result2);
            Assert.AreSame(builder, result3);
        }

        [Test]
        public void Builder_WithLlmProfile_TrimsAndStoresProfile()
        {
            AgentConfig config = new AgentBuilder("RoutedAgent")
                .WithLlmProfile("  local-magic  ")
                .BuildDetached();

            Assert.AreEqual("local-magic", config.LlmProfileId);
        }

        [Test]
        public void ApplyToPolicy_ToolsAndChat_DefaultsStreamingOverrideToTrue()
        {
            AgentMemoryPolicy policy = new();
            AgentConfig config = new AgentBuilder("ChatWithToolsRole")
                .WithMode(AgentMode.ToolsAndChat)
                .Build();

            config.ApplyToPolicy(policy);

            Assert.IsTrue(policy.TryGetStreamingOverride("ChatWithToolsRole", out bool enabled));
            Assert.IsTrue(enabled);
        }

        [Test]
        public void ApplyToPolicy_ChatOnly_LeavesStreamingOnGlobalFallback()
        {
            AgentMemoryPolicy policy = new();
            AgentConfig config = new AgentBuilder("ChatOnlyRole")
                .WithMode(AgentMode.ChatOnly)
                .Build();

            config.ApplyToPolicy(policy);

            Assert.IsFalse(policy.TryGetStreamingOverride("ChatOnlyRole", out _));
        }

        [Test]
        public void ApplyToPolicy_ExplicitWithStreamingFalse_WinsOverModeDefault()
        {
            AgentMemoryPolicy policy = new();
            AgentConfig config = new AgentBuilder("ExplicitOffRole")
                .WithMode(AgentMode.ToolsAndChat)
                .WithStreaming(false)
                .Build();

            config.ApplyToPolicy(policy);

            Assert.IsTrue(policy.TryGetStreamingOverride("ExplicitOffRole", out bool enabled));
            Assert.IsFalse(enabled);
        }

        [Test]
        public void ValidateOnBuild_CustomRole_WithoutSystemPrompt_ReportsMissingPrompt()
        {
            AgentBuilder builder = new("CustomNpc")
            {
                SuppressBuildWarnings = true
            };
            IReadOnlyList<AgentBuilderIssue> issues = builder.ValidateOnBuild();

            Assert.That(issues.Any(i => i.Code == AgentBuilderIssueCode.MissingSystemPrompt), Is.True);
        }

        [Test]
        public void ValidateOnBuild_CustomRole_WithPerRequestSystemPrompt_SkipsMissingPrompt()
        {
            AgentBuilder builder = new("CustomNpc")
            {
                SuppressBuildWarnings = true
            };
            builder.WithPerRequestSystemPrompt();

            IReadOnlyList<AgentBuilderIssue> issues = builder.ValidateOnBuild();

            Assert.That(issues.Any(i => i.Code == AgentBuilderIssueCode.MissingSystemPrompt), Is.False);
        }

        [Test]
        public void ValidateOnBuild_BuiltInRole_WithoutSystemPrompt_SkipsMissingPrompt()
        {
            AgentBuilder builder = new(BuiltInAgentRoleIds.Creator)
            {
                SuppressBuildWarnings = true
            };
            IReadOnlyList<AgentBuilderIssue> issues = builder.ValidateOnBuild();

            Assert.That(issues.Any(i => i.Code == AgentBuilderIssueCode.MissingSystemPrompt), Is.False);
        }

        [Test]
        public void ValidateOnBuild_ToolsOnlyWithoutTools_ReportsNoTools()
        {
            AgentBuilder builder = new("Npc")
            {
                SuppressBuildWarnings = true
            };
            builder.WithMode(AgentMode.ToolsOnly);

            IReadOnlyList<AgentBuilderIssue> issues = builder.ValidateOnBuild();

            Assert.That(issues.Any(i => i.Code == AgentBuilderIssueCode.NoToolsForToolMode), Is.True);
        }

        [Test]
        public void ValidateOnBuild_CompactionTrue_GlobalGateOff_ReportsCompactionGate()
        {
            try
            {
                // Ensure static override is deterministic for the test body
                CoreAISettings.ResetOverrides();
                CoreAISettings.EnableLlmContextCompaction = false;

                AgentBuilder builder = new("CompactionRole")
                {
                    SuppressBuildWarnings = true
                };
                builder.WithSystemPrompt("x");
                builder.WithMode(AgentMode.ChatOnly);
                builder.WithLlmContextCompaction(true);

                IReadOnlyList<AgentBuilderIssue> issues = builder.ValidateOnBuild();

                Assert.That(issues.Any(i => i.Code == AgentBuilderIssueCode.CompactionGateDisabled), Is.True);
            }
            finally
            {
                CoreAISettings.ResetOverrides();
            }
        }

        [Test]
        public async Task ApplyToPolicyAsync_WithAuthoring_PublishesRoleOnlyAfterHydration()
        {
            GatedAsyncSkillStore store = new();
            store.GateListAsync = true;
            store.Seed(new SkillRecord("gated-skill", "Gated skill", "Do gated things.",
                new[] { "memory" }));
            AgentMemoryPolicy policy = new();
            AgentConfig config = new AgentBuilder("GatedRole") { SuppressBuildWarnings = true }
                .WithMemory()
                .WithSkillAuthoring(store)
                .BuildDetached();

            Task apply = config.ApplyToPolicyAsync(policy);
            await CompletesPromptly(store.ListEntered);
            Assert.IsFalse(apply.IsCompleted, "Apply must wait for store hydration.");
            Assert.IsFalse(policy.HasRole("GatedRole"),
                "Role must stay unready until asynchronous hydration completes.");

            store.ReleaseList();
            await apply;

            Assert.IsTrue(policy.HasRole("GatedRole"),
                "Role must be ready after hydration completes.");
            Assert.IsTrue(HasSkill(policy, "GatedRole", "gated-skill"),
                "Hydrated store skill must be visible through the policy.");
            Assert.AreEqual(1, store.ListAsyncCalls, "Hydration must run exactly once.");
            Assert.AreEqual(0, store.SyncListCalls,
                "The async apply path must not use synchronous hydration.");
        }

        [Test]
        public async Task AskAsync_ConcurrentFirstAsk_HydratesStoreOnce()
        {
            GatedAsyncSkillStore store = new();
            store.GateListAsync = true;
            store.Seed(new SkillRecord("shared-skill", "Shared skill", "Do shared things.",
                new[] { "memory" }));
            AgentMemoryPolicy policy = new();
            StubOrchestrationService orch = new();
            CoreAIAgent.Initialize(orch, policy, null);
            try
            {
                AgentConfig config = new AgentBuilder("SingleHydrateRole") { SuppressBuildWarnings = true }
                    .WithMemory()
                    .WithSkillAuthoring(store)
                    .BuildDetached();

                List<Task<string>> asks = new();
                for (int i = 0; i < 8; i++)
                {
                    asks.Add(config.AskAsync(orch, "hello " + i, 0, CancellationToken.None));
                }

                await CompletesPromptly(store.ListEntered);
                Assert.IsFalse(policy.HasRole("SingleHydrateRole"),
                    "Concurrent first asks must wait for the shared hydration.");
                Assert.AreEqual(0, orch.Calls, "No orchestration may run before the role is ready.");

                store.ReleaseList();
                string[] results = await Task.WhenAll(asks);

                Assert.AreEqual(8, orch.Calls, "Every waiter must reach orchestration after readiness.");
                Assert.AreEqual(1, store.ListAsyncCalls,
                    "Concurrent first asks must share a single hydration.");
                foreach (string result in results)
                {
                    StringAssert.Contains("SingleHydrateRole", result);
                }
            }
            finally
            {
                CoreAIAgent.Reset();
            }
        }

        [Test]
        public async Task ApplyToPolicyAsync_FailedReplacement_PreservesPreviousRole()
        {
            GatedAsyncSkillStore good = new();
            good.Seed(new SkillRecord("v1-skill", "V1 skill", "Do v1 things.",
                new[] { "memory" }));
            AgentMemoryPolicy policy = new();
            AgentConfig first = new AgentBuilder("ReplaceRole") { SuppressBuildWarnings = true }
                .WithSystemPrompt("First prompt.")
                .WithMemory()
                .WithSkillAuthoring(good)
                .BuildDetached();
            await first.ApplyToPolicyAsync(policy);
            int toolsBefore = policy.GetToolsForRole("ReplaceRole").Count;
            Assert.Greater(toolsBefore, 0, "First apply must publish tools.");
            Assert.IsTrue(policy.TryGetAdditionalSystemPrompt("ReplaceRole", out string promptBefore));

            GatedAsyncSkillStore bad = new(new InvalidOperationException("Simulated hydration failure."));
            AgentConfig second = new AgentBuilder("ReplaceRole") { SuppressBuildWarnings = true }
                .WithSystemPrompt("Second prompt.")
                .WithMemory()
                .WithSkillAuthoring(bad)
                .BuildDetached();
            Exception fault = null;
            try
            {
                await second.ApplyToPolicyAsync(policy);
            }
            catch (Exception ex)
            {
                fault = ex;
            }

            Assert.NotNull(fault, "Failed hydration must surface instead of publishing a partial role.");
            Assert.IsTrue(policy.HasRole("ReplaceRole"), "Previous ready role must stay registered.");
            Assert.AreEqual(toolsBefore, policy.GetToolsForRole("ReplaceRole").Count,
                "Failed replacement must preserve the previous tool list.");
            Assert.IsTrue(policy.TryGetAdditionalSystemPrompt("ReplaceRole", out string promptAfter));
            Assert.AreEqual(promptBefore, promptAfter,
                "Failed replacement must preserve the previous prompt.");
            Assert.IsTrue(HasSkill(policy, "ReplaceRole", "v1-skill"),
                "Failed replacement must preserve the previous skills.");
        }

        [Test]
        public async Task ApplyToPolicyAsync_CanceledReplacement_PreservesPreviousRoleAndStaysRetryable()
        {
            GatedAsyncSkillStore good = new();
            good.Seed(new SkillRecord("v1-skill", "V1 skill", "Do v1 things.",
                new[] { "memory" }));
            AgentMemoryPolicy policy = new();
            AgentConfig first = new AgentBuilder("RetryRole") { SuppressBuildWarnings = true }
                .WithMemory()
                .WithSkillAuthoring(good)
                .BuildDetached();
            await first.ApplyToPolicyAsync(policy);
            int toolsBefore = policy.GetToolsForRole("RetryRole").Count;

            GatedAsyncSkillStore gated = new();
            gated.GateListAsync = true;
            AgentConfig second = new AgentBuilder("RetryRole") { SuppressBuildWarnings = true }
                .WithMemory()
                .WithSkillAuthoring(gated)
                .BuildDetached();
            using (CancellationTokenSource quitter = new())
            {
                Task apply = second.ApplyToPolicyAsync(policy, quitter.Token);
                await CompletesPromptly(gated.ListEntered);
                quitter.Cancel();
                bool canceled = false;
                try
                {
                    await CompletesPromptly(apply);
                }
                catch (OperationCanceledException)
                {
                    canceled = true;
                }

                Assert.IsTrue(canceled, "A canceled replacement must surface cancellation.");
            }

            Assert.IsTrue(policy.HasRole("RetryRole"), "Previous ready role must stay registered.");
            Assert.AreEqual(toolsBefore, policy.GetToolsForRole("RetryRole").Count,
                "Canceled replacement must preserve the previous tool list.");
            Assert.IsTrue(HasSkill(policy, "RetryRole", "v1-skill"),
                "Canceling hydration must preserve the old catalog, not just an equal tool count.");

            GatedAsyncSkillStore recovery = new();
            recovery.Seed(new SkillRecord("v2-skill", "V2 skill", "Do v2 things.",
                new[] { "memory" }));
            AgentConfig third = new AgentBuilder("RetryRole") { SuppressBuildWarnings = true }
                .WithMemory()
                .WithSkillAuthoring(recovery)
                .BuildDetached();
            await third.ApplyToPolicyAsync(policy);

            Assert.IsTrue(HasSkill(policy, "RetryRole", "v2-skill"),
                "A canceled initialization must stay retryable.");
        }

        [Test]
        public async Task AskAsync_WaiterCancel_DoesNotPoisonOtherCallers()
        {
            GatedAsyncSkillStore store = new();
            store.GateListAsync = true;
            store.Seed(new SkillRecord("waiter-skill", "Waiter skill", "Do waiter things.",
                new[] { "memory" }));
            AgentMemoryPolicy policy = new();
            StubOrchestrationService orch = new();
            CoreAIAgent.Initialize(orch, policy, null);
            try
            {
                AgentConfig config = new AgentBuilder("WaiterRole") { SuppressBuildWarnings = true }
                    .WithMemory()
                    .WithSkillAuthoring(store)
                    .BuildDetached();

                Task<string> worker;
                using (CancellationTokenSource quitter = new())
                {
                    Task<string> waiter = config.AskAsync(orch, "waiter", 0, quitter.Token);
                    worker = config.AskAsync(orch, "worker", 0, CancellationToken.None);
                    await CompletesPromptly(store.ListEntered);
                    quitter.Cancel();
                    bool waiterCanceled = false;
                    try
                    {
                        await CompletesPromptly(waiter);
                    }
                    catch (OperationCanceledException)
                    {
                        waiterCanceled = true;
                    }

                    Assert.IsTrue(waiterCanceled, "Canceling one waiter must cancel only that waiter.");
                    Assert.AreEqual(0, orch.Calls, "No orchestration may run before the role is ready.");
                }

                store.ReleaseList();
                string result = await worker;
                StringAssert.Contains("WaiterRole", result);

                int reads = store.ListAsyncCalls;
                string again = await config.AskAsync(orch, "again", 0, CancellationToken.None);
                StringAssert.Contains("WaiterRole", again);
                Assert.AreEqual(reads, store.ListAsyncCalls,
                    "A ready role must reuse the existing registration without rereading storage.");
            }
            finally
            {
                CoreAIAgent.Reset();
            }
        }

        [Test]
        public async Task AskAsync_RepeatedReadyAsk_DoesNotRereadStore()
        {
            GatedAsyncSkillStore store = new();
            store.Seed(new SkillRecord("ready-skill", "Ready skill", "Do ready things.",
                new[] { "memory" }));
            AgentMemoryPolicy policy = new();
            StubOrchestrationService orch = new();
            CoreAIAgent.Initialize(orch, policy, null);
            try
            {
                AgentConfig config = new AgentBuilder("ReadyRole") { SuppressBuildWarnings = true }
                    .WithMemory()
                    .WithSkillAuthoring(store)
                    .BuildDetached();

                await config.AskAsync(orch, "first", 0, CancellationToken.None);
                Assert.AreEqual(1, store.ListAsyncCalls, "First ask hydrates the role once.");

                await config.AskAsync(orch, "second", 0, CancellationToken.None);
                await config.AskAsync("third");

                Assert.AreEqual(1, store.ListAsyncCalls,
                    "Repeated ready asks must not reread storage.");
                Assert.AreEqual(0, store.SyncListCalls,
                    "Repeated ready asks must not fall back to synchronous hydration.");
                Assert.AreEqual(3, orch.Calls, "Every ask must still reach orchestration.");
            }
            finally
            {
                CoreAIAgent.Reset();
            }
        }

        [Test]
        public async Task ApplyToPolicyAsync_SameCatalogObservedByPolicyAndAgent()
        {
            GatedAsyncSkillStore store = new();
            store.Seed(new SkillRecord("catalog-skill", "Catalog skill", "Do catalog things.",
                new[] { "memory" }));
            AgentMemoryPolicy policy = new();
            AgentConfig config = new AgentBuilder("CatalogRole") { SuppressBuildWarnings = true }
                .WithMemory()
                .WithSkillAuthoring(store)
                .BuildDetached();
            await config.ApplyToPolicyAsync(policy);

            Assert.IsTrue(HasSkill(policy, "CatalogRole", "catalog-skill"),
                "Policy lookup must serve the hydrated skill.");

            policy.AddSkillForRole("CatalogRole",
                SkillSet.FromTextContent("extra-skill", "Extra skill", "Extra instructions."));
            Assert.IsTrue(HasSkill(policy, "CatalogRole", "extra-skill"),
                "Policy lookup must serve skills added after publication.");

            ILlmTool readSkill = null;
            foreach (ILlmTool tool in policy.GetToolsForRole("CatalogRole"))
            {
                if (string.Equals(tool.Name, "read_skill", StringComparison.OrdinalIgnoreCase))
                {
                    readSkill = tool;
                }
            }

            Assert.NotNull(readSkill, "Agent must keep the read_skill proxy after publication.");
            IAIFunctionLlmTool readable = readSkill as IAIFunctionLlmTool;
            Assert.NotNull(readable, "read_skill proxy must expose a function binding.");
            AIFunction function = readable.CreateAIFunction();
            AIFunctionArguments arguments = new() { { "skill_name", "extra-skill" } };
            object result = await function.InvokeAsync(arguments, CancellationToken.None);
            string json = result?.ToString();
            Assert.NotNull(json, "read_skill must answer with skill JSON.");
            StringAssert.Contains("extra-skill", json);
            StringAssert.Contains("Extra instructions.", json);
        }

        [Test]
        public async Task ApplyToPolicyAsync_PreCanceledOwner_DoesNotBlockNextReplacement()
        {
            AgentMemoryPolicy policy = new();
            GatedAsyncSkillStore store = new();
            AgentConfig config = new AgentBuilder("PreCanceled") { SuppressBuildWarnings = true }
                .WithSkillAuthoring(store).BuildDetached();
            using (CancellationTokenSource canceled = new())
            {
                canceled.Cancel();
                Assert.That(async () =>
                    await config.ApplyToPolicyAsync(policy, canceled.Token), Throws.InstanceOf<OperationCanceledException>());
            }

            Assert.IsFalse(policy.HasRole(config.RoleId));
            Assert.AreEqual(0, store.ListAsyncCalls, "Pre-cancel must not access storage.");
            await CompletesPromptly(config.ApplyToPolicyAsync(policy));
            Assert.IsTrue(policy.HasRole(config.RoleId), "Cancellation must not leave an owned gate behind.");
        }

        [Test]
        public async Task ApplyToPolicyAsync_CancellationInsidePreparation_ReleasesOwnership()
        {
            AgentMemoryPolicy policy = new();
            GatedAsyncSkillStore store = new();
            AgentConfig config = new AgentBuilder("RaceCanceled") { SuppressBuildWarnings = true }
                .WithSkillAuthoring(store).BuildDetached();
            using (CancellationTokenSource canceled = new())
            {
                store.OnListEntered = canceled.Cancel;
                Assert.That(async () =>
                    await config.ApplyToPolicyAsync(policy, canceled.Token), Throws.InstanceOf<OperationCanceledException>());
            }

            Assert.IsFalse(policy.HasRole(config.RoleId));
            store.OnListEntered = null;
            await CompletesPromptly(config.ApplyToPolicyAsync(policy));
            Assert.IsTrue(policy.HasRole(config.RoleId));
        }

        [Test]
        public async Task ApplyToPolicyAsync_QueuedReplacements_RemainOrderedAfterWaiterCancellation()
        {
            AgentMemoryPolicy policy = new();
            GatedAsyncSkillStore blocked = new() { GateListAsync = true };
            GatedAsyncSkillStore last = new();
            AgentConfig first = new AgentBuilder("OrderedRole") { SuppressBuildWarnings = true }
                .WithSystemPrompt("First config").WithSkillAuthoring(blocked).BuildDetached();
            AgentConfig final = new AgentBuilder("OrderedRole") { SuppressBuildWarnings = true }
                .WithSystemPrompt("Final config").WithSkillAuthoring(last).BuildDetached();
            Task firstApply = first.ApplyToPolicyAsync(policy);
            await CompletesPromptly(blocked.ListEntered);
            using (CancellationTokenSource canceled = new())
            {
                Task rejected = final.ApplyToPolicyAsync(policy, canceled.Token);
                canceled.Cancel();
                Assert.That(async () => await CompletesPromptly(rejected), Throws.InstanceOf<OperationCanceledException>());
                Task finalApply = final.ApplyToPolicyAsync(policy);
                Assert.AreEqual(0, last.ListAsyncCalls,
                    "A canceled waiter must not release another owner's registration.");
                blocked.ReleaseList();
                await CompletesPromptly(Task.WhenAll(firstApply, finalApply));
            }

            Assert.IsTrue(policy.TryGetAdditionalSystemPrompt(final.RoleId, out string prompt));
            Assert.AreEqual(final.SystemPrompt, prompt, "Explicit apply must run after the prior owner.");
        }

        [Test]
        public async Task AskAsync_FailedFirstHydration_CanRetryWithAnotherConfig()
        {
            AgentMemoryPolicy policy = new();
            StubOrchestrationService orchestrator = new();
            CoreAIAgent.Initialize(orchestrator, policy, null);
            GatedAsyncSkillStore bad = new(new InvalidOperationException("Expected storage failure"));
            AgentConfig first = new AgentBuilder("FailedFirst") { SuppressBuildWarnings = true }
                .WithSkillAuthoring(bad).BuildDetached();
            Assert.ThrowsAsync<InvalidOperationException>(async () => await first.AskAsync("first"));
            Assert.IsFalse(policy.HasRole(first.RoleId));
            AgentConfig retry = new AgentBuilder(first.RoleId) { SuppressBuildWarnings = true }
                .WithSkillAuthoring(new GatedAsyncSkillStore()).BuildDetached();
            await CompletesPromptly(retry.AskAsync("retry"));
            Assert.AreEqual(1, orchestrator.Calls, "Only the successful ready role may reach orchestration.");
        }

        [Test]
        public async Task BuildAsync_HydratesBeforeReturning_AndPreservesRuntimeProvider()
        {
            AgentMemoryPolicy policy = new();
            CoreAIAgent.Initialize(null, policy, null);
            GatedAsyncSkillStore store = new() { GateListAsync = true };
            ConstantRuntimeProvider provider = new();
            policy.SetRuntimeContextProvider("BuildAsyncRole", provider);
            Task<AgentConfig> build = new AgentBuilder("BuildAsyncRole") { SuppressBuildWarnings = true }
                .WithSkillAuthoring(store).BuildAsync();
            await CompletesPromptly(store.ListEntered);
            Assert.IsFalse(build.IsCompleted);
            Assert.IsFalse(policy.HasRole("BuildAsyncRole"));
            store.ReleaseList();
            await CompletesPromptly(build);
            Assert.IsTrue(policy.HasRole("BuildAsyncRole"));
            Assert.IsTrue(policy.TryGetRuntimeContextProvider("BuildAsyncRole", out IAgentRuntimeContextProvider after));
            Assert.AreSame(provider, after, "Role publication must preserve unrelated context providers.");
        }

        [Test]
        public async Task ApplyToPolicyAsync_WhitespaceAliases_SerializeOneRole()
        {
            AgentMemoryPolicy policy = new();
            GatedAsyncSkillStore blocked = new() { GateListAsync = true };
            GatedAsyncSkillStore replacement = new();
            AgentConfig first = new AgentBuilder(" WhitespaceRole ") { SuppressBuildWarnings = true }
                .WithSkillAuthoring(blocked).BuildDetached();
            AgentConfig next = new AgentBuilder("WhitespaceRole") { SuppressBuildWarnings = true }
                .WithSkillAuthoring(replacement).BuildDetached();
            Task firstApply = first.ApplyToPolicyAsync(policy);
            await CompletesPromptly(blocked.ListEntered);
            Task nextApply = next.ApplyToPolicyAsync(policy);
            try
            {
                Assert.AreEqual(0, replacement.ListAsyncCalls,
                    "Whitespace aliases must not prepare competing generations concurrently.");
            }
            finally
            {
                blocked.ReleaseList();
                await CompletesPromptly(Task.WhenAll(firstApply, nextApply));
            }
        }

        [Test]
        public async Task AskAsync_CaseDistinctRoles_DoNotShareReadiness()
        {
            AgentMemoryPolicy policy = new();
            StubOrchestrationService orchestrator = new();
            CoreAIAgent.Initialize(orchestrator, policy, null);
            GatedAsyncSkillStore upperStore = new() { GateListAsync = true };
            GatedAsyncSkillStore lowerStore = new();
            AgentConfig upper = new AgentBuilder("CaseRole") { SuppressBuildWarnings = true }
                .WithSkillAuthoring(upperStore).BuildDetached();
            AgentConfig lower = new AgentBuilder("caserole") { SuppressBuildWarnings = true }
                .WithSkillAuthoring(lowerStore).BuildDetached();
            Task upperAsk = upper.AskAsync("upper");
            await CompletesPromptly(upperStore.ListEntered);
            Task lowerAsk = lower.AskAsync("lower");
            try
            {
                await CompletesPromptly(lowerAsk);
                Assert.IsTrue(policy.HasRole(lower.RoleId),
                    "Readiness must use the policy's case-sensitive role identity.");
                Assert.IsFalse(policy.HasRole(upper.RoleId));
                Assert.AreEqual(1, orchestrator.Calls);
            }
            finally
            {
                upperStore.ReleaseList();
                await CompletesPromptly(Task.WhenAll(upperAsk, lowerAsk));
            }
        }

        [Test]
        public async Task ApplyToPolicyAsync_ExplicitSkillReplacement_RemovesOldToolAuthority()
        {
            AgentMemoryPolicy policy = new();
            int removedCalls = 0;
            DelegateLlmTool removed = new("removed_action", "Old phase action",
                (Action)(() => removedCalls++));
            SkillSet oldSkill = new("old-phase", "Old phase", "Use the old action.", removed);
            AgentConfig first = new AgentBuilder("PhaseRole") { SuppressBuildWarnings = true }
                .WithSkill(oldSkill).BuildDetached();
            await first.ApplyToPolicyAsync(policy);
            IResolvedLlmToolCallProvider firstProxy = policy.GetToolsForRole(first.RoleId)
                .OfType<IResolvedLlmToolCallProvider>().Single();
            Dictionary<string, object> arguments = new()
            {
                { "tool_name", removed.Name }, { "arguments_json", "{}" }
            };
            Assert.IsTrue(firstProxy.TryResolveInvocation(arguments,
                out ResolvedLlmToolInvocation before, out string initialError), initialError);
            await before.InvokeAsync(CancellationToken.None);
            Assert.AreEqual(1, removedCalls, "The original generation must prove its tool is executable.");

            AgentConfig replacement = new AgentBuilder(first.RoleId) { SuppressBuildWarnings = true }
                .WithSkillAuthoring(new GatedAsyncSkillStore()).BuildDetached();
            await replacement.ApplyToPolicyAsync(policy);
            IResolvedLlmToolCallProvider currentProxy = policy.GetToolsForRole(first.RoleId)
                .OfType<IResolvedLlmToolCallProvider>().Single();
            Assert.IsFalse(currentProxy.TryResolveInvocation(arguments,
                out ResolvedLlmToolInvocation after, out string rejection),
                "An explicit replacement must not inherit removed skill permissions.");
            Assert.IsNull(after);
            Assert.IsFalse(HasSkill(policy, first.RoleId, oldSkill.Name));
            Assert.AreEqual(1, removedCalls, "Resolving a removed tool must not enter its body.");
        }

        [Test]
        public async Task ApplyToPolicyAsync_CaseDistinctRoles_KeepSeparateSkillCatalogs()
        {
            AgentMemoryPolicy policy = new();
            SkillSet upperSkill = SkillSet.FromTextContent("upper-skill", "Upper skill", "Upper instructions.");
            SkillSet lowerSkill = SkillSet.FromTextContent("lower-skill", "Lower skill", "Lower instructions.");
            AgentConfig upper = new AgentBuilder("CatalogCase") { SuppressBuildWarnings = true }
                .WithSkill(upperSkill).BuildDetached();
            AgentConfig lower = new AgentBuilder("catalogcase") { SuppressBuildWarnings = true }
                .WithSkill(lowerSkill).BuildDetached();
            await upper.ApplyToPolicyAsync(policy);
            await lower.ApplyToPolicyAsync(policy);
            policy.AddSkillForRole(upper.RoleId,
                SkillSet.FromTextContent("added-upper", "Added upper skill", "Additional instructions."));

            Assert.IsTrue(HasSkill(policy, upper.RoleId, upperSkill.Name));
            Assert.IsTrue(HasSkill(policy, upper.RoleId, "added-upper"));
            Assert.IsFalse(HasSkill(policy, upper.RoleId, lowerSkill.Name));
            Assert.IsTrue(HasSkill(policy, lower.RoleId, lowerSkill.Name));
            Assert.IsFalse(HasSkill(policy, lower.RoleId, upperSkill.Name));
            Assert.IsFalse(HasSkill(policy, lower.RoleId, "added-upper"),
                "Adding a skill to one case-sensitive role must not change another role's catalog.");
        }

        private static async Task CompletesPromptly(Task operation)
        {
            Task completed = await Task.WhenAny(operation, Task.Delay(TimeSpan.FromSeconds(3)));
            Assert.AreSame(operation, completed, "The operation remained blocked beyond the bounded test deadline.");
            await operation;
        }

        private sealed class ConstantRuntimeProvider : IAgentRuntimeContextProvider
        {
            public string BuildContext(AiTaskRequest request, string roleId, string traceId) => "host context";
        }

        private static bool HasSkill(AgentMemoryPolicy policy, string roleId, string skillName)
        {
            foreach (SkillSet skill in policy.GetSkillsForRole(roleId))
            {
                if (string.Equals(skill.Name, skillName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private sealed class StubOrchestrationService : IAiOrchestrationService
        {
            public int Calls;

            public Task<string> RunTaskAsync(AiTaskRequest task, CancellationToken cancellationToken = default)
            {
                Interlocked.Increment(ref Calls);
                return Task.FromResult("stub-response:" + task.RoleId);
            }

            public void CancelTasks(string cancellationScope)
            {
            }
        }

        private sealed class GatedAsyncSkillStore : ISkillStore, IAsyncSkillStore
        {
            private readonly Dictionary<string, SkillRecord> _records =
                new(StringComparer.OrdinalIgnoreCase);
            private readonly InlineAsyncSkillStoreAdapter _async;
            private readonly TaskCompletionSource<bool> _listGate =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly Exception _listFailure;

            public int ListAsyncCalls;
            public int SyncListCalls;
            public bool GateListAsync;
            public Action OnListEntered;
            private readonly TaskCompletionSource<bool> _listEntered =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            public Task ListEntered => _listEntered.Task;

            public GatedAsyncSkillStore(Exception listFailure = null)
            {
                _listFailure = listFailure;
                _async = new InlineAsyncSkillStoreAdapter(this);
            }

            public void Seed(SkillRecord record)
            {
                _records[record.Id] = record;
            }

            public void ReleaseList()
            {
                _listGate.TrySetResult(true);
            }

            public void Save(SkillRecord record)
            {
                _records[record.Id] = record;
            }

            public bool TryLoad(string id, out SkillRecord record)
            {
                return _records.TryGetValue(id ?? "", out record);
            }

            public IReadOnlyList<SkillRecord> List()
            {
                Interlocked.Increment(ref SyncListCalls);
                return new List<SkillRecord>(_records.Values);
            }

            public void Delete(string id)
            {
                _records.Remove(id ?? "");
            }

            public Task<SkillRecord> LoadAsync(string id, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                TryLoad(id, out SkillRecord record);
                return Task.FromResult(record == null
                    ? null
                    : new SkillRecord(record.Id, record.Description, record.Instructions,
                        record.ToolNames, record.Version, record.Sections));
            }

            public async Task<IReadOnlyList<SkillRecord>> ListAsync(
                CancellationToken cancellationToken = default)
            {
                Interlocked.Increment(ref ListAsyncCalls);
                _listEntered.TrySetResult(true);
                OnListEntered?.Invoke();
                if (_listFailure != null)
                {
                    throw _listFailure;
                }

                if (GateListAsync)
                {
                    await AwaitGateAsync(cancellationToken).ConfigureAwait(false);
                }

                cancellationToken.ThrowIfCancellationRequested();
                return new List<SkillRecord>(_records.Values);
            }

            public Task<TResult> MutateAndPublishAsync<TResult>(string id,
                Func<SkillRecord, SkillStoreMutation<TResult>> prepare,
                Func<TResult, CancellationToken, Task> publish, ILlmAsyncMarshaler callbackContext,
                CancellationToken cancellationToken = default)
            {
                return _async.MutateAndPublishAsync(id, prepare, publish, callbackContext,
                    cancellationToken);
            }

            private async Task AwaitGateAsync(CancellationToken cancellationToken)
            {
                if (_listGate.Task.IsCompleted)
                {
                    await _listGate.Task.ConfigureAwait(false);
                    return;
                }

                cancellationToken.ThrowIfCancellationRequested();
                TaskCompletionSource<bool> canceled =
                    new(TaskCreationOptions.RunContinuationsAsynchronously);
                using (cancellationToken.Register(
                    state => ((TaskCompletionSource<bool>)state).TrySetResult(true),
                    canceled, false))
                {
                    await Task.WhenAny(_listGate.Task, canceled.Task).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    await _listGate.Task.ConfigureAwait(false);
                }
            }
        }
    }
}
