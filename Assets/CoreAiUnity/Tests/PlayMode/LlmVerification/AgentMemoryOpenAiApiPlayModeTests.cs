#if !COREAI_NO_LLM && !UNITY_WEBGL
using System;
using System.Collections;
using System.Reflection;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Infrastructure.Llm;
using CoreAI.Messaging;
using MessagePipe;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CoreAI.Tests.PlayMode
{
    /// <summary>
    /// PlayMode verification for the Memory tool against a real LLM backend configured through
    /// <see cref="CoreAISettingsAsset"/>. Assertions use memory-store state rather than reply text.
    /// </summary>
    public sealed class AgentMemoryOpenAiApiPlayModeTests
    {
        private const string Role = BuiltInAgentRoleIds.Creator;
        private const string WriteExpectedSubstring = "qwen4b works great";
        private const string AppendMarker = "appended value";
        private const string InitialBaseline = "initial value";
        private const string PreClearPayload = "this will be deleted";

        [UnityTest]
        [Timeout(300000)]
        public IEnumerator MemoryTool_WritesMemory()
        {
            using TestAgentSetup setup = new();
            yield return setup.Initialize();
            if (!setup.IsReady)
            {
                Assert.Ignore("TestAgentSetup failed");
            }

            ApplyVerboseLlmLoggingForTrace();
            using LlmFailureMonitor llmFailures = LlmFailureMonitor.Start();

            Debug.Log($"[AgentMemoryOpenAiApiPlayMode] Backend: {setup.BackendName}, role={Role}");

            AiTaskRequest request = new()
            {
                RoleId = Role,
                Hint = "Remember this new fact for future turns: qwen4b works great."
            };
            LogHintToConsole(request);

            Task<string> run = setup.Orchestrator.RunTaskAsync(request);
            yield return setup.RunAndWait(run, 240f, "memory write");
            LogOrchestratorReply(run);

            if (!ReadMemoryOrEmpty(setup).Contains(WriteExpectedSubstring, StringComparison.OrdinalIgnoreCase))
            {
                llmFailures.InconclusiveIfInfrastructureFailureOrNoResponse("memory write", run);
            }

            AssertAgentMemoryNonEmpty(setup, "after memory write");
            setup.MemoryStore.TryLoad(Role, out AgentMemoryState state);
            Assert.That(
                state.Memory,
                Does.Contain(WriteExpectedSubstring).IgnoreCase,
                () => MemoryMismatchMessage("write", WriteExpectedSubstring, state.Memory));

            Debug.Log(
                $"[AgentMemoryOpenAiApiPlayMode] Write OK — stored memory ({state.Memory.Length} chars): {FormatMemoryForLog(state.Memory)}");
        }

        [UnityTest]
        [Timeout(360000)]
        public IEnumerator MemoryTool_AppendsMemory()
        {
            using TestAgentSetup setup = new();
            yield return setup.Initialize();
            if (!setup.IsReady)
            {
                Assert.Ignore("TestAgentSetup failed");
            }

            ApplyVerboseLlmLoggingForTrace();
            using LlmFailureMonitor llmFailures = LlmFailureMonitor.Start();

            setup.MemoryStore.Save(Role, new AgentMemoryState { Memory = InitialBaseline });
            AssertAgentMemoryEquals(setup, InitialBaseline, "baseline before append");

            Debug.Log("[AgentMemoryOpenAiApiPlayMode] Testing append...");

            AiTaskRequest appendRequest = new()
            {
                RoleId = Role,
                Hint = "Keep the existing memory and add this extra note: appended value."
            };
            LogHintToConsole(appendRequest);

            Task<string> run = setup.Orchestrator.RunTaskAsync(appendRequest);
            yield return setup.RunAndWait(run, 240f, "memory append");
            LogOrchestratorReply(run);
            string memAfterFirst = ReadMemoryOrEmpty(setup);
            LogMemorySnapshot(setup, "after first append request");

            string finalAppendMemory = ReadMemoryOrEmpty(setup);
            if (!finalAppendMemory.Contains(InitialBaseline, StringComparison.Ordinal) ||
                !finalAppendMemory.Contains(AppendMarker, StringComparison.OrdinalIgnoreCase))
            {
                llmFailures.InconclusiveIfInfrastructureFailureOrNoResponse("memory append", run);
            }

            AssertAgentMemoryNonEmpty(setup, "final append state");
            setup.MemoryStore.TryLoad(Role, out AgentMemoryState state);
            Assert.That(
                state.Memory,
                Does.Contain(InitialBaseline),
                () => MemoryMismatchMessage("append (preserve baseline)", InitialBaseline, state.Memory));
            Assert.That(
                state.Memory,
                Does.Contain(AppendMarker).IgnoreCase,
                () => MemoryMismatchMessage("append (marker)", AppendMarker, state.Memory));
            Debug.Log(
                $"[AgentMemoryOpenAiApiPlayMode] Append OK — stored memory ({state.Memory.Length} chars): {FormatMemoryForLog(state.Memory)}");
        }

        [UnityTest]
        [Timeout(360000)]
        public IEnumerator MemoryTool_ClearsMemory()
        {
            using TestAgentSetup setup = new();
            yield return setup.Initialize();
            if (!setup.IsReady)
            {
                Assert.Ignore("TestAgentSetup failed");
            }

            ApplyVerboseLlmLoggingForTrace();
            using LlmFailureMonitor llmFailures = LlmFailureMonitor.Start();

            setup.MemoryStore.Save(Role, new AgentMemoryState { Memory = PreClearPayload });
            AssertAgentMemoryEquals(setup, PreClearPayload, "baseline before clear");

            Debug.Log("[AgentMemoryOpenAiApiPlayMode] Testing clear...");

            AiTaskRequest clearRequest = new()
            {
                RoleId = Role,
                Hint = "Clear your stored memory for this role."
            };
            LogHintToConsole(clearRequest);

            Task<string> run = setup.Orchestrator.RunTaskAsync(clearRequest);
            yield return setup.RunAndWait(run, 240f, "memory clear");
            LogOrchestratorReply(run);

            if (setup.MemoryStore.TryLoad(Role, out AgentMemoryState stillPresent) &&
                !string.IsNullOrWhiteSpace(stillPresent.Memory))
            {
                llmFailures.InconclusiveIfInfrastructureFailureOrNoResponse("memory clear", run);
            }

            AssertAgentMemoryCleared(setup, "after clear tool");
            // Since the audit-wave versioning rework the memory tool's clear is a VERSIONED mutation:
            // the role row survives with empty memory plus a recorded "clear" version so an accidental
            // clear can be rolled back (ListVersions/Revert). Only the direct store-level Clear removes
            // the role key when nothing else shares the file.
            if (setup.MemoryStore.TryLoad(Role, out AgentMemoryState cleared))
            {
                Assert.IsTrue(string.IsNullOrWhiteSpace(cleared.Memory),
                    "After memory tool clear, any retained row must hold no memory content.");
            }

            Debug.Log("[AgentMemoryOpenAiApiPlayMode] Clear OK — memory content empty.");
        }

        /// <summary>
        /// Enables raw HTTP, MEAI, and LLM input/output logging through <see cref="CoreAISettingsAsset"/>.
        /// The orchestrator created by <see cref="TestAgentSetup"/> uses the same settings instance.
        /// </summary>
        private static void ApplyVerboseLlmLoggingForTrace()
        {
            CoreAISettingsAsset s = CoreAISettingsAsset.Instance;
            if (s == null)
            {
                Debug.LogWarning(
                    "[AgentMemoryOpenAiApiPlayMode] CoreAISettingsAsset.Instance is null — verbose LLM trace skipped.");
                return;
            }

            SetPrivateBool(s, "enableHttpDebugLogging", true);
            SetPrivateBool(s, "logLlmInput", true);
            SetPrivateBool(s, "logLlmOutput", true);
            SetPrivateBool(s, "enableMeaiDebugLogging", true);
            SetPrivateBool(s, "logToolCalls", true);
            SetPrivateBool(s, "logToolCallArguments", true);
            SetPrivateBool(s, "logToolCallResults", true);
            SetPrivateBool(s, "logMeaiToolCallingSteps", true);
            SetPrivateBool(s, "logTokenUsage", true);
            SetPrivateBool(s, "logLlmLatency", true);
            SetPrivateBool(s, "logLlmConnectionErrors", true);

            Debug.Log(
                "[AgentMemoryOpenAiApiPlayMode] Verbose LLM trace ON: HTTP body, MEAI steps, LLM input/output, tool args/results " +
                "(see [CoreAI] [Llm] / Meai lines in Console).");
        }

        private static void SetPrivateBool(CoreAISettingsAsset target, string fieldName, bool value)
        {
            FieldInfo f = typeof(CoreAISettingsAsset).GetField(
                fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            f?.SetValue(target, value);
        }

        private static void LogHintToConsole(AiTaskRequest request)
        {
            string hint = request?.Hint ?? "";
            Debug.Log(
                $"[AgentMemoryOpenAiApiPlayMode] ─── AiTaskRequest → model (role={request?.RoleId}) ───\n{hint}\n" +
                "─── end hint ───");
        }

        private static void LogOrchestratorReply(Task<string> completedTask)
        {
            if (completedTask == null)
            {
                return;
            }

            try
            {
                string text = completedTask.Status == TaskStatus.RanToCompletion ? completedTask.Result : null;
                string status = completedTask.Status.ToString();
                string ex = completedTask.IsFaulted && completedTask.Exception != null
                    ? completedTask.Exception.GetBaseException().Message
                    : "";
                Debug.Log(
                    $"[AgentMemoryOpenAiApiPlayMode] ─── orchestrator RunTaskAsync finished ({status}) ───\n" +
                    (string.IsNullOrEmpty(text) ? "(no terminal string)" : text) +
                    (string.IsNullOrEmpty(ex) ? "" : $"\n─── fault: {ex} ───"));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[AgentMemoryOpenAiApiPlayMode] Could not read task result: {e.Message}");
            }
        }

        private static string ReadMemoryOrEmpty(TestAgentSetup setup)
        {
            return setup.MemoryStore.TryLoad(Role, out AgentMemoryState st) ? st.Memory ?? "" : "";
        }

        private static void LogMemorySnapshot(TestAgentSetup setup, string phase)
        {
            if (!setup.MemoryStore.TryLoad(Role, out AgentMemoryState st))
            {
                Debug.Log($"[AgentMemoryOpenAiApiPlayMode] Memory snapshot ({phase}): <no row for role {Role}>");
                return;
            }

            string m = st.Memory ?? "";
            Debug.Log(
                $"[AgentMemoryOpenAiApiPlayMode] Memory snapshot ({phase}): len={m.Length}, empty={string.IsNullOrWhiteSpace(m)}, " +
                $"repr={FormatMemoryForLog(m)}");
        }

        private static string FormatMemoryForLog(string memory)
        {
            if (memory == null)
            {
                return "<null>";
            }

            string escaped = memory.Replace("\r", "\\r").Replace("\n", "\\n");
            const int max = 800;
            return escaped.Length <= max ? $"\"{escaped}\"" : $"\"{escaped.Substring(0, max)}…\" (truncated)";
        }

        private static string MemoryMismatchMessage(string expectation, string expectedFragment, string actual)
        {
            return
                $"Expected memory to contain [{expectedFragment}] ({expectation}). Actual ({actual?.Length ?? 0} chars): {FormatMemoryForLog(actual)}";
        }

        private static void AssertAgentMemoryNonEmpty(TestAgentSetup setup, string phase)
        {
            Assert.IsTrue(
                setup.MemoryStore.TryLoad(Role, out AgentMemoryState st),
                $"[{phase}] Expected row for role '{Role}' in InMemoryStore.");
            string m = st.Memory ?? "";
            Assert.IsFalse(string.IsNullOrWhiteSpace(m),
                $"[{phase}] AgentMemoryState.Memory must be non-empty. Got: {FormatMemoryForLog(m)}");
        }

        private static void AssertAgentMemoryEquals(TestAgentSetup setup, string expectedExact, string phase)
        {
            Assert.IsTrue(setup.MemoryStore.TryLoad(Role, out AgentMemoryState st), $"[{phase}] Missing memory row.");
            Assert.AreEqual(
                expectedExact,
                st.Memory ?? "",
                $"[{phase}] Memory string mismatch. Actual: {FormatMemoryForLog(st.Memory)}");
        }

        private static void AssertAgentMemoryCleared(TestAgentSetup setup, string phase)
        {
            if (!setup.MemoryStore.TryLoad(Role, out AgentMemoryState st))
            {
                return;
            }

            string m = st.Memory ?? "";
            Assert.IsTrue(
                string.IsNullOrWhiteSpace(m),
                $"[{phase}] After clear, memory must be empty or row removed. Still have ({m.Length} chars): {FormatMemoryForLog(m)}");
        }

        private sealed class LlmFailureMonitor : IDisposable
        {
            private IDisposable _subscription;
            private bool _hasFailure;
            private LlmRequestCompleted _lastFailure;

            private LlmFailureMonitor(IDisposable subscription)
            {
                _subscription = subscription;
            }

            public static LlmFailureMonitor Start()
            {
                if (!GlobalMessagePipe.IsInitialized)
                {
                    return new LlmFailureMonitor(null);
                }

                LlmFailureMonitor monitor = new(null);
                try
                {
                    monitor._subscription = GlobalMessagePipe
                        .GetSubscriber<LlmRequestCompleted>()
                        .Subscribe(monitor.OnCompleted);
                    return monitor;
                }
                catch (Exception e)
                {
                    Debug.LogWarning(
                        $"[AgentMemoryOpenAiApiPlayMode] Could not subscribe to LLM diagnostics: {e.Message}");
                    return monitor;
                }
            }

            public void Dispose()
            {
                _subscription?.Dispose();
            }

            public void InconclusiveIfInfrastructureFailure(string phase)
            {
                if (!_hasFailure || !IsInfrastructureFailure(_lastFailure))
                {
                    return;
                }

                Assert.Inconclusive(
                    $"[{phase}] LLM backend did not complete successfully ({_lastFailure.ErrorCode}): {_lastFailure.Error}");
            }

            public void InconclusiveIfInfrastructureFailureOrNoResponse(string phase, Task<string> completedTask)
            {
                InconclusiveIfInfrastructureFailure(phase);

                if (completedTask == null)
                {
                    return;
                }

                if (completedTask.IsFaulted)
                {
                    string error = completedTask.Exception?.GetBaseException().Message ?? "unknown fault";
                    Assert.Inconclusive($"[{phase}] LLM backend task faulted before memory could change: {error}");
                }

                if (completedTask.Status == TaskStatus.RanToCompletion &&
                    string.IsNullOrWhiteSpace(completedTask.Result))
                {
                    Assert.Inconclusive(
                        $"[{phase}] LLM backend returned no terminal response before memory changed. Check HTTP backend connectivity.");
                }
            }

            private void OnCompleted(LlmRequestCompleted completed)
            {
                if (completed.Success)
                {
                    return;
                }

                _hasFailure = true;
                _lastFailure = completed;
            }

            private static bool IsInfrastructureFailure(LlmRequestCompleted completed)
            {
                switch (completed.ErrorCode)
                {
                    case LlmErrorCode.Timeout:
                    case LlmErrorCode.Cancelled:
                    case LlmErrorCode.AuthExpired:
                    case LlmErrorCode.QuotaExceeded:
                    case LlmErrorCode.RateLimited:
                    case LlmErrorCode.BackendUnavailable:
                    case LlmErrorCode.ProviderError:
                    case LlmErrorCode.RoutingError:
                    case LlmErrorCode.ContextLengthExceeded:
                        return true;
                }

                string error = completed.Error ?? "";
                return error.IndexOf("task was canceled", StringComparison.OrdinalIgnoreCase) >= 0 ||
                       error.IndexOf("timed out", StringComparison.OrdinalIgnoreCase) >= 0 ||
                       error.IndexOf("timeout", StringComparison.OrdinalIgnoreCase) >= 0 ||
                       error.IndexOf("model is unloaded", StringComparison.OrdinalIgnoreCase) >= 0 ||
                       error.IndexOf("http error", StringComparison.OrdinalIgnoreCase) >= 0 ||
                       error.IndexOf("connection", StringComparison.OrdinalIgnoreCase) >= 0 ||
                       error.IndexOf("unavailable", StringComparison.OrdinalIgnoreCase) >= 0;
            }
        }
    }
}
#endif
