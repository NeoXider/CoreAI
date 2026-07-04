using System;
using System.Threading.Tasks;
using CoreAI.Infrastructure.Logging;
using UnityEngine;

namespace CoreAI.Chat
{
    /// <summary>
    /// SendMessage-compatible bridge for driving <see cref="CoreAiChatPanel"/> from host JavaScript
    /// in a WebGL player (or from any external harness that only has string entry points).
    /// Synthetic DOM events never reach Unity 6's Input System, so browser-side automation cannot
    /// click the chat UI; this driver is the supported way to submit a prompt from page JS:
    /// <c>unityInstance.SendMessage('CoreAiChatExternalDriver', 'SubmitPrompt', 'text')</c>.
    /// The turn result is logged to the console (and the browser console in WebGL), so headless
    /// harnesses can assert on the log stream alone.
    ///
    /// The driver spawns itself ONLY when the page URL contains <c>coreai-external-driver=1</c>
    /// (WebGL) or the environment variable <c>COREAI_EXTERNAL_DRIVER=1</c> is set (other players),
    /// so shipping builds are unaffected unless explicitly opted in.
    /// </summary>
    public sealed class CoreAiChatExternalDriver : MonoBehaviour
    {
        /// <summary>Fixed GameObject name so SendMessage targeting never guesses.</summary>
        public const string DriverObjectName = "CoreAiChatExternalDriver";

        private const string LogPrefix = "[CoreAiChatExternalDriver]";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoSpawnWhenOptedIn()
        {
            if (!IsOptedIn())
            {
                return;
            }

            GameObject go = new(DriverObjectName);
            DontDestroyOnLoad(go);
            go.AddComponent<CoreAiChatExternalDriver>();
            Debug.Log($"{LogPrefix} spawned (opt-in flag detected). " +
                      "Submit prompts via SendMessage('" + DriverObjectName + "', 'SubmitPrompt', text).");
        }

        private static bool IsOptedIn()
        {
            string url = Application.absoluteURL ?? string.Empty;
            if (url.IndexOf("coreai-external-driver=1", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            try
            {
                return Environment.GetEnvironmentVariable("COREAI_EXTERNAL_DRIVER") == "1";
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Switches the LLM backend at runtime via <see cref="CoreAI.CoreAiBackend"/>.
        /// SendMessage-compatible: takes one JSON string
        /// <c>{"baseUrl":"...","apiKey":"...","model":"..."}</c> and calls
        /// <c>CoreAiBackend.ApplyHttpApi</c>. The outcome is logged (<c>backend-applied</c> /
        /// <c>backend-failed</c>); the API key is never echoed.
        /// </summary>
        public void ApplyBackendJson(string json)
        {
            try
            {
                Newtonsoft.Json.Linq.JObject o = Newtonsoft.Json.Linq.JObject.Parse(json ?? "{}");
                string baseUrl = (string)o["baseUrl"] ?? "";
                string apiKey = (string)o["apiKey"] ?? "";
                string model = (string)o["model"] ?? "";
                bool hotSwapped = CoreAI.CoreAiBackend.ApplyHttpApi(baseUrl, apiKey, model);
                Debug.Log($"{LogPrefix} backend-applied: baseUrl={baseUrl} model={model} " +
                          $"hotSwapped={hotSwapped} keyLen={apiKey.Length}");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{LogPrefix} backend-failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// Submits <paramref name="prompt"/> to the first active <see cref="CoreAiChatPanel"/> in the
        /// scene through the same turn pipeline the UI uses (streaming, tool calls, history).
        /// SendMessage-compatible: one string parameter, void return; completion is reported via logs
        /// (<c>turn-complete</c> with the assistant text, or <c>turn-failed</c>).
        /// </summary>
        public void SubmitPrompt(string prompt)
        {
            CoreAiChatPanel panel = FindFirstObjectByType<CoreAiChatPanel>();
            if (panel == null)
            {
                Debug.LogWarning($"{LogPrefix} SubmitPrompt ignored: no CoreAiChatPanel in the scene.");
                return;
            }

            Debug.Log($"{LogPrefix} submitting prompt ({prompt?.Length ?? 0} chars) to '{panel.name}'.");
            SubmitAndReportAsync(panel, prompt).Forget();
        }

        private static async Task SubmitAndReportAsync(CoreAiChatPanel panel, string prompt)
        {
            try
            {
                string response = await panel.SubmitMessageFromExternalAsync(prompt);
                if (response == null)
                {
                    Debug.LogWarning($"{LogPrefix} turn-failed: panel busy, canceled, or empty input.");
                }
                else
                {
                    Debug.Log($"{LogPrefix} turn-complete ({response.Length} chars): {response}");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{LogPrefix} turn-failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    internal static class CoreAiChatExternalDriverTaskExtensions
    {
        /// <summary>Fire-and-forget with observed exceptions (avoids unobserved-task noise).</summary>
        public static void Forget(this Task task)
        {
            task.ContinueWith(
                t => Debug.LogWarning($"[CoreAiChatExternalDriver] unobserved failure: {t.Exception?.GetBaseException().Message}"),
                TaskContinuationOptions.OnlyOnFaulted);
        }
    }
}
