#if COREAI_LLM
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Chat;
using CoreAI.Infrastructure.Llm;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using VContainer;
using VContainer.Unity;
#if UNITY_EDITOR
using UnityEditor;
#endif
using Object = UnityEngine.Object;

namespace CoreAI.Tests.PlayMode
{
    /// <summary>
    /// The demo scene's chat against the real endpoint the environment names: a turn streams, Stop cancels
    /// it and the chat recovers. Hard failures are the unambiguous outcomes only — a turn that errors,
    /// completes empty, ignores Stop or never settles after cancellation. Whether partial text was on
    /// screen while a turn was open is reported, not asserted: a live endpoint cannot be made to pace on
    /// demand, and <c>CoreAiChatPanelPacedSseStreamingPlayModeTests</c> decides panel pacing
    /// deterministically. Every persisted SmartChat turn of the scene goes to a test-scoped memory key that
    /// the teardown erases, so the developer's own chat history is neither fed to the model nor touched.
    /// </summary>
    public sealed class CoreAiChatDemoRealModelWebGlPlayModeTests
    {
        [UnityTearDown]
        public IEnumerator ScrubTestScopedHistoryAndUnloadLoadedScenes()
        {
            // WHY the scrub precedes the unload: the panel and the scope's stores are reachable only while
            // the scene is loaded, and a turn the test left in flight must settle first — the cancellation
            // an unload triggers still appends the turn's user line to history, after any earlier erase.
            yield return ScrubTestScopedChatHistory();
            // Single-mode scene loads otherwise persist past this test and leak their scope into the
            // rest of the PlayMode run.
            yield return PlayModeSceneSandbox.UnloadToEmptyScene();
        }

        private const string LogPrefix = "[CoreAI.Tests.ChatSceneRealModel]";
        private const string SceneName = "CoreAiChatDemo";
        private const string RoleId = "SmartChat";

        private const string LongListPrompt =
            "Write a long numbered list from 1 to 80. Keep each line short, but do not stop early.";
        private const string ReasoningHint =
            "Try Reasoning Mode = Disabled for hybrid-thinking models (some ignore it).";

        // WHY 120 s: the budget phase two already grants a stopped turn (WaitTask after StopAgent), so the
        // timeout path holds the cancel path to the contract this test asserts anyway, not a stricter one.
        private const float StopSettleTimeoutSeconds = 120f;

        // WHY 120 s and 3 frames: the pacing probe repeats an unsampled prompt through the panel's own
        // service so the skip message can say how this endpoint paced it. Three polled frames with content
        // while the turn is still open are all that report needs, so the probe stops there instead of
        // waiting for the whole answer; the wait matches the settle budget a stopped turn already gets.
        private const float PacingProbeTimeoutSeconds = 120f;
        private const int PacingProbeOpenFrames = 3;

        // WHY 10 s: the teardown only needs a turn the test left in flight to settle after Stop before the
        // test-scoped history is erased; a turn that ignores Stop for longer has already failed the test.
        private const float TearDownSettleTimeoutSeconds = 10f;

        /// <summary>
        /// The memory scope every persisted SmartChat turn of the demo scene is keyed under while a test of
        /// this fixture runs. The scene's scope registers a file-backed store under
        /// <c>Application.persistentDataPath/CoreAI/AgentMemory</c>, and with the default empty scope the
        /// demo, the developer's own Play sessions and every fixture that orchestrates SmartChat share one
        /// <c>SmartChat.history.jsonl</c> — the model then received that whole file as context on each run.
        /// A declared scope digests into a separate file of its own, which the teardown erases.
        /// </summary>
        private static readonly IAgentMemoryScopeProvider TestMemoryScope = new FixedAgentMemoryScopeProvider(
            new AgentMemoryScope(
                "CoreAI.Tests.PlayMode",
                "",
                nameof(CoreAiChatDemoRealModelWebGlPlayModeTests),
                ""));

        private CoreAISettingsAsset _sharedSettings;
        private string _sharedSettingsSnapshotJson;

        /// <summary>
        /// A first turn streams to completion, a long second turn is stopped and its bubble stays frozen,
        /// and a third turn answers. Skipped when the endpoint hands a turn over before a frame can sample
        /// it; the Stop contract that needs no sampled stream is pinned by
        /// <see cref="CoreAiChatDemo_RealModel_StopCancelsTheTurnAndChatRecovers"/>.
        /// </summary>
        [UnityTest]
        [Category("RealLlm")]
        [Category("WebGL")]
        [Timeout(600000)]
        public IEnumerator CoreAiChatDemo_RealModel_StreamsStopAndRecovers()
        {
            DemoChat demo = new();
            yield return OpenDemoChat(demo);
            CoreAiChatPanel panel = demo.Panel;
            CoreAiChatService chatService = demo.ChatService;
            CoreAISettingsAsset settings = demo.Settings;

            // WHY the retarget leaves this alone: streaming is the behaviour under test, and the env vars
            // name an endpoint, not a streaming policy, so an asset with streaming off keeps skipping.
            if (!chatService.IsStreamingEnabled(RoleId, true))
            {
                Assert.Ignore(
                    $"{LogPrefix} Streaming is disabled for {RoleId}. " +
                    $"WebGlNativeStreaming={settings.WebGlNativeStreaming}, EnableStreaming={settings.EnableStreaming}.");
            }

            Debug.Log(
                $"{LogPrefix} Starting real-model scene test. " +
                $"ExecutionMode={settings.ExecutionMode}, BackendType={settings.BackendType}, " +
                $"BaseUrl={settings.ApiBaseUrl}, Model={settings.ModelName}, " +
                $"WebGlNativeStreaming={settings.WebGlNativeStreaming}");

            CoreAiChatExternalSubmitOptions options = new() { AppendUserMessageToChat = true };

            const string firstPrompt = "Give a short response about streaming chat.";
            Task<string> firstTask = Submit(panel, firstPrompt, options);
            StreamingTextProbe firstProbe = new();
            yield return WaitForVisibleStreamingText(
                firstTask, panel, chatService, firstPrompt, firstProbe, 90f, "first real-model stream",
                priorTurnCompleted: false);
            Debug.Log($"{LogPrefix} First visible stream: '{TrimForLog(firstProbe.Text)}'");
            yield return WaitTask(firstTask, 120f, "first real-model response");
            Assert.IsFalse(string.IsNullOrWhiteSpace(firstTask.Result), $"{LogPrefix} First response is empty.");
            Debug.Log($"{LogPrefix} First final response: '{TrimForLog(firstTask.Result)}'");

            Task<string> stopTask = Submit(panel, LongListPrompt, options);
            StreamingTextProbe stopProbe = new();
            yield return WaitForVisibleStreamingText(
                stopTask, panel, chatService, LongListPrompt, stopProbe, 240f, "cancellable real-model stream",
                priorTurnCompleted: true);
            if (stopTask.IsCompleted)
            {
                // WHY a skip is affordable here: only the frozen-text check below needs a sampled bubble.
                // Stop, the null result, the unlock and the follow-up turn are pinned without one by
                // CoreAiChatDemo_RealModel_StopCancelsTheTurnAndChatRecovers.
                Assert.Ignore(
                    $"{LogPrefix} Real model completed before Stop could cancel it; use a slower model or rerun.");
            }

            Label stoppedLabel = stopProbe.Label;
            string textAtStop = stoppedLabel?.text ?? string.Empty;
            Debug.Log($"{LogPrefix} Stop before text: '{TrimForLog(textAtStop)}'");
            panel.StopAgent();
            yield return WaitTask(stopTask, 120f, "stopped real-model response");
            Assert.IsNull(stopTask.Result, $"{LogPrefix} Stop should cancel the active turn and return null.");
            yield return WaitUntil(() => !panel.IsBusy, 10f, "chat panel unlock after Stop");
            yield return null;
            yield return null;
            Assert.AreEqual(textAtStop, stoppedLabel?.text ?? string.Empty,
                $"{LogPrefix} Streaming text changed after Stop.");
            Debug.Log($"{LogPrefix} Stop cancelled streaming and left chat usable.");

            Task<string> thirdTask = Submit(panel,
                "Give a short response showing chat still works.",
                options);
            yield return WaitTask(thirdTask, 120f, "third real-model response");
            Assert.IsFalse(string.IsNullOrWhiteSpace(thirdTask.Result), $"{LogPrefix} Third response is empty.");
            Assert.IsFalse(panel.IsBusy, $"{LogPrefix} Chat panel stayed busy after third response.");
            Debug.Log($"{LogPrefix} Third final response after Stop: '{TrimForLog(thirdTask.Result)}'");
        }

        /// <summary>
        /// The Stop contract without a sampled stream: a turn is stopped the frame after it is admitted,
        /// then cancellation, the null result, the unlock, a frozen transcript and a working follow-up
        /// turn are asserted — none of which needs partial text on screen.
        /// </summary>
        [UnityTest]
        [Category("RealLlm")]
        [Category("WebGL")]
        [Timeout(600000)]
        public IEnumerator CoreAiChatDemo_RealModel_StopCancelsTheTurnAndChatRecovers()
        {
            // WHY a second test rather than a flag in the first: the streaming test skips whenever the
            // endpoint hands over its whole answer before a frame can sample it, and that skip used to take
            // Stop, the null result, the unlock and the follow-up turn down with it. None of those need a
            // partial render, so they run here on every endpoint the demo can reach at all.
            DemoChat demo = new();
            yield return OpenDemoChat(demo);
            CoreAiChatPanel panel = demo.Panel;

            Debug.Log(
                $"{LogPrefix} Starting real-model Stop test. " +
                $"ExecutionMode={demo.Settings.ExecutionMode}, BackendType={demo.Settings.BackendType}, " +
                $"BaseUrl={demo.Settings.ApiBaseUrl}, Model={demo.Settings.ModelName}, " +
                $"Streaming={demo.ChatService.IsStreamingEnabled(RoleId, GetPanelUiStreaming(panel))}");

            CoreAiChatExternalSubmitOptions options = new() { AppendUserMessageToChat = true };

            Task<string> stopTask = Submit(panel, LongListPrompt, options);
            // WHY one frame: the turn's first suspension is a Task.Yield before its request is even built,
            // so the frame after admission is the first in which the request can be on the wire, and no
            // model answers eighty lines inside one frame. The panel publishes busy before that yield, so a
            // turn that is not busy here was refused — a refused submit resolves to null at once and would
            // make the null check below vacuous.
            yield return null;
            Assert.IsTrue(panel.IsBusy && !stopTask.IsCompleted,
                $"{LogPrefix} The cancellable turn is not in flight: IsBusy={panel.IsBusy}, " +
                $"TaskStatus={stopTask.Status}, FinalResult='{DescribeCompletedTaskResult(stopTask)}'.");

            panel.StopAgent();
            string bubblesAtStop = DescribeAssistantBubbles(panel);
            yield return WaitTask(stopTask, 120f, "stopped real-model response");
            Assert.IsNull(stopTask.Result, $"{LogPrefix} Stop should cancel the active turn and return null.");
            yield return WaitUntil(() => !panel.IsBusy, 10f, "chat panel unlock after Stop");
            yield return null;
            yield return null;
            Assert.AreEqual(bubblesAtStop, DescribeAssistantBubbles(panel),
                $"{LogPrefix} Assistant text changed after Stop.");
            Debug.Log($"{LogPrefix} Stop cancelled the admitted turn and left chat usable.");

            Task<string> nextTask = Submit(panel,
                "Give a short response showing chat still works.",
                options);
            yield return WaitTask(nextTask, 120f, "real-model response after Stop");
            Assert.IsFalse(string.IsNullOrWhiteSpace(nextTask.Result), $"{LogPrefix} Response after Stop is empty.");
            Assert.IsFalse(panel.IsBusy, $"{LogPrefix} Chat panel stayed busy after the response that followed Stop.");
            Debug.Log($"{LogPrefix} Final response after Stop: '{TrimForLog(nextTask.Result)}'");
        }

        /// <summary>
        /// Loads the demo scene against the endpoint the environment names, keys its persisted SmartChat
        /// data under <see cref="TestMemoryScope"/>, starts it from an empty transcript and store, and hands
        /// back its panel, service and settings; skips the configurations the live demo cannot run under.
        /// </summary>
        private IEnumerator OpenDemoChat(DemoChat demo)
        {
            // WHY before the scene load: CoreAILifetimeScope picks the client TYPE from the asset's
            // ExecutionMode while its container builds, so a base URL and model applied after
            // LoadSceneAsync would reach a client that was already built as LLMUnity or Offline.
            RetargetSharedSettingsFromTestEnvironment();

            // WHY Enqueue rather than CoreAILifetimeScope.SetAgentMemoryScopeProvider: the scene's scope
            // builds its container in Awake while the load completes, so no frame exists in which the test
            // holds the scope unbuilt. VContainer runs the installer queued here after Configure, and a later
            // registration of the same interface is the one the container resolves, so the decorators that
            // key memory, history, transcript and summary all take the test scope. Disposed as soon as the
            // load returns, so no scope built later in the run inherits it.
            using (LifetimeScope.Enqueue(builder => builder.RegisterInstance<IAgentMemoryScopeProvider>(TestMemoryScope)))
            {
                yield return SceneManager.LoadSceneAsync(SceneName, LoadSceneMode.Single);
            }

            yield return null;
            yield return null;

            CoreAiChatPanel panel = Object.FindFirstObjectByType<CoreAiChatPanel>();
            Assert.NotNull(panel, $"{LogPrefix} {SceneName} does not contain CoreAiChatPanel.");

            panel.SetCollapsed(false, false);
            yield return WaitForChatService(panel, 10f);

            CoreAiChatService chatService = panel.ChatService;
            if (chatService == null)
            {
                Assert.Ignore($"{LogPrefix} CoreAiChatService is not available in {SceneName}.");
            }

            CoreAISettingsAsset settings = CoreAISettingsAsset.Instance;
            if (settings == null)
            {
                Assert.Ignore($"{LogPrefix} CoreAISettingsAsset is not available in Resources.");
            }

            // WHY this guard only ever sees the untouched asset: with COREAI_TEST_BASE_URL/MODEL set the
            // asset was retargeted to HTTP before the scene loaded, so an Offline asset plus env vars runs
            // live instead of skipping. The env vars are an explicit per-run instruction, while Offline is
            // the committed default that keeps unattended runs from booting a local model; letting it veto
            // the env vars would leave a CI shell with no way to ever run this test.
            if (settings.ExecutionMode == LlmExecutionMode.Offline || settings.BackendType == LlmBackendType.Offline)
            {
                Assert.Ignore($"{LogPrefix} CoreAISettingsAsset is configured for Offline mode.");
            }

            // WHY the clear on a scope that is already the test's own: a run killed before its teardown
            // leaves the test-scoped files behind, and the panel has by now hydrated them into the
            // transcript — and would hand them to the model as context. The service call settles the
            // history through the scoped store even when the CoreAi facade cannot resolve the scene's scope;
            // ClearChat then drops the bubbles, the summary and the long-term memory the same way the demo's
            // own Clear button does.
            chatService.ClearHistory(RoleId);
            panel.ClearChat(true, true);
            yield return null;

            demo.Panel = panel;
            demo.ChatService = chatService;
            demo.Settings = settings;
        }

        /// <summary>
        /// Points the shared settings asset at the endpoint named by <c>COREAI_TEST_BASE_URL</c> /
        /// <c>COREAI_TEST_MODEL</c> for the duration of this test; a no-op when neither is set, so the
        /// asset-driven behaviour is untouched for every run that does not set them.
        /// </summary>
        private void RetargetSharedSettingsFromTestEnvironment()
        {
            // WHY this override exists: the demo scene composes its LLM client from the shared
            // Resources/CoreAISettings asset, so this was the one live fixture the COREAI_TEST_* env vars
            // honoured by every other live fixture (PlayModeOpenAiTestConfig) could not reach. A shell that
            // points the live suite at a bridge must reach this test too, without editing the committed asset.
            if (!IsTestEnvironmentEndpointSet())
            {
                return;
            }

            CoreAISettingsAsset settings = CoreAISettingsAsset.Instance;
            if (settings == null)
            {
                Assert.Ignore(
                    $"{LogPrefix} {PlayModeOpenAiTestConfig.EnvBaseUrl}/{PlayModeOpenAiTestConfig.EnvModel} are set " +
                    "but CoreAISettingsAsset is not available in Resources; there is no asset to retarget.");
            }

            // WHY the shared resolver and not the raw env values: the sibling fixtures resolve each field
            // as env, then the asset when it already drives HTTP, then the gitignored local file; the same
            // precedence here means one env var (say only the model) completes the way it does everywhere else.
            PlayModeOpenAiTestConfig.ResolvedConfig config = PlayModeOpenAiTestConfig.Resolve();
            if (!config.IsComplete)
            {
                Assert.Ignore($"{LogPrefix} {PlayModeOpenAiTestConfig.BuildIgnoreReason(config)}");
            }

            // WHY the snapshot is taken before the first write: an exception between the writes must still
            // hand RestoreSharedSettings a complete pre-test state.
            _sharedSettings = settings;
            _sharedSettingsSnapshotJson = JsonUtility.ToJson(settings);

            string originalBackend = $"{settings.BackendType}/{settings.ExecutionMode}";
            // WHY SetModelResolution rather than SetModelName: the backend and execution mode must flip to
            // HTTP in the same step, otherwise an LLMUnity or Offline asset keeps building its old client
            // and the base URL and model are never read.
            settings.SetModelResolution(
                LlmExecutionMode.ClientOwnedApi,
                LlmBackendType.OpenAiHttp,
                config.Model,
                settings.GgufModelPath);
            settings.SetApiBaseUrl(config.BaseUrl);
            settings.SetApiKey(config.ApiKey);
#if UNITY_EDITOR
            // WHY: the asset is committed to the repo; a dirty flag would let any later Save Project write
            // the test endpoint into the developer's file.
            EditorUtility.ClearDirty(settings);
#endif

            Debug.Log(
                $"{LogPrefix} Shared CoreAISettings retargeted for this test from {originalBackend} to " +
                $"OpenAiHttp/ClientOwnedApi at {config.BaseUrl} with model '{config.Model}'; " +
                "RestoreSharedSettings puts it back in TearDown.");
        }

        private static bool IsTestEnvironmentEndpointSet()
        {
            return !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(PlayModeOpenAiTestConfig.EnvBaseUrl))
                   || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(PlayModeOpenAiTestConfig.EnvModel));
        }

        [TearDown]
        public void RestoreSharedSettings()
        {
            // WHY the restore is unconditional: CoreAISettingsAsset.Instance is the committed, project-wide
            // asset that every later test in the run and the developer's own Play sessions read, and this
            // project disables domain reload on entering play mode, so an in-memory edit outlives both this
            // test and this play session. The retarget therefore has to be undone on every exit — pass,
            // assertion failure, timeout, exception and Assert.Ignore — which is exactly what [TearDown]
            // gives: the Test Framework records the test outcome and then runs it regardless, before
            // [UnityTearDown] unloads the scene. Gating it on the outcome or moving it to the end of the
            // test body would leak the bridge endpoint into everything that runs afterwards.
            //
            // WHY JsonUtility and not the EditorJsonUtility the FastNoLlm smoke uses: this fixture also
            // compiles into the WebGL player (its guard has no !UNITY_WEBGL), and the asset serializes only
            // primitives, strings and enums, so the runtime round-trip restores the identical field set.
            if (_sharedSettings != null && !string.IsNullOrEmpty(_sharedSettingsSnapshotJson))
            {
                JsonUtility.FromJsonOverwrite(_sharedSettingsSnapshotJson, _sharedSettings);
#if UNITY_EDITOR
                EditorUtility.ClearDirty(_sharedSettings);

                string assetPath = AssetDatabase.GetAssetPath(_sharedSettings);
                if (!string.IsNullOrEmpty(assetPath))
                {
                    // WHY: the committed file is the authority; the reimport guarantees nothing this test
                    // touched survives in memory, even if a future edit adds a field the JSON missed.
                    AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
                }
#endif
            }

            _sharedSettings = null;
            _sharedSettingsSnapshotJson = null;
        }

        private static Task<string> Submit(
            CoreAiChatPanel panel,
            string message,
            CoreAiChatExternalSubmitOptions options)
        {
            return SubmitInner(panel, message, options);

            static async Task<string> SubmitInner(
                CoreAiChatPanel panel,
                string message,
                CoreAiChatExternalSubmitOptions options)
            {
                return await panel.SubmitMessageFromExternalAsync(message, options);
            }
        }

        private static IEnumerator WaitForChatService(CoreAiChatPanel panel, float timeoutSeconds)
        {
            float started = Time.realtimeSinceStartup;
            while (panel.ChatService == null && Time.realtimeSinceStartup - started <= timeoutSeconds)
            {
                yield return null;
            }
        }

        private static IEnumerator WaitForVisibleStreamingText(
            Task<string> task,
            CoreAiChatPanel panel,
            CoreAiChatService chatService,
            string prompt,
            StreamingTextProbe probe,
            float timeoutSeconds,
            string operationName,
            bool priorTurnCompleted)
        {
            float started = Time.realtimeSinceStartup;
            bool timedOut = false;
            while (!task.IsCompleted)
            {
                Label label = GetStreamingLabel(panel);
                string text = label?.text ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(text))
                {
                    probe.Label = label;
                    probe.Text = text;
                    yield break;
                }

                if (Time.realtimeSinceStartup - started > timeoutSeconds)
                {
                    timedOut = true;
                    break;
                }

                yield return null;
            }

            if (timedOut)
            {
                // WHY the deadline alone decides nothing: a pending turn with nothing rendered looks the same
                // for a slow first token, a reasoning-only prelude and a product that never surfaces deltas.
                // Stop splits off the hang — a turn that ignores it is the product's. The pacing probe below
                // then repeats the prompt through the same service so the skip message says how this endpoint
                // paced it; it can neither clear nor blame the panel, see JudgeUnsampledTurn.
                StopActiveTurn(panel, task, operationName);
                float stopIssued = Time.realtimeSinceStartup;
                while (!task.IsCompleted && Time.realtimeSinceStartup - stopIssued <= StopSettleTimeoutSeconds)
                {
                    yield return null;
                }
            }

            string prelude = timedOut
                ? $"{LogPrefix} No visible streaming text within {timeoutSeconds:0.#}s: {operationName}"
                : $"{LogPrefix} {operationName} completed before any streaming text was visible";
            string diagnostics =
                $"TaskStatus={task.Status}; visibleLabels='{DescribeVisibleLabels(panel)}'; " +
                $"FinalResult='{DescribeCompletedTaskResult(task)}'.";
            FailTurnThatBroke(task, timedOut, prelude, diagnostics);

            if (timedOut && !priorTurnCompleted)
            {
                // WHY no probe yet: nothing has completed on this endpoint, so a model still loading when the
                // wait expired would answer the probe warm and the panel would be blamed for a cold start.
                Assert.Ignore(
                    $"{prelude}; the endpoint produced no visible content within the wait and Stop cancelled the turn cleanly. " +
                    $"{diagnostics} The backend may be streaming only reasoning_content or taking too long before the first " +
                    $"visible content token. {ReasoningHint}");
            }

            PacingProbe pacing = new();
            yield return ProbeServicePacing(chatService, GetPanelUiStreaming(panel), prompt, pacing);
            JudgeUnsampledTurn(prelude, diagnostics, pacing);
        }

        /// <summary>
        /// Fails a turn that never showed partial text for the reasons that are the product's whatever the
        /// endpoint did: it did not settle after Stop, it ended in an error, or it completed with nothing.
        /// </summary>
        private static void FailTurnThatBroke(Task<string> task, bool stoppedAfterTimeout, string prelude, string diagnostics)
        {
            if (!task.IsCompleted)
            {
                Assert.Fail(
                    $"{prelude}; the turn did not settle within {StopSettleTimeoutSeconds:0.#}s after Stop. {diagnostics} " +
                    "A turn that neither renders text nor honours Stop is a product hang, not a slow endpoint.");
            }

            if (task.IsFaulted || task.IsCanceled)
            {
                Assert.Fail(
                    $"{prelude}; the turn ended in an error. {diagnostics} " +
                    "The backend likely failed before the first SSE chunk (for example connection refused/CORS).");
            }

            if (!stoppedAfterTimeout && string.IsNullOrWhiteSpace(task.Result))
            {
                Assert.Fail(
                    $"{prelude}; the turn completed with no visible assistant content at all. {diagnostics} " +
                    $"The backend likely exhausted generation in reasoning_content and produced no visible assistant content. {ReasoningHint}");
            }
        }

        /// <summary>
        /// Repeats an unsampled prompt through the panel's own service, bypassing the panel, and records how
        /// the service paced it: whether visible content reached the test while the turn was still open,
        /// how many polled frames it stayed open after that (the probe stops at
        /// <see cref="PacingProbeOpenFrames"/>), and when each chunk reached the callback, so content that
        /// arrived only in the completion frame is counted and dated. The record feeds the skip message;
        /// only a probe that ignores cancellation afterwards turns into a verdict.
        /// </summary>
        private static IEnumerator ProbeServicePacing(
            CoreAiChatService chatService,
            bool panelUiStreaming,
            string prompt,
            PacingProbe pacing)
        {
            CancellationTokenSource cancellation = new();
            // WHY a stopwatch and not Time.realtimeSinceStartup: the chunk callback may run off the main
            // thread, where Unity's clock throws; this one is readable anywhere and every timestamp in the
            // probe shares it.
            System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
            Task<string> task = chatService.SendMessageSmartAsync(
                prompt,
                RoleId,
                chunk => pacing.RecordChunk(chunk, clock.Elapsed.TotalSeconds),
                panelUiStreaming,
                cancellation.Token);

            while (!task.IsCompleted)
            {
                if (pacing.ContentChunks > 0)
                {
                    if (pacing.FirstContentSeconds < 0f)
                    {
                        pacing.FirstContentSeconds = (float)clock.Elapsed.TotalSeconds;
                    }

                    if (++pacing.OpenFramesWithContent >= PacingProbeOpenFrames)
                    {
                        break;
                    }
                }
                else
                {
                    if (pacing.ReasoningChunks > 0)
                    {
                        pacing.OpenFramesWithReasoning++;
                    }

                    if (clock.Elapsed.TotalSeconds > PacingProbeTimeoutSeconds)
                    {
                        pacing.TimedOut = true;
                        break;
                    }
                }

                yield return null;
            }

            pacing.Seconds = (float)clock.Elapsed.TotalSeconds;
            pacing.OpenAtExit = !task.IsCompleted;
            cancellation.Cancel();
            float cancelled = Time.realtimeSinceStartup;
            while (!task.IsCompleted && Time.realtimeSinceStartup - cancelled <= StopSettleTimeoutSeconds)
            {
                yield return null;
            }

            pacing.Settled = task.IsCompleted;
            pacing.Error = task.IsFaulted ? task.Exception?.GetBaseException().Message : null;
            if (task.IsCompleted)
            {
                cancellation.Dispose();
            }
        }

        /// <summary>
        /// Ends the test for a settled turn that never showed partial text. The one verdict the probe can
        /// still return is a hang: a probe that ignored cancellation fails. Everything else is skipped with
        /// the probe's frame counts and arrival times in the message — the probe is a second request, and
        /// what it saw does not carry over to the turn that was not sampled.
        /// </summary>
        private static void JudgeUnsampledTurn(string prelude, string diagnostics, PacingProbe pacing)
        {
            string pacingReport = pacing.Describe();
            if (!pacing.Settled)
            {
                Assert.Fail(
                    $"{prelude}; the pacing probe then ignored cancellation for {StopSettleTimeoutSeconds:0.#}s ({pacingReport}), " +
                    $"a hang the product owns. {diagnostics}");
            }

            // WHY a skip whatever the probe saw: a replaying endpoint (a local OpenAI-compatible bridge that
            // generates the whole answer and then plays it back as many SSE events) bursts with a timing of
            // its own on every request — one burst lands between two polled frames, the next is spread over
            // three. A probe that saw content while its turn was open therefore does not show that the
            // panel's turn had anything to show while it was open, and a probe that received every delta in
            // its completion frame does not clear the panel either; from inside the client a flushed stream
            // and a replayed one are the same bytes on the same clock. The split is made where the pacing is
            // controlled: CoreAiChatPanelPacedSseStreamingPlayModeTests hands the same client, service and
            // panel a body whose events are stamped on the test's own clock, and fails when the bubble shows
            // nothing before the last content event was handed out. The counts and times stay in this
            // message so a human reading the run still sees how this endpoint paced.
            Assert.Ignore(
                $"{prelude}; the same service then ran this prompt a second time ({pacingReport}). " +
                "A second request's pacing does not decide whether the panel showed the first one's deltas — " +
                "a replaying endpoint bursts differently on every request — so this endpoint cannot be sampled " +
                "mid-stream from a live test; CoreAiChatPanelPacedSseStreamingPlayModeTests decides panel pacing " +
                $"with a paced body. {diagnostics} {ReasoningHint} Use a slower model or a longer answer to observe streaming.");
        }

        /// <summary>
        /// The panel's own streaming switch, read the way the panel reads it, so the pacing probe asks the
        /// service for the stream the panel would have asked for and never for one it never requested.
        /// </summary>
        private static bool GetPanelUiStreaming(CoreAiChatPanel panel)
        {
            PropertyInfo optionsProperty = typeof(CoreAiChatPanel).GetProperty(
                "Options",
                BindingFlags.Instance | BindingFlags.NonPublic);
            object options = optionsProperty?.GetValue(panel);
            PropertyInfo enableStreaming = options?.GetType().GetProperty("EnableStreaming");
            return enableStreaming?.GetValue(options) is bool enabled ? enabled : true;
        }

        /// <summary>
        /// Erases the test-scoped SmartChat history, summary and long-term memory while the scene is still
        /// loaded, after a turn the test left in flight has been stopped and has settled. Never throws: a
        /// scrub that failed is logged, and the scene unload that follows must still run.
        /// </summary>
        private static IEnumerator ScrubTestScopedChatHistory()
        {
            CoreAiChatPanel panel = Object.FindFirstObjectByType<CoreAiChatPanel>();
            if (panel == null)
            {
                yield break;
            }

            if (panel.IsBusy)
            {
                panel.StopAgent();
                float stopped = Time.realtimeSinceStartup;
                while (panel.IsBusy && Time.realtimeSinceStartup - stopped <= TearDownSettleTimeoutSeconds)
                {
                    yield return null;
                }

                if (panel.IsBusy)
                {
                    Debug.LogWarning(
                        $"{LogPrefix} A turn was still busy {TearDownSettleTimeoutSeconds:0.#}s after Stop in TearDown; " +
                        "its late history write may leave a test-scoped file behind.");
                }
            }

            try
            {
                // WHY both calls: the facade clears history, summary and long-term memory through the scope's
                // decorators but silently does nothing when it cannot resolve the scene's scope; the service
                // reaches the same scoped store directly and settles the history either way.
                CoreAi.ClearContext(RoleId, clearChatHistory: true, clearLongTermMemory: true);
                panel.ChatService?.ClearHistory(RoleId);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{LogPrefix} Scrubbing the test-scoped chat history failed: {ex.Message}");
            }
        }

        private static void StopActiveTurn(CoreAiChatPanel panel, Task<string> task, string operationName)
        {
            if (panel == null || task == null || task.IsCompleted || !panel.IsBusy)
            {
                return;
            }

            Debug.LogWarning($"{LogPrefix} Stopping active turn after streaming wait timeout: {operationName}.");
            panel.StopAgent();
        }

        private static IEnumerator WaitTask(Task task, float timeoutSeconds, string operationName)
        {
            float started = Time.realtimeSinceStartup;
            while (!task.IsCompleted)
            {
                if (Time.realtimeSinceStartup - started > timeoutSeconds)
                {
                    Assert.Fail($"{LogPrefix} Timeout waiting for {operationName} after {timeoutSeconds:0.#}s.");
                }

                yield return null;
            }

            if (task.IsCanceled)
            {
                Assert.Fail($"{LogPrefix} Task was canceled: {operationName}.");
            }

            if (task.IsFaulted)
            {
                Assert.Fail(
                    $"{LogPrefix} Task faulted during {operationName}: {task.Exception?.GetBaseException().Message}");
            }
        }

        private static IEnumerator WaitUntil(Func<bool> predicate, float timeoutSeconds, string operationName)
        {
            float started = Time.realtimeSinceStartup;
            while (!predicate())
            {
                if (Time.realtimeSinceStartup - started > timeoutSeconds)
                {
                    Assert.Fail($"{LogPrefix} Timeout waiting for {operationName} after {timeoutSeconds:0.#}s.");
                }

                yield return null;
            }
        }

        private static Label GetStreamingLabel(CoreAiChatPanel panel)
        {
            FieldInfo field = typeof(CoreAiChatPanel).GetField(
                "_streamingLabel",
                BindingFlags.Instance | BindingFlags.NonPublic);
            return field?.GetValue(panel) as Label;
        }

        private static VisualElement GetRoot(CoreAiChatPanel panel)
        {
            FieldInfo field = typeof(CoreAiChatPanel).GetField(
                "Root",
                BindingFlags.Instance | BindingFlags.NonPublic);
            return field?.GetValue(panel) as VisualElement;
        }

        private static string DescribeVisibleLabels(CoreAiChatPanel panel)
        {
            VisualElement root = GetRoot(panel);
            if (root == null)
            {
                return "<no root>";
            }

            List<Label> labels = new();
            root.Query<Label>().ToList(labels);
            List<string> texts = new();
            foreach (Label label in labels)
            {
                string text = label?.text;
                if (!string.IsNullOrWhiteSpace(text))
                {
                    texts.Add(TrimForLog(text));
                }
            }

            return texts.Count == 0 ? "<no labels>" : string.Join(" | ", texts);
        }

        /// <summary>Every non-empty assistant bubble in order, for the before/after comparison around Stop.</summary>
        private static string DescribeAssistantBubbles(CoreAiChatPanel panel)
        {
            VisualElement root = GetRoot(panel);
            if (root == null)
            {
                return "<no root>";
            }

            List<Label> bubbles = new();
            root.Query<Label>(className: "coreai-ai-message").ToList(bubbles);
            List<string> texts = new();
            foreach (Label bubble in bubbles)
            {
                string text = bubble?.text;
                if (!string.IsNullOrWhiteSpace(text))
                {
                    texts.Add(text);
                }
            }

            return texts.Count == 0 ? "<no assistant bubbles>" : string.Join(" | ", texts);
        }

        private static string DescribeCompletedTaskResult(Task<string> task)
        {
            if (task == null)
            {
                return "<null task>";
            }

            if (!task.IsCompleted)
            {
                return "<not completed>";
            }

            if (task.IsCanceled)
            {
                return "<canceled>";
            }

            if (task.IsFaulted)
            {
                return $"<faulted: {task.Exception?.GetBaseException().Message}>";
            }

            string result = task.Result;
            return string.IsNullOrWhiteSpace(result) ? "<empty>" : TrimForLog(result);
        }

        private static string TrimForLog(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            const int max = 160;
            string normalized = text.Replace("\r", " ").Replace("\n", " ");
            return normalized.Length <= max ? normalized : normalized.Substring(0, max) + "...";
        }

        private sealed class StreamingTextProbe
        {
            public Label Label;
            public string Text = string.Empty;
        }

        private sealed class DemoChat
        {
            public CoreAiChatPanel Panel;
            public CoreAiChatService ChatService;
            public CoreAISettingsAsset Settings;
        }

        /// <summary>One declared scope for every role, the way a host isolates a learner or a session.</summary>
        private sealed class FixedAgentMemoryScopeProvider : IAgentMemoryScopeProvider
        {
            private readonly AgentMemoryScope _scope;

            public FixedAgentMemoryScopeProvider(AgentMemoryScope scope)
            {
                _scope = scope;
            }

            public AgentMemoryScope GetScope(string roleId)
            {
                return _scope;
            }
        }

        /// <summary>
        /// What the service's second run of an unsampled prompt looked like from the test: chunk counts
        /// and arrival times on the probe's clock, the polled frames the turn stayed open with content or
        /// reasoning, and how the probe ended. Reported in the skip message; only <see cref="Settled"/>
        /// decides anything.
        /// </summary>
        private sealed class PacingProbe
        {
            private readonly object _gate = new();
            private int _contentChunks;
            private int _reasoningChunks;
            private double _firstContentArrivalSeconds = -1d;
            private double _lastContentArrivalSeconds = -1d;

            public int OpenFramesWithContent;
            public int OpenFramesWithReasoning;
            public float FirstContentSeconds = -1f;
            public float Seconds;
            /// <summary>The turn was still open when the probe stopped polling — at the frame cap or the wait's end.</summary>
            public bool OpenAtExit;
            public bool TimedOut;
            public bool Settled;
            public string Error;

            /// <summary>Content deltas that reached the callback so far, including any that arrived in the completion frame.</summary>
            public int ContentChunks
            {
                get
                {
                    lock (_gate)
                    {
                        return _contentChunks;
                    }
                }
            }

            public int ReasoningChunks
            {
                get
                {
                    lock (_gate)
                    {
                        return _reasoningChunks;
                    }
                }
            }

            public double FirstContentArrivalSeconds
            {
                get
                {
                    lock (_gate)
                    {
                        return _firstContentArrivalSeconds;
                    }
                }
            }

            public double LastContentArrivalSeconds
            {
                get
                {
                    lock (_gate)
                    {
                        return _lastContentArrivalSeconds;
                    }
                }
            }

            /// <summary>Records one chunk from the service's callback, on whichever thread it arrives.</summary>
            public void RecordChunk(LlmStreamChunk chunk, double seconds)
            {
                lock (_gate)
                {
                    if (!string.IsNullOrEmpty(chunk.ReasoningText))
                    {
                        _reasoningChunks++;
                    }

                    if (string.IsNullOrEmpty(chunk.Text))
                    {
                        return;
                    }

                    _contentChunks++;
                    if (_firstContentArrivalSeconds < 0d)
                    {
                        _firstContentArrivalSeconds = seconds;
                    }

                    _lastContentArrivalSeconds = seconds;
                }
            }

            public string Describe()
            {
                int contentChunks = ContentChunks;
                string content = FirstContentSeconds >= 0f
                    ? $"first visible content after {FirstContentSeconds:0.#}s, {contentChunks} content chunk(s) by {Seconds:0.#}s"
                    : contentChunks == 0
                        ? $"no visible content within {Seconds:0.#}s"
                        : $"no visible content in any open frame within {Seconds:0.#}s, yet {contentChunks} content delta(s) " +
                          $"reached the callback between {FirstContentArrivalSeconds:0.##}s and {LastContentArrivalSeconds:0.##}s";
                string reasoning = ReasoningChunks == 0
                    ? string.Empty
                    : $", {ReasoningChunks} reasoning delta(s) seen across {OpenFramesWithReasoning} open frame(s)";
                string turn = TimedOut
                    ? "wait expired with the turn still open"
                    : OpenAtExit
                        ? $"turn still open across {OpenFramesWithContent} polled frame(s) with content"
                        : $"turn completed within {OpenFramesWithContent} polled frame(s) of its content";
                string error = Error == null ? string.Empty : $", probe error: {Error}";
                return $"service pacing: {content}{reasoning}, {turn}{error}";
            }
        }
    }
}
#endif
