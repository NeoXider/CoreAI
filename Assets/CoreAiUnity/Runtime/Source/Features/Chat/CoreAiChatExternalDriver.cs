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
            Logging.Log.Instance.Info($"{LogPrefix} spawned (opt-in flag detected). " +
                                      "Submit prompts via SendMessage('" + DriverObjectName +
                                      "', 'SubmitPrompt', text).");
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
        /// Logs every active Graphic/Renderer whose shader reports unsupported on this device -
        /// exactly the objects the player renders as magenta. SendMessage-compatible (arg unused).
        /// </summary>
        public void DumpUnsupportedShaders(string _ = null)
        {
            int hits = 0;
            foreach (UnityEngine.UI.Graphic g in FindObjectsByType<UnityEngine.UI.Graphic>(
                         FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                Material m = g.materialForRendering;
                if (m != null && m.shader != null && !m.shader.isSupported)
                {
                    hits++;
                    Logging.Log.Instance.Warn(
                        $"{LogPrefix} [shader-diag] UNSUPPORTED {BuildPath(g.transform)} mat={m.name} shader={m.shader.name}");
                }
            }

            foreach (Renderer r in FindObjectsByType<Renderer>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                foreach (Material m in r.sharedMaterials)
                {
                    if (m != null && m.shader != null && !m.shader.isSupported)
                    {
                        hits++;
                        Logging.Log.Instance.Warn(
                            $"{LogPrefix} [shader-diag] UNSUPPORTED {BuildPath(r.transform)} mat={m.name} shader={m.shader.name}");
                    }
                }
            }

            Logging.Log.Instance.Info($"{LogPrefix} [shader-diag] done, unsupported={hits}");

            static string BuildPath(Transform t)
            {
                return t.parent == null ? t.name : BuildPath(t.parent) + "/" + t.name;
            }
        }

        /// <summary>
        /// Retired MoonSharp WebGL "null function" diagnostic. The MoonSharp VM has been removed in favor
        /// of the managed, AOT-safe Lua-CSharp runtime (which does not exhibit the wasm-trap failure this
        /// probe was built to isolate), so this SendMessage hook is now a logged no-op. Lua execution is
        /// driven through the CoreAIMods (Lua-CSharp) stack.
        /// </summary>
        public void RunLuaDiag(string _ = null)
        {
            Logging.Log.Instance.Info(
                $"{LogPrefix} RunLuaDiag retired: MoonSharp removed; Lua now runs on the Lua-CSharp runtime.");
        }

        /// <summary>
        /// Switches the LLM backend at runtime via <see cref="CoreAI.CoreAiBackend"/>.
        /// SendMessage-compatible: takes one JSON string
        /// <c>{"baseUrl":"...","apiKey":"...","model":"...","maxTokens":4096,"temperature":0.7,"timeoutSeconds":120}</c>
        /// (the last three optional; providers like Groq count <c>max_tokens</c> toward per-minute
        /// token limits, so capping it per backend matters) and calls
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
                float? temperature = (float?)o["temperature"];
                int? timeoutSeconds = (int?)o["timeoutSeconds"];
                int? maxTokens = (int?)o["maxTokens"];
                bool hotSwapped = CoreAiBackend.ApplyHttpApi(
                    baseUrl, apiKey, model, temperature, timeoutSeconds, maxTokens);
                Logging.Log.Instance.Info($"{LogPrefix} backend-applied: baseUrl={baseUrl} model={model} " +
                                          $"maxTokens={(maxTokens.HasValue ? maxTokens.Value.ToString() : "keep")} " +
                                          $"hotSwapped={hotSwapped} keyLen={apiKey.Length}");
            }
            catch (Exception ex)
            {
                Logging.Log.Instance.Warn($"{LogPrefix} backend-failed: {ex.GetType().Name}: {ex.Message}");
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
                Logging.Log.Instance.Warn($"{LogPrefix} SubmitPrompt ignored: no CoreAiChatPanel in the scene.");
                return;
            }

            Logging.Log.Instance.Info(
                $"{LogPrefix} submitting prompt ({prompt?.Length ?? 0} chars) to '{panel.name}'.");
            SubmitAndReportAsync(panel, prompt).Forget();
        }

        private static async Task SubmitAndReportAsync(CoreAiChatPanel panel, string prompt)
        {
            try
            {
                string response = await panel.SubmitMessageFromExternalAsync(prompt);
                if (response == null)
                {
                    Logging.Log.Instance.Warn($"{LogPrefix} turn-failed: panel busy, canceled, or empty input.");
                }
                else
                {
                    Logging.Log.Instance.Info($"{LogPrefix} turn-complete ({response.Length} chars): {response}");
                }
            }
            catch (Exception ex)
            {
                Logging.Log.Instance.Warn($"{LogPrefix} turn-failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    internal static class CoreAiChatExternalDriverTaskExtensions
    {
        /// <summary>Fire-and-forget with observed exceptions (avoids unobserved-task noise).</summary>
        public static void Forget(this Task task)
        {
            task.ContinueWith(
                t => Logging.Log.Instance.Warn(
                    $"[CoreAiChatExternalDriver] unobserved failure: {t.Exception?.GetBaseException().Message}"),
                TaskContinuationOptions.OnlyOnFaulted);
        }
    }
}