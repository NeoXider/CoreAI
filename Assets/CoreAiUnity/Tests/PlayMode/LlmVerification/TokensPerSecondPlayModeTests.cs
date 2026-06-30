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
using UnityEngine;
using UnityEngine.TestTools;
using Debug = UnityEngine.Debug;

namespace CoreAI.Tests.PlayMode
{
    /// <summary>
    /// Live throughput sanity check: streams a real completion, measures time-to-first-token (TTFT) and
    /// total stream time, and derives the two throughput numbers the benchmark reports — then asserts they
    /// are internally consistent and that the <b>decode-only</b> rate (completion tokens ÷ post-TTFT time)
    /// lands in a sane range, so it is genuinely comparable to the tok/s a runtime like LM Studio prints.
    /// <para>
    /// Why decode-only is the LM-Studio-comparable number: LM Studio's headline tok/s excludes prompt
    /// prefill (it times only generation after the first token). CoreAI's benchmark "provider-call" tok/s
    /// divides by the WHOLE call (prefill + decode), which on large agentic prompts reads much lower. This
    /// test isolates decode by subtracting TTFT, the same way LM Studio does, and checks the two agree in
    /// the expected direction (decode >= provider-call).
    /// </para>
    /// <para>
    /// <see cref="ExplicitAttribute"/>: needs a configured live backend (HTTP OpenAI-compatible such as LM
    /// Studio, or LLMUnity). Streaming must be on for TTFT to be observable; the test <see cref="Assert.Ignore(string)"/>s
    /// when usage tokens or TTFT cannot be measured, rather than failing — it is a measurement probe, not a
    /// correctness gate.
    /// </para>
    /// </summary>
    public class TokensPerSecondPlayModeTests
    {
        /// <summary>Lower bound for a believable local-model decode rate (tok/s). Below this, treat as unmeasured.</summary>
        private const double MinPlausibleDecodeTokPerSec = 1.0;

        /// <summary>Upper bound guarding against a divide-by-near-zero TTFT artifact (very fast local MoE can be high).</summary>
        private const double MaxPlausibleDecodeTokPerSec = 5000.0;

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
        [Explicit("Live throughput probe; run manually against a configured streaming model (e.g. LM Studio).")]
        public IEnumerator DecodeTokensPerSecond_IsConsistent_AndLmStudioComparable()
        {
            // A prompt that forces a non-trivial amount of generation so decode time dominates the tail and
            // the tok/s estimate is stable (a one-word reply makes the denominator too small to be reliable).
            LlmCompletionRequest request = new()
            {
                AgentRoleId = "SmartChat",
                SystemPrompt = "You are a helpful assistant.",
                UserPayload =
                    "Count from 1 to 60, writing each number as a word on its own line " +
                    "(one, two, three, ...). Do not stop early."
            };

            ThroughputProbe probe = new();
            Task streamTask = MeasureStreamAsync(_setup.Client, request, probe);

            float waitSec = 120f;
            CoreAISettingsAsset settingsAsset = CoreAISettingsAsset.Instance;
            if (settingsAsset != null)
            {
                waitSec = Mathf.Max(120f, settingsAsset.RequestTimeoutSeconds + 30f);
            }

            yield return _setup.RunAndWait(streamTask, waitSec, "TokensPerSecond");

            if (!string.IsNullOrEmpty(probe.Error))
            {
                Assert.Ignore($"Streaming probe failed at the provider ({probe.Error}); not a CoreAI metric bug.");
            }

            Assert.IsTrue(probe.SawDone, "Stream never reported IsDone — cannot trust the timing window.");

            // Completion-token source: prefer provider usage; fall back to a chunk count is NOT valid for
            // tok/s (chunks != tokens), so if the provider reported no usage we cannot compute a real rate.
            if (!probe.CompletionTokens.HasValue || probe.CompletionTokens.Value <= 0)
            {
                Assert.Ignore(
                    "Provider reported no completion-token usage on the final chunk, so a real tok/s cannot " +
                    "be computed (chunk count is not a token count). Enable usage reporting on the backend.");
            }

            if (probe.FirstTokenMs <= 0 || probe.TotalMs <= probe.FirstTokenMs)
            {
                Assert.Ignore(
                    "Could not isolate decode time (no measurable gap between first token and stream end — " +
                    "the local server likely returned the whole reply in one SSE frame). TTFT-based decode " +
                    "tok/s is not observable for this backend/model.");
            }

            int completion = probe.CompletionTokens.Value;
            double totalSec = probe.TotalMs / 1000.0;
            double decodeSec = (probe.TotalMs - probe.FirstTokenMs) / 1000.0;

            // The two rates the benchmark cares about:
            //  - providerCallTokPerSec: completion / whole call (prefill + decode) — the benchmark's headline.
            //  - decodeTokPerSec:       completion / (call - TTFT) — what LM Studio shows (decode-only).
            double providerCallTokPerSec = completion / totalSec;
            double decodeTokPerSec = completion / decodeSec;

            Debug.Log(
                $"[TokPerSec] backend={_setup.BackendName} model={probe.Model} " +
                $"completionTokens={completion} promptTokens={probe.PromptTokens?.ToString() ?? "?"} " +
                $"TTFT={probe.FirstTokenMs:0}ms total={probe.TotalMs:0}ms decodeWindow={probe.TotalMs - probe.FirstTokenMs:0}ms\n" +
                $"  provider-call tok/s (prefill+decode) = {providerCallTokPerSec:0.0}\n" +
                $"  decode-only   tok/s (LM-Studio-like)  = {decodeTokPerSec:0.0}\n" +
                $"  => compare decode-only to the number LM Studio prints for this model; they should be close.");

            // Sanity: decode rate is in a believable range (guards against TTFT≈0 blowups and dead backends).
            Assert.That(decodeTokPerSec, Is.GreaterThan(MinPlausibleDecodeTokPerSec),
                $"Decode tok/s {decodeTokPerSec:0.0} is implausibly low — measurement or backend problem.");
            Assert.That(decodeTokPerSec, Is.LessThan(MaxPlausibleDecodeTokPerSec),
                $"Decode tok/s {decodeTokPerSec:0.0} is implausibly high — likely a near-zero decode window artifact.");

            // Directional invariant: removing prefill from the denominator can only INCREASE the rate, so
            // decode-only tok/s must be >= provider-call tok/s (within a tiny tolerance for rounding). If this
            // is violated, the TTFT/timing wiring is wrong — exactly the bug class this test guards.
            Assert.That(decodeTokPerSec, Is.GreaterThanOrEqualTo(providerCallTokPerSec - 0.01),
                "Decode-only tok/s must be >= provider-call tok/s (decode excludes prefill). " +
                "If it is lower, TTFT or the timing window is measured incorrectly.");
        }

        /// <summary>
        /// Streams the request, stamping the first non-empty text delta (TTFT) and the terminal usage, so the
        /// caller can split the total call into prefill (≈TTFT) and decode (the rest).
        /// </summary>
        private static async Task MeasureStreamAsync(
            ILlmClient client, LlmCompletionRequest request, ThroughputProbe probe)
        {
            Stopwatch sw = Stopwatch.StartNew();
            try
            {
                await foreach (LlmStreamChunk chunk in client.CompleteStreamingAsync(request, CancellationToken.None))
                {
                    if (!string.IsNullOrEmpty(chunk.Error))
                    {
                        probe.Error = chunk.Error;
                    }

                    // TTFT = first chunk that actually carries generated text (an empty leading frame, e.g. a
                    // role/keep-alive delta, must not count as the first token).
                    if (probe.FirstTokenMs <= 0 && !string.IsNullOrEmpty(chunk.Text))
                    {
                        probe.FirstTokenMs = sw.Elapsed.TotalMilliseconds;
                    }

                    // Usage arrives on the final chunk when the backend reports it; keep the last seen values.
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
