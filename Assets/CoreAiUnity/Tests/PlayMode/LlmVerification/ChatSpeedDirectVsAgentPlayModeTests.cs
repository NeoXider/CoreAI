#if !COREAI_NO_LLM && !UNITY_WEBGL
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Infrastructure.Llm;
using NUnit.Framework;
using UnityEngine.TestTools;
using Debug = UnityEngine.Debug;

namespace CoreAI.Tests.PlayMode
{
    /// <summary>
    /// Live speed comparison between the DIRECT provider call (raw model, minimal prompt, no tools — the
    /// shape the native LLMUnity chat sends) and the CoreAI AGENT path (<see cref="AiOrchestrator"/>: role
    /// system prompt + memory + tool policy). Both hit the same model/server, so the delta is the CoreAI
    /// pipeline overhead — a small A→B delta means the local model, not CoreAI, dominates the wall time.
    /// <para>
    /// Measures three shapes: (A) direct minimal, (B) agent with a light tool-free role (PlainChat), and
    /// (C) agent with a tool-configured role (Creator). <see cref="ExplicitAttribute"/> — needs a live
    /// streaming backend; it is a measurement probe, not a correctness gate.
    /// </para>
    /// </summary>
    public class ChatSpeedDirectVsAgentPlayModeTests
    {
        private TestAgentSetup _setup;

        [UnitySetUp]
        public IEnumerator Setup()
        {
            _setup = new TestAgentSetup();
            yield return _setup.Initialize();
            Assert.IsTrue(_setup.IsReady, $"LLM backend not ready ({_setup.BackendName}). Configure a live model.");
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            _setup?.Dispose();
            yield return null;
        }

        [UnityTest]
        [Explicit("Live speed probe: direct model call vs CoreAI agent pipeline. Run manually against a live model.")]
        public IEnumerator DirectVsAgent_Speed()
        {
            const string userText = "Привет! Как дела? Ответь одним коротким предложением.";
            const float timeout = 120f;

            // Warm the model/KV so the first measured call is not skewed by cold start.
            var warm = new ThroughputProbe();
            yield return _setup.RunAndWait(
                MeasureAsync(_setup.Client.CompleteStreamingAsync(Direct(userText), CancellationToken.None), warm),
                timeout, "warmup");

            // A) DIRECT — raw provider client, minimal system prompt, no tools (like native LLMUnity chat).
            var direct = new ThroughputProbe();
            yield return _setup.RunAndWait(
                MeasureAsync(_setup.Client.CompleteStreamingAsync(Direct(userText), CancellationToken.None), direct),
                timeout, "direct");

            // B) AGENT (PlainChat) — full orchestrator pipeline with a light, tool-free role.
            var agentLight = new ThroughputProbe();
            yield return _setup.RunAndWait(
                MeasureAsync(_setup.Orchestrator.RunStreamingAsync(Turn(BuiltInAgentRoleIds.PlainChat, userText), CancellationToken.None), agentLight),
                timeout, "agent-plainchat");

            // C) AGENT (Creator) — full orchestrator pipeline with the tool-configured game-master role.
            var agentHeavy = new ThroughputProbe();
            yield return _setup.RunAndWait(
                MeasureAsync(_setup.Orchestrator.RunStreamingAsync(Turn(BuiltInAgentRoleIds.Creator, userText), CancellationToken.None), agentHeavy),
                timeout, "agent-creator");

            Debug.Log($"[ChatSpeed] ===== Direct vs Agent (same model/server: {_setup.BackendName}, {direct.Model}) =====");
            Debug.Log(Line("A) DIRECT (raw client, minimal prompt, no tools)", direct));
            Debug.Log(Line("B) AGENT  (orchestrator, PlainChat, no tools)   ", agentLight));
            Debug.Log(Line("C) AGENT  (orchestrator, Creator + tools)       ", agentHeavy));
            Debug.Log(
                $"[ChatSpeed] overhead vs direct:  " +
                $"PlainChat +{agentLight.TotalMs - direct.TotalMs:0}ms total / +{agentLight.FirstTokenMs - direct.FirstTokenMs:0}ms TTFT   |   " +
                $"Creator +{agentHeavy.TotalMs - direct.TotalMs:0}ms total / +{agentHeavy.FirstTokenMs - direct.FirstTokenMs:0}ms TTFT");
            Debug.Log(
                "[ChatSpeed] Interpretation: all three hit the same local model, so most of each total is decode " +
                "time. A small A->B delta = the CoreAI chat pipeline is cheap; a large A->C delta = the heavy " +
                "role's prompt+tools prefill (and any tool round-trips), which is why a light role like PlainChat " +
                "is the fast chat path.");

            Assert.IsTrue(direct.SawDone, "Direct path must complete to compare.");
        }

        private static LlmCompletionRequest Direct(string userText)
        {
            return new LlmCompletionRequest
            {
                AgentRoleId = BuiltInAgentRoleIds.SmartChat,
                SystemPrompt = "You are a helpful assistant. Answer in one short sentence.",
                UserPayload = userText
            };
        }

        private static AiTaskRequest Turn(string roleId, string userText)
        {
            return new AiTaskRequest { RoleId = roleId, Hint = userText };
        }

        private static string Line(string label, ThroughputProbe p)
        {
            double decodeSec = (p.TotalMs - p.FirstTokenMs) / 1000.0;
            double decode = p.CompletionTokens.HasValue && decodeSec > 0 ? p.CompletionTokens.Value / decodeSec : double.NaN;
            string err = string.IsNullOrEmpty(p.Error) ? "" : $"  ERROR={p.Error}";
            return $"[ChatSpeed] {label}: TTFT={p.FirstTokenMs:0}ms total={p.TotalMs:0}ms " +
                   $"outTok={p.CompletionTokens?.ToString() ?? "?"} promptTok={p.PromptTokens?.ToString() ?? "?"} " +
                   $"decode~{(double.IsNaN(decode) ? "?" : decode.ToString("0.0"))}tok/s{err}";
        }

        private static async Task MeasureAsync(IAsyncEnumerable<LlmStreamChunk> stream, ThroughputProbe probe)
        {
            Stopwatch sw = Stopwatch.StartNew();
            try
            {
                await foreach (LlmStreamChunk chunk in stream)
                {
                    if (!string.IsNullOrEmpty(chunk.Error))
                    {
                        probe.Error = chunk.Error;
                    }

                    if (probe.FirstTokenMs <= 0 && !string.IsNullOrEmpty(chunk.Text))
                    {
                        probe.FirstTokenMs = sw.Elapsed.TotalMilliseconds;
                    }

                    if (chunk.CompletionTokens.HasValue)
                    {
                        probe.CompletionTokens = chunk.CompletionTokens;
                    }

                    if (chunk.PromptTokens.HasValue)
                    {
                        probe.PromptTokens = chunk.PromptTokens;
                    }

                    if (!string.IsNullOrEmpty(chunk.Model))
                    {
                        probe.Model = chunk.Model;
                    }

                    if (chunk.IsDone)
                    {
                        probe.SawDone = true;
                    }
                }
            }
            catch (Exception ex)
            {
                probe.Error = ex.Message;
            }
            finally
            {
                probe.TotalMs = sw.Elapsed.TotalMilliseconds;
            }
        }

        private sealed class ThroughputProbe
        {
            public double FirstTokenMs;
            public double TotalMs;
            public int? CompletionTokens;
            public int? PromptTokens;
            public string Model = "";
            public bool SawDone;
            public string Error;
        }
    }
}
#endif
