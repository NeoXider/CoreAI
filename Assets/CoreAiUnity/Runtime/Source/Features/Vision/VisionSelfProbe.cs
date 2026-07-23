using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Composition;
using CoreAI.Infrastructure.Llm;
using Microsoft.Extensions.AI;
using UnityEngine;

namespace CoreAI.Vision
{
    /// <summary>
    /// Adaptive vision capability probe: sends the currently-routed model a deterministic in-memory test
    /// image (a solid pure-green square) and asks for its dominant color. A correct reply proves the
    /// backend actually parses image parts, upgrading the model-name heuristic of
    /// <see cref="VisionCapability"/> to an empirical answer. Results are cached per model name so
    /// re-probing the same model is instant. The probe intentionally BYPASSES the
    /// <see cref="CoreAISettingsAsset.IsVisionEnabled"/> gate — it must be able to test a model whose
    /// vision support is currently Off/undetected — by issuing the image request directly through the
    /// resolved <see cref="ILlmClient"/>, mirroring the chat service's user-image message shape.
    /// </summary>
    public sealed class VisionSelfProbe
    {
        private const int ProbeImageSize = 96;
        private const string ExpectedColorWord = "green";

        private const string ProbePrompt =
            "Reply with ONLY the single dominant color word of this image.";

        // WHY: pure green is unambiguous across models and JPEG-free PNG encoding keeps it exact;
        // deterministic by design (no Random) so the expected answer is always "green".
        private static readonly Color32 ProbeColor = new(0, 255, 0, 255);

        // WHY: probe outcomes are stable per model, so a static cache lets every probe instance and
        // repeated UI clicks reuse the first definitive answer without another network roundtrip.
        private static readonly Dictionary<string, bool> ResultsByModel = new(StringComparer.OrdinalIgnoreCase);
        private static readonly object CacheLock = new();

        /// <summary>
        /// Returns a previously probed result for <paramref name="modelName"/> without any network call.
        /// </summary>
        public bool TryGetCached(string modelName, out bool visionOk)
        {
            visionOk = false;
            if (string.IsNullOrWhiteSpace(modelName))
            {
                return false;
            }

            lock (CacheLock)
            {
                return ResultsByModel.TryGetValue(modelName.Trim(), out visionOk);
            }
        }

        /// <summary>
        /// Sends one completion carrying the test image to the currently-routed model and returns
        /// <c>true</c> iff the reply contains the expected color word. Returns <c>false</c> (with a
        /// warning) on any error, timeout, or when no CoreAI scope is running. Definitive model replies
        /// (successful completions) are cached per model name; transport failures are NOT cached so a
        /// temporarily unreachable backend can be re-probed. Must be called from the Unity main thread
        /// (the test texture is built with the Unity API).
        /// </summary>
        public async Task<bool> ProbeAsync(int timeoutSeconds = 30, CancellationToken ct = default)
        {
            CoreAISettingsAsset settings = CoreAISettingsAsset.Instance;
            string modelName = settings != null ? settings.ModelName : "";
            if (TryGetCached(modelName, out bool cached))
            {
                return cached;
            }

            if (!TryResolveClient(out ILlmClient client))
            {
                Debug.LogWarning(
                    "[VisionSelfProbe] CoreAI scope is not running (no CoreAILifetimeScope with a built " +
                    "container); cannot probe vision support.");
                return false;
            }

            byte[] png;
            try
            {
                png = BuildProbePng();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[VisionSelfProbe] Failed to build the test image: {ex.Message}");
                return false;
            }

            try
            {
                using CancellationTokenSource timeoutCts = new(TimeSpan.FromSeconds(
                    timeoutSeconds <= 0 ? 30 : timeoutSeconds));
                using CancellationTokenSource linked =
                    CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

                // TODO: verify the image-content wire shape live against the backend — the probe assumes
                // the provider client serializes a user ChatMessage DataContent ("image/png") to an
                // OpenAI image_url part exactly like the camera send path does with image/jpeg.
                Microsoft.Extensions.AI.ChatMessage userMessage = new(ChatRole.User, new List<AIContent>
                {
                    new TextContent(ProbePrompt),
                    new DataContent(png, "image/png")
                });

                LlmCompletionRequest request = new()
                {
                    AgentRoleId = BuiltInAgentRoleIds.SmartChat,
                    SystemPrompt = "",
                    UserPayload = "",
                    ChatHistory = new List<Microsoft.Extensions.AI.ChatMessage> { userMessage },
                    // WHY: generous budget for reasoning models whose thinking counts toward max_tokens;
                    // the visible answer is one word so the actual cost stays negligible.
                    MaxOutputTokens = 128000
                };

                LlmCompletionResult result = await client.CompleteAsync(request, linked.Token);
                if (result == null || !result.Ok)
                {
                    Debug.LogWarning(
                        "[VisionSelfProbe] Probe request failed: " +
                        (result == null ? "null result" : string.IsNullOrEmpty(result.Error)
                            ? "unknown error"
                            : result.Error));
                    return false;
                }

                // WHY: a successful completion is a definitive answer either way — a text-only model
                // that ignored the image part and replied nonsense is exactly the Off signal we want.
                bool visionOk = (result.Content ?? "").Trim()
                    .IndexOf(ExpectedColorWord, StringComparison.OrdinalIgnoreCase) >= 0;
                StoreResult(modelName, visionOk);
                return visionOk;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                Debug.LogWarning(
                    $"[VisionSelfProbe] Probe timed out after {(timeoutSeconds <= 0 ? 30 : timeoutSeconds)}s.");
                return false;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[VisionSelfProbe] Probe failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Runs <see cref="ProbeAsync"/> and persists the outcome into <paramref name="asset"/> as an
        /// explicit <see cref="VisionSupportMode.On"/> / <see cref="VisionSupportMode.Off"/>, replacing
        /// the Auto model-name heuristic with the measured answer. Null-tolerant: with a <c>null</c>
        /// asset the probe still runs and the detected mode is returned without being persisted.
        /// </summary>
        public async Task<VisionSupportMode> DetectAndApplyAsync(
            CoreAISettingsAsset asset, int timeoutSeconds = 30, CancellationToken ct = default)
        {
            bool visionOk = await ProbeAsync(timeoutSeconds, ct);
            VisionSupportMode mode = visionOk ? VisionSupportMode.On : VisionSupportMode.Off;
            asset?.SetVisionSupport(mode);
            return mode;
        }

        private static void StoreResult(string modelName, bool visionOk)
        {
            if (string.IsNullOrWhiteSpace(modelName))
            {
                return;
            }

            lock (CacheLock)
            {
                ResultsByModel[modelName.Trim()] = visionOk;
            }
        }

        // WHY: mirrors CoreAiBackend's scope resolution so the probe stays self-contained and works
        // from any caller (Hub UI, tests) whenever a CoreAI scope is up.
        private static bool TryResolveClient(out ILlmClient client)
        {
            client = null;
            CoreAILifetimeScope scope =
                UnityEngine.Object.FindAnyObjectByType<CoreAILifetimeScope>(FindObjectsInactive.Include);
            if (scope == null || scope.Container == null)
            {
                return false;
            }

            try
            {
                if (scope.Container.Resolve(typeof(ILlmClientRegistry)) is not ILlmClientRegistry registry)
                {
                    return false;
                }

                client = registry.ResolveClientForRole(BuiltInAgentRoleIds.SmartChat);
                return client != null;
            }
            catch
            {
                return false;
            }
        }

        private static byte[] BuildProbePng()
        {
            Texture2D texture = new(ProbeImageSize, ProbeImageSize, TextureFormat.RGBA32, false);
            try
            {
                Color32[] pixels = new Color32[ProbeImageSize * ProbeImageSize];
                for (int i = 0; i < pixels.Length; i++)
                {
                    pixels[i] = ProbeColor;
                }

                texture.SetPixels32(pixels);
                texture.Apply(false, false);
                return ImageConversion.EncodeToPNG(texture);
            }
            finally
            {
                // WHY: Destroy is deferred and play-mode-only; DestroyImmediate covers edit-mode callers.
                if (Application.isPlaying)
                {
                    UnityEngine.Object.Destroy(texture);
                }
                else
                {
                    UnityEngine.Object.DestroyImmediate(texture);
                }
            }
        }
    }
}
