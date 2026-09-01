#if COREAI_LLM && !UNITY_WEBGL
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.AgentMemory;
using CoreAI.Ai;
using CoreAI.Authority;
using CoreAI.Infrastructure.Llm;
using CoreAI.Infrastructure.Logging;
using CoreAI.Messaging;
using CoreAI.Session;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CoreAI.Tests.PlayMode
{
    /// <summary>
    /// LIVE PlayMode verification for the <see cref="AiAttachment"/> / <see cref="AiTaskRequest.Attachments"/>
    /// API against a REAL OpenAI-compatible model (LM Studio, OpenAI, OpenRouter, …). The EditMode suite
    /// (<c>AiAttachmentEditModeTests</c>) already pins the wire shape; these tests prove a real model actually
    /// RECEIVES and READS the attachment end-to-end.
    /// <para>
    /// STREAMING IS THE PRIMARY PATH: the main assertions drive <see cref="AiOrchestrator.RunStreamingAsync"/>
    /// (which flows attachments through <c>BuildCompletionRequest</c> into
    /// <see cref="ILlmClient.CompleteStreamingAsync"/>). A non-streaming variant
    /// (<see cref="ILlmClient.CompleteAsync"/> via <see cref="AiOrchestrator.RunTaskAsync"/> with streaming
    /// disabled) is a secondary support check.
    /// </para>
    /// <para>
    /// Endpoint selection and skip semantics mirror the rest of the LlmVerification suite: the backend is
    /// resolved by <see cref="PlayModeProductionLikeLlmFactory.TryCreate"/> (env
    /// <c>COREAI_TEST_BASE_URL</c>/<c>COREAI_TEST_MODEL</c>/<c>COREAI_TEST_API_KEY</c>, a gitignored local
    /// config file, or a fully configured <c>CoreAISettingsAsset</c>). When no live backend is configured the
    /// test <see cref="Assert.Ignore(string)"/>s with the factory's reason instead of failing.
    /// </para>
    /// </summary>
    [TestFixture]
    public sealed class AiAttachmentLivePlayModeTests
    {
        // WHY: per-test unique sentinels keep any prompt/response caching on the provider from letting one
        // test's answer satisfy another (cross-contamination).
        private const string TextSentinel = "ZANZIBAR-7741";
        private const string LuaSpawnLimit = "37";
        private const string HistorySentinel = "QUOKKA-5093";

        // Per-test vision gating, following the suite's COREAI_TEST_* env convention. VISION_MODEL is an
        // optional per-test model override (only honored on the env/file HTTP path); VISION forces the gate
        // (on|off|auto, default auto → VisionCapability model heuristic).
        private const string EnvVisionModel = "COREAI_TEST_VISION_MODEL";
        private const string EnvVisionMode = "COREAI_TEST_VISION";

        private const float Temperature = 0f;
        private const int RequestTimeoutSeconds = 180;
        private const float WaitSeconds = 180f;

        // ===================== 1. Text attachment, STREAMING (primary) =====================

        [UnityTest]
        [Timeout(300000)]
        public IEnumerator TextAttachment_Streaming_ModelReadsInlinedFile()
        {
            if (!TryCreateHandle(null, out PlayModeProductionLikeLlmHandle handle, out string ignore))
            {
                Assert.Ignore(ignore);
            }

            using (handle)
            {
                yield return EnsureBackendReady(handle);

                byte[] md = Encoding.UTF8.GetBytes(
                    "# Secret Note\n\nThe secret code word is " + TextSentinel + ". Keep it safe.\n");
                AiTaskRequest task = BuildTask(
                    "AttachTest_TextStream",
                    "What is the secret code word in the attached file? Reply with just the code word.",
                    new List<AiAttachment> { AiAttachment.FromFile("secret.md", md) });

                StreamOutcome outcome = new();
                AiOrchestrator orch = CreateOrchestrator(handle.Client, task.RoleId, true, out _);
                Task run = RunStreamingCollectAsync(orch, task, outcome, CancellationToken.None);
                yield return PlayModeTestAwait.WaitTask(run, WaitSeconds, "TextAttachment_Streaming");

                string answer = outcome.Text.ToString();
                Debug.Log($"[AttachmentLive] TextAttachment_Streaming answer: {answer} (error={outcome.Error})");
                Assert.IsNull(outcome.Error, $"Streaming attachment turn failed: {outcome.Error}");
                StringAssert.Contains(TextSentinel, answer,
                    "The streamed answer must contain the sentinel inlined from the attached .md file.");
            }
        }

        // ===================== 2. Lua attachment, STREAMING (primary) =====================

        [UnityTest]
        [Timeout(300000)]
        public IEnumerator LuaAttachment_Streaming_ModelAnswersAboutCode()
        {
            if (!TryCreateHandle(null, out PlayModeProductionLikeLlmHandle handle, out string ignore))
            {
                Assert.Ignore(ignore);
            }

            using (handle)
            {
                yield return EnsureBackendReady(handle);

                byte[] lua = Encoding.UTF8.GetBytes(
                    "-- spawn configuration\nlocal SPAWN_LIMIT = " + LuaSpawnLimit + "\nreturn SPAWN_LIMIT\n");
                AiTaskRequest task = BuildTask(
                    "AttachTest_LuaStream",
                    "What number is SPAWN_LIMIT set to in the attached script? Reply with just the number.",
                    new List<AiAttachment> { AiAttachment.FromFile("spawn.lua", lua) });

                StreamOutcome outcome = new();
                AiOrchestrator orch = CreateOrchestrator(handle.Client, task.RoleId, true, out _);
                Task run = RunStreamingCollectAsync(orch, task, outcome, CancellationToken.None);
                yield return PlayModeTestAwait.WaitTask(run, WaitSeconds, "LuaAttachment_Streaming");

                string answer = outcome.Text.ToString();
                Debug.Log($"[AttachmentLive] LuaAttachment_Streaming answer: {answer} (error={outcome.Error})");
                Assert.IsNull(outcome.Error, $"Streaming lua-attachment turn failed: {outcome.Error}");
                StringAssert.Contains(LuaSpawnLimit, answer,
                    "The streamed answer must report the SPAWN_LIMIT value read from the attached .lua file.");
            }
        }

        // ===================== 3. Text attachment, NON-STREAMING (secondary) =====================

        [UnityTest]
        [Timeout(300000)]
        public IEnumerator TextAttachment_NonStreaming_SameBehavior()
        {
            if (!TryCreateHandle(null, out PlayModeProductionLikeLlmHandle handle, out string ignore))
            {
                Assert.Ignore(ignore);
            }

            using (handle)
            {
                yield return EnsureBackendReady(handle);

                // Distinct sentinel from test 1 so a cached streaming answer cannot satisfy this turn.
                const string sentinel = "NARWHAL-8820";
                byte[] md = Encoding.UTF8.GetBytes(
                    "# Secret Note\n\nThe secret code word is " + sentinel + ". Keep it safe.\n");
                AiTaskRequest task = BuildTask(
                    "AttachTest_TextNonStream",
                    "What is the secret code word in the attached file? Reply with just the code word.",
                    new List<AiAttachment> { AiAttachment.FromFile("secret.md", md) });

                // streaming:false forces CompleteForTaskAsync down the non-streaming ILlmClient.CompleteAsync path.
                AiOrchestrator orch = CreateOrchestrator(handle.Client, task.RoleId, false, out _);
                TaskResultBox box = new();
                Task run = RunTaskCollectAsync(orch, task, box, CancellationToken.None);
                yield return PlayModeTestAwait.WaitTask(run, WaitSeconds, "TextAttachment_NonStreaming");

                Debug.Log($"[AttachmentLive] TextAttachment_NonStreaming answer: {box.Content}");
                StringAssert.Contains(sentinel, box.Content ?? "",
                    "The non-streaming answer must contain the sentinel inlined from the attached .md file.");
            }
        }

        // ===================== 4. Image attachment, STREAMING (vision-gated) =====================

        [UnityTest]
        [Timeout(300000)]
        public IEnumerator ImageAttachment_Streaming_VisionModelSeesColor()
        {
            string modelOverride = GetEnv(EnvVisionModel);
            if (!TryCreateHandle(modelOverride, out PlayModeProductionLikeLlmHandle handle, out string ignore))
            {
                Assert.Ignore(ignore);
            }

            using (handle)
            {
                // Vision capability gate: reuse the production VisionCapability heuristic on the resolved model,
                // overridable with COREAI_TEST_VISION=on|off. Text-only models are skipped, never failed.
                string model = ResolveModelName(handle);
                VisionSupportMode mode = ParseVisionMode(GetEnv(EnvVisionMode));
                if (!VisionCapability.IsEnabled(mode, model))
                {
                    Assert.Ignore(
                        $"Configured model '{model}' is not vision-capable (mode={mode}). " +
                        $"Set {EnvVisionModel} to a vision model id and/or {EnvVisionMode}=on to run this test.");
                }

                yield return EnsureBackendReady(handle);

                byte[] png = MakeSolidColorPng(64, 64, new Color32(255, 0, 0, 255));
                AiTaskRequest task = BuildTask(
                    "AttachTest_ImageStream",
                    "What single dominant color is this image? Answer with one word.",
                    new List<AiAttachment> { AiAttachment.Image(png, "image/png", "red64.png") });

                StreamOutcome outcome = new();
                AiOrchestrator orch = CreateOrchestrator(handle.Client, task.RoleId, true, out _);
                Task run = RunStreamingCollectAsync(orch, task, outcome, CancellationToken.None);
                yield return PlayModeTestAwait.WaitTask(run, WaitSeconds, "ImageAttachment_Streaming");

                string answer = outcome.Text.ToString();
                Debug.Log($"[AttachmentLive] ImageAttachment_Streaming answer: {answer} (error={outcome.Error})");

                // A provider that lacks real vision typically rejects the image_url part with an error; skip
                // (do not fail) so a mis-tagged model or keyless local server cannot red the suite.
                if (!string.IsNullOrEmpty(outcome.Error))
                {
                    Assert.Ignore(
                        $"Model '{model}' errored on image content (likely no vision support): {outcome.Error}");
                }

                if (string.IsNullOrWhiteSpace(answer))
                {
                    Assert.Ignore(
                        $"Vision model '{model}' returned an empty streamed answer for the red image; " +
                        "retry or pick another model — not a CoreAI attachment-routing failure.");
                }

                StringAssert.Contains("red", answer.ToLowerInvariant(),
                    "A vision model must identify the pure-red PNG as red.");
            }
        }

        // ===================== 5. History placeholder sanity (cheap, live) =====================

        [UnityTest]
        [Timeout(300000)]
        public IEnumerator AttachmentTurn_PersistsPlaceholder_NotRawBytes()
        {
            if (!TryCreateHandle(null, out PlayModeProductionLikeLlmHandle handle, out string ignore))
            {
                Assert.Ignore(ignore);
            }

            using (handle)
            {
                yield return EnsureBackendReady(handle);

                byte[] md = Encoding.UTF8.GetBytes("# Note\n\nThe secret code word is " + HistorySentinel + ".\n");
                AiTaskRequest task = BuildTask(
                    "AttachTest_History",
                    "What is the secret code word in the attached file? Reply with just the code word.",
                    new List<AiAttachment> { AiAttachment.FromFile("note.md", md) });

                // A recording store so the persisted user turn is readable after the orchestrator completes.
                AiOrchestrator orch = CreateOrchestrator(handle.Client, task.RoleId, true,
                    out InMemoryStore store);
                StreamOutcome outcome = new();
                Task run = RunStreamingCollectAsync(orch, task, outcome, CancellationToken.None);
                yield return PlayModeTestAwait.WaitTask(run, WaitSeconds, "AttachmentTurn_History");

                Assert.IsNull(outcome.Error, $"Attachment turn failed before history was persisted: {outcome.Error}");

                ChatMessage[] history = store.GetChatHistory(task.RoleId);
                string userTurn = null;
                foreach (ChatMessage m in history)
                {
                    if (string.Equals(m.Role, "user", StringComparison.Ordinal))
                    {
                        userTurn = m.Content;
                        break;
                    }
                }

                Debug.Log($"[AttachmentLive] Persisted user turn: {userTurn}");
                Assert.IsNotNull(userTurn, "The completed attachment turn must persist a 'user' history entry.");
                StringAssert.Contains("[attachment:", userTurn,
                    "The persisted user turn must carry a compact [attachment: …] placeholder.");
                StringAssert.Contains("note.md", userTurn, "The placeholder must name the attached file.");
                StringAssert.DoesNotContain("base64", userTurn.ToLowerInvariant(),
                    "History must never persist base64 attachment data — only the placeholder.");
                // Stronger guarantee: the raw file body is inlined only into the wire prompt, never history.
                StringAssert.DoesNotContain(HistorySentinel, userTurn,
                    "The raw attachment content must not be persisted into chat history, only the placeholder.");
            }
        }

        // ===================== Helpers =====================

        private static bool TryCreateHandle(
            string modelOverride, out PlayModeProductionLikeLlmHandle handle, out string ignoreReason)
        {
            return PlayModeProductionLikeLlmFactory.TryCreate(
                null, // auto-select backend from CoreAISettingsAsset / env
                Temperature,
                RequestTimeoutSeconds,
                modelOverride,
                out handle,
                out ignoreReason);
        }

        private static IEnumerator EnsureBackendReady(PlayModeProductionLikeLlmHandle handle)
        {
            if (handle.ResolvedBackend == PlayModeProductionLikeLlmBackend.LlmUnity)
            {
                yield return PlayModeProductionLikeLlmFactory.EnsureLlmUnityModelReady(handle);
            }
        }

        private static AiTaskRequest BuildTask(string roleId, string question, List<AiAttachment> attachments)
        {
            return new AiTaskRequest
            {
                RoleId = roleId,
                // WHY: an explicit per-request system prompt neutralizes any built-in role prompt and keeps the
                // model on a terse, single-answer reply so the sentinel assertion is not buried in prose.
                SystemPrompt =
                    "You are a precise assistant. Read the attached files and answer the question directly and " +
                    "briefly. Do not explain your reasoning.",
                Hint = question,
                Attachments = attachments,
                MaxOutputTokens = 128000
            };
        }

        private static AiOrchestrator CreateOrchestrator(
            ILlmClient client, string roleId, bool streaming, out InMemoryStore store)
        {
            store = new InMemoryStore();

            AgentMemoryPolicy policy = new();
            // ChatOnly + chat history: no tools to distract the model, and the user turn is persisted so the
            // history-placeholder test can read it back.
            AgentConfig cfg = new AgentBuilder(roleId)
                .WithMode(AgentMode.ChatOnly)
                .WithChatHistory()
                .Build();
            cfg.ApplyToPolicy(policy);

            CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            if (!streaming)
            {
                SetEnableStreaming(settings, false);
            }

            SessionTelemetryCollector telemetry = new();
            AiPromptComposer composer = new(
                new BuiltInDefaultAgentSystemPromptProvider(),
                new NoAgentUserPromptTemplateProvider(),
                new NullLuaScriptVersionStore());

            return new AiOrchestrator(
                new SoloAuthorityHost(),
                client,
                new NullSink(),
                telemetry,
                composer,
                store,
                policy,
                new NoOpRoleStructuredResponsePolicy(),
                new NullAiOrchestrationMetrics(),
                settings,
                new LocalActorIdentityProvider("ai-attachment-live-test"));
        }

        // WHY: CoreAISettingsAsset has no public streaming setter; toggling the serialized field via reflection
        // is a test-only escape hatch (mirrors PlayModeProductionLikeLlmFactory.BuildBehaviorSettings) so the
        // non-streaming path can be exercised without touching runtime code.
        private static void SetEnableStreaming(CoreAISettingsAsset settings, bool enabled)
        {
            FieldInfo field = typeof(CoreAISettingsAsset).GetField(
                "enableStreaming", BindingFlags.Instance | BindingFlags.NonPublic);
            field?.SetValue(settings, enabled);
        }

        private static async Task RunStreamingCollectAsync(
            AiOrchestrator orch, AiTaskRequest task, StreamOutcome outcome, CancellationToken ct)
        {
            await foreach (LlmStreamChunk chunk in orch.RunStreamingAsync(task, ct))
            {
                if (!string.IsNullOrEmpty(chunk.Error))
                {
                    outcome.Error = chunk.Error;
                }

                if (!string.IsNullOrEmpty(chunk.Text))
                {
                    outcome.Text.Append(chunk.Text);
                }

                if (chunk.IsDone)
                {
                    outcome.Done = true;
                }
            }
        }

        private static async Task RunTaskCollectAsync(
            AiOrchestrator orch, AiTaskRequest task, TaskResultBox box, CancellationToken ct)
        {
            box.Content = await orch.RunTaskAsync(task, ct);
        }

        private static string ResolveModelName(PlayModeProductionLikeLlmHandle handle)
        {
            if (handle.ResolvedConfig != null && !string.IsNullOrWhiteSpace(handle.ResolvedConfig.Model))
            {
                return handle.ResolvedConfig.Model;
            }

            CoreAISettingsAsset settings = CoreAISettingsAsset.Instance;
            return settings != null ? settings.ModelName : "";
        }

        private static VisionSupportMode ParseVisionMode(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return VisionSupportMode.Auto;
            }

            switch (raw.Trim().ToLowerInvariant())
            {
                case "on":
                case "true":
                case "1":
                    return VisionSupportMode.On;
                case "off":
                case "false":
                case "0":
                    return VisionSupportMode.Off;
                default:
                    return VisionSupportMode.Auto;
            }
        }

        private static byte[] MakeSolidColorPng(int width, int height, Color32 color)
        {
            Texture2D tex = new(width, height, TextureFormat.RGBA32, false);
            try
            {
                Color32[] pixels = new Color32[width * height];
                for (int i = 0; i < pixels.Length; i++)
                {
                    pixels[i] = color;
                }

                tex.SetPixels32(pixels);
                tex.Apply();
                return tex.EncodeToPNG();
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(tex);
            }
        }

        private static string GetEnv(string name)
        {
            string v = Environment.GetEnvironmentVariable(name);
            return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        }

        private sealed class StreamOutcome
        {
            public readonly StringBuilder Text = new();
            public string Error;
            public bool Done;
        }

        private sealed class TaskResultBox
        {
            public string Content;
        }

        private sealed class NullSink : IAiGameCommandSink
        {
            public void Publish(ApplyAiGameCommand command)
            {
            }
        }
    }
}
#endif
