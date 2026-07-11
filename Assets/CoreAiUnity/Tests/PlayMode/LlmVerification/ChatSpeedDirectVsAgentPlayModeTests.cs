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

            // CRITICAL for a fair TTFT comparison: warm EACH configuration immediately before measuring it,
            // not once globally. A single global warmup skews the result because the server keeps warming
            // its KV cache / model load between calls, so whichever path runs LAST looks fastest regardless
            // of its prompt size — earlier this made the big-prompt Creator role read as the FASTEST, which
            // is backwards. A per-config warm-up run (result discarded) puts every path on an equally warm
            // server, so the measured TTFT delta reflects the CoreAI pipeline prefill, not call order.

            // A) DIRECT — raw provider client, minimal system prompt, no tools (like native LLMUnity chat).
            ThroughputProbe direct = new();
            yield return WarmThenMeasure(
                () => _setup.Client.CompleteStreamingAsync(Direct(userText), CancellationToken.None),
                timeout, "direct", direct);

            // B) AGENT (PlainChat) — full orchestrator pipeline with a light, tool-free role.
            ThroughputProbe agentLight = new();
            yield return WarmThenMeasure(
                () => _setup.Orchestrator.RunStreamingAsync(Turn(BuiltInAgentRoleIds.PlainChat, userText),
                    CancellationToken.None),
                timeout, "agent-plainchat", agentLight);

            // C) AGENT (Creator) — full orchestrator pipeline with the tool-configured game-master role.
            ThroughputProbe agentHeavy = new();
            yield return WarmThenMeasure(
                () => _setup.Orchestrator.RunStreamingAsync(Turn(BuiltInAgentRoleIds.Creator, userText),
                    CancellationToken.None),
                timeout, "agent-creator", agentHeavy);

            Debug.Log(
                $"[ChatSpeed] ===== Direct vs Agent (same model/server: {_setup.BackendName}, {direct.Model}) =====");
            Debug.Log(Line("A) DIRECT (raw client, minimal prompt, no tools)", direct));
            Debug.Log(Line("B) AGENT  (orchestrator, PlainChat, no tools)   ", agentLight));
            Debug.Log(Line("C) AGENT  (orchestrator, Creator + tools)       ", agentHeavy));
            // TTFT is the ONLY fair overhead metric here: total wall-clock is dominated by decode, which is
            // proportional to how many tokens each path happened to emit (a shorter reply finishes sooner even
            // with more pipeline work), so a raw total delta is misleading. Report TTFT delta as the pipeline
            // overhead and print total deltas only as context, each with an explicit sign.
            Debug.Log(
                $"[ChatSpeed] pipeline overhead vs direct (TTFT = prefill cost, the fair metric):  " +
                $"PlainChat {Signed(agentLight.FirstTokenMs - direct.FirstTokenMs)}ms TTFT   |   " +
                $"Creator {Signed(agentHeavy.FirstTokenMs - direct.FirstTokenMs)}ms TTFT");
            Debug.Log(
                $"[ChatSpeed] total wall-clock delta (NOT overhead — scales with output length; " +
                $"direct={direct.CompletionTokens?.ToString() ?? "?"}tok, " +
                $"plainchat={agentLight.CompletionTokens?.ToString() ?? "?"}tok, " +
                $"creator={agentHeavy.CompletionTokens?.ToString() ?? "?"}tok):  " +
                $"PlainChat {Signed(agentLight.TotalMs - direct.TotalMs)}ms   |   " +
                $"Creator {Signed(agentHeavy.TotalMs - direct.TotalMs)}ms");
            Debug.Log(
                "[ChatSpeed] Interpretation: all three hit the same local model. The TTFT delta is the real " +
                "CoreAI pipeline cost (role prompt + tools prefill); a small delta means CoreAI is cheap and the " +
                "model dominates. Ignore total deltas and decode tok/s when TTFT ≈ total (the stream arrived in " +
                "one late chunk, so the decode window is too small to measure throughput).");

            Assert.IsTrue(direct.SawDone, "Direct path must complete to compare.");
        }

        /// <summary>
        /// Warms THIS specific configuration once (result discarded), then measures it. Per-config warming is
        /// what makes the TTFT comparison fair: without it, the server keeps warming between calls and
        /// whichever path runs last looks fastest regardless of prompt size.
        /// </summary>
        private IEnumerator WarmThenMeasure(
            Func<IAsyncEnumerable<LlmStreamChunk>> streamFactory,
            float timeout, string label, ThroughputProbe probe)
        {
            ThroughputProbe warm = new();
            yield return _setup.RunAndWait(MeasureAsync(streamFactory(), warm), timeout, label + "-warm");
            yield return _setup.RunAndWait(MeasureAsync(streamFactory(), probe), timeout, label);
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

        /// <summary>Formats a signed millisecond delta with an explicit +/- and no double sign (e.g. "-2418").</summary>
        private static string Signed(double ms)
        {
            return (ms >= 0 ? "+" : "") + ms.ToString("0");
        }

        private static string Line(string label, ThroughputProbe p)
        {
            double decodeSec = (p.TotalMs - p.FirstTokenMs) / 1000.0;
            double decodeRaw = p.CompletionTokens.HasValue && decodeSec > 0
                ? p.CompletionTokens.Value / decodeSec
                : double.NaN;
            // Decode throughput is only meaningful when the stream was actually incremental. If it arrived as
            // one late chunk, TTFT ≈ total, the decode window is tiny, and out/window explodes to an impossible
            // rate. Gate on a plausible local-model ceiling (~500 tok/s) rather than a fixed window: above that
            // the number is a streaming artefact, not throughput, so report it as unmeasurable.
            const double PlausibleMaxLocalTokPerSec = 500.0;
            bool decodeMeasurable = !double.IsNaN(decodeRaw)
                                    && p.CompletionTokens.Value > 2
                                    && decodeRaw <= PlausibleMaxLocalTokPerSec;
            string decodeStr = decodeMeasurable
                ? decodeRaw.ToString("0.0") + "tok/s"
                : "n/a (stream not incremental — one late chunk)";
            string err = string.IsNullOrEmpty(p.Error) ? "" : $"  ERROR={p.Error}";
            return $"[ChatSpeed] {label}: TTFT={p.FirstTokenMs:0}ms total={p.TotalMs:0}ms " +
                   $"outTok={p.CompletionTokens?.ToString() ?? "?"} promptTok={p.PromptTokens?.ToString() ?? "?"} " +
                   $"decode~{decodeStr}{err}";
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